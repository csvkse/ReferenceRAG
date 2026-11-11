using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Business.Features.Search.Cognitive;
using ReferenceRAG.Business.Features.Search.Services;
using ReferenceRAG.Business.Features.Search.StateMachine;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Business.Features.Search.Strategies;

/// <summary>
/// 认知增强词法检索策略
/// 全面面向单轨物理知识库 (IKnowledgeBaseSearchService)，利用适配器策略模式解耦认知后端 (Jev/Cross-Encoder/Statistical)，
/// 并基于状态机治理 Hop 0 粗筛 -> 认知判别 -> 早停截断 -> 实体多跳 -> DPP 稀疏核多样性去重。
/// </summary>
public sealed class CognitiveEnhancedLexicalStrategy : ISearchExecutionStrategy
{
    private readonly IKnowledgeBaseSearchService? _kbSearchService;
    private readonly ICatalogStore? _catalogStore;
    private readonly ICognitiveEvidenceEvaluator _evaluator;
    private readonly CognitiveLexicalConfig _config;
    private readonly Func<IBM25Store>? _bm25StoreFactory;
    private readonly ILogger<CognitiveEnhancedLexicalStrategy>? _logger;

    public QueryMode Mode => QueryMode.Lexical;

    public CognitiveEnhancedLexicalStrategy(
        IKnowledgeBaseSearchService? kbSearchService,
        ICatalogStore? catalogStore,
        ICognitiveEvidenceEvaluator evaluator,
        CognitiveLexicalConfig config,
        Func<IBM25Store>? bm25StoreFactory = null,
        ILogger<CognitiveEnhancedLexicalStrategy>? logger = null)
    {
        _kbSearchService = kbSearchService;
        _catalogStore = catalogStore;
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _config = config ?? new CognitiveLexicalConfig();
        _bm25StoreFactory = bm25StoreFactory;
        _logger = logger;
    }

    public async Task<SearchStrategyResult> ExecuteAsync(
        SearchExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var sm = context.StateMachine;
        sm.TransitionTo(SearchPipelinePhase.QueryPrepared, 0, 1, $"认知增强词法检索准备 (后端: {_evaluator.BackendName})");

        int initialLimit = Math.Max(context.Request.TopK * 3, 30);
        sm.TransitionTo(SearchPipelinePhase.ParallelRetrieving, 1, initialLimit, "Hop 0 初始词法检索 (FTS5)");

        // ================= Hop 0: 权威知识库词法初筛 =================
        var hop0Candidates = await RetrieveHop0CandidatesAsync(context.Request, initialLimit, cancellationToken);
        var validHop0 = hop0Candidates
            .Where(h => !string.IsNullOrWhiteSpace(h.Content) && !MarkdownChunker.IsNoiseOrSeparator(h.Content))
            .ToList();

        if (validHop0.Count == 0)
        {
            sm.TransitionTo(SearchPipelinePhase.AssemblingResults, 0, 0, "无匹配词法候选分块");
            return new SearchStrategyResult { Results = new List<SearchResult>() };
        }

        // 锁定 Hop 0 Top 3 作为保底受保护种子 (Protected Seeds)，杜绝全杀致空
        var protectedSeeds = validHop0.Take(3).ToDictionary(h => h.ChunkId, h => h, StringComparer.OrdinalIgnoreCase);

        // ================= Hop 0: 认知后端证据判别 =================
        sm.TransitionTo(SearchPipelinePhase.CognitiveScoringHop0, validHop0.Count, validHop0.Count, $"{_evaluator.BackendName} 证据判定 (Hop 0)");
        
        int batchLimit = Math.Max(_config.BatchSize > 0 ? _config.BatchSize : 10, 10);
        var candidatesToEval = validHop0.Take(batchLimit).ToList();

        var evals0 = await _evaluator.EvaluateBatchAsync(
            context.Request.Query,
            candidatesToEval,
            previouslyRetrievedEvidence: Array.Empty<CandidateEvaluationItem>(),
            cancellationToken);

        var retainedEvidence = new List<CandidateEvaluationItem>();
        var retainedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidatesToEval)
        {
            if (evals0.TryGetValue(c.ChunkId, out var res) && res.IsAccepted)
            {
                if (retainedIds.Add(c.ChunkId))
                {
                    retainedEvidence.Add(c with { PreviousScore = res.NormalizedScore });
                }
            }
            else if (protectedSeeds.ContainsKey(c.ChunkId))
            {
                if (retainedIds.Add(c.ChunkId))
                {
                    retainedEvidence.Add(c with { PreviousScore = 0.50 });
                }
            }
        }

        // 若前序评估后证据依然不足 TopK，补充未进入批处理的靠前候选分块
        if (retainedEvidence.Count < context.Request.TopK && validHop0.Count > batchLimit)
        {
            foreach (var remaining in validHop0.Skip(batchLimit))
            {
                if (retainedIds.Add(remaining.ChunkId))
                {
                    retainedEvidence.Add(remaining with { PreviousScore = 0.45 });
                    if (retainedEvidence.Count >= context.Request.TopK) break;
                }
            }
        }

        // 早停检测：若命中高置信度或配置单跳，立即组装输出
        bool canEarlyExit = retainedEvidence.Any(e => (e.PreviousScore ?? 0.0) >= _config.EarlyExitThreshold);
        if (_config.MaxHops <= 1 || (canEarlyExit && retainedEvidence.Count >= context.Request.TopK))
        {
            return AssembleFinalResults(retainedEvidence, context, sm, sw.ElapsedMilliseconds);
        }

        // ================= Hop 1: 专有名词抽取与多跳交替扩展 =================
        sm.TransitionTo(SearchPipelinePhase.MultiHopExpanding, retainedEvidence.Count, 0, "实体抽取与多跳扩展");
        var queryTerms = new HashSet<string>(
            TokenizationCacheProxy.Default.Tokenize(context.Request.Query),
            StringComparer.OrdinalIgnoreCase);
        var seeds = retainedEvidence.Select(e => (e.Content, e.PreviousScore ?? 0.5)).ToList();
        var expansionTerms = LexicalEntityExtractor.BuildExpansionTerms(seeds, queryTerms, limit: 5);

        if (expansionTerms.Count > 0)
        {
            var multiHopQuery = $"{string.Join(" OR ", expansionTerms.Select(t => $"\"{t}\""))} OR ({context.Request.Query})";
            var hop1Candidates = await RetrieveHopCandidatesByQueryAsync(
                context.Request,
                multiHopQuery,
                limit: context.Request.TopK * 2,
                cancellationToken);

            var newHop1Candidates = hop1Candidates
                .Where(h => !retainedIds.Contains(h.ChunkId))
                .Where(h => !MarkdownChunker.IsNoiseOrSeparator(h.Content))
                .Take(batchLimit)
                .ToList();

            if (newHop1Candidates.Count > 0)
            {
                sm.TransitionTo(SearchPipelinePhase.CognitiveScoringHop1, newHop1Candidates.Count, newHop1Candidates.Count, $"{_evaluator.BackendName} 联合证据评估 (Hop 1)");
                var evals1 = await _evaluator.EvaluateBatchAsync(
                    context.Request.Query,
                    newHop1Candidates,
                    previouslyRetrievedEvidence: retainedEvidence,
                    cancellationToken);

                foreach (var hc in newHop1Candidates)
                {
                    if (evals1.TryGetValue(hc.ChunkId, out var r) && r.IsAccepted)
                    {
                        if (retainedIds.Add(hc.ChunkId))
                        {
                            retainedEvidence.Add(hc with { PreviousScore = r.NormalizedScore });
                        }
                    }
                }
            }
        }

        // ================= 最终阶段：证据池精化与 DPP 多样性截断 =================
        return AssembleFinalResults(retainedEvidence, context, sm, sw.ElapsedMilliseconds);
    }

    private async Task<List<CandidateEvaluationItem>> RetrieveHop0CandidatesAsync(
        AIQueryRequest request,
        int limit,
        CancellationToken ct)
    {
        return await RetrieveHopCandidatesByQueryAsync(request, request.Query, limit, ct);
    }

    private async Task<List<CandidateEvaluationItem>> RetrieveHopCandidatesByQueryAsync(
        AIQueryRequest request,
        string query,
        int limit,
        CancellationToken ct)
    {
        var items = new List<CandidateEvaluationItem>();

        // 1. 优先以单轨物理知识库为权威底座检索
        if (_kbSearchService != null && _catalogStore != null)
        {
            try
            {
                List<string> targetKbIds;
                if (request.KnowledgeBaseIds != null && request.KnowledgeBaseIds.Count > 0)
                {
                    targetKbIds = request.KnowledgeBaseIds;
                }
                else
                {
                    var allKbs = await _catalogStore.ListKnowledgeBasesAsync(ct);
                    targetKbIds = allKbs.Where(k => k.Enabled).Select(k => k.Id).ToList();
                }

                if (targetKbIds.Count > 0)
                {
                    var kbResponse = await _kbSearchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
                        Query: query,
                        TopK: limit,
                        KnowledgeBaseIds: targetKbIds,
                        QueryVector: null,
                        UseLexicalTokenizer: true,
                        FailurePolicy: FailurePolicy.AllowPartial,
                        AllowDegrade: true
                    ), ct);

                    if (kbResponse.Items != null && kbResponse.Items.Count > 0)
                    {
                        foreach (var item in kbResponse.Items)
                        {
                            var title = Path.GetFileNameWithoutExtension(item.SourceKey);
                            items.Add(new CandidateEvaluationItem(
                                ChunkId: item.ChunkId,
                                Title: title,
                                Heading: item.HeadingPath ?? "",
                                Content: item.Text,
                                Bm25Score: item.Score,
                                PreviousScore: null,
                                FileId: item.DocumentId,
                                FilePath: item.SourceKey,
                                Source: item.ConnectorId ?? "knowledge-base",
                                StartLine: item.StartLine,
                                EndLine: item.EndLine,
                                HeadingPath: item.HeadingPath,
                                Locator: item.Locator
                            ));
                        }
                        return items;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[CognitiveEnhancedLexicalStrategy] 知识库词法初筛异常: {Message}", ex.Message);
            }
        }

        // 2. 若知识库无数据且旧 BM25 工厂存在，执行旧库保底
        if (items.Count == 0 && _bm25StoreFactory != null)
        {
            try
            {
                var bm25Store = _bm25StoreFactory();
                var bm25Results = await bm25Store.SearchAsync(query, limit);
                foreach (var bm in bm25Results)
                {
                    items.Add(new CandidateEvaluationItem(
                        ChunkId: bm.ChunkId,
                        Title: "本地文档",
                        Heading: "",
                        Content: bm.Content,
                        Bm25Score: bm.Score
                    ));
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[CognitiveEnhancedLexicalStrategy] 旧版 BM25 检索保底失败: {Message}", ex.Message);
            }
        }

        return items;
    }

    private SearchStrategyResult AssembleFinalResults(
        List<CandidateEvaluationItem> evidencePool,
        SearchExecutionContext context,
        SearchPipelineStateMachine sm,
        long elapsedMs)
    {
        sm.TransitionTo(SearchPipelinePhase.EvidencePoolRefining, evidencePool.Count, evidencePool.Count, "证据池去重与打分排序");

        // 排序确保高质量证据优先
        var sortedEvidence = evidencePool
            .OrderByDescending(e => e.PreviousScore ?? 0.5)
            .ThenByDescending(e => e.Bm25Score)
            .ToList();

        sm.TransitionTo(SearchPipelinePhase.DppDiversitySelecting, sortedEvidence.Count, context.Request.TopK, "DPP 核多样性去重");

        var diversified = DppKernelDiversitySelector.Diversify(
            sortedEvidence,
            textSelector: e => e.Content,
            scoreSelector: e => e.PreviousScore ?? (Math.Abs(e.Bm25Score) / 100.0),
            targetK: context.Request.TopK);

        sm.TransitionTo(SearchPipelinePhase.AssemblingResults, diversified.Count, diversified.Count, "输出认知增强证据集");

        var results = diversified.Select(e => new SearchResult
        {
            ChunkId = e.ChunkId,
            FileId = e.FileId ?? string.Empty,
            FilePath = e.FilePath ?? string.Empty,
            Source = e.Source,
            Title = e.Title,
            Content = e.Content,
            Score = (float)(e.PreviousScore ?? 0.7),
            BM25Score = (float)e.Bm25Score,
            StartLine = e.StartLine,
            EndLine = e.EndLine,
            HeadingPath = e.HeadingPath,
            Locator = e.Locator
        }).ToList();

        return new SearchStrategyResult
        {
            Results = results,
            RerankApplied = true,
            RerankStats = new RerankStats
            {
                ModelName = $"Cognitive-{_evaluator.BackendName}",
                RerankDurationMs = elapsedMs
            }
        };
    }
}
