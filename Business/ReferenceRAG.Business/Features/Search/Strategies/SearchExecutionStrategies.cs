using System.Diagnostics;
using ReferenceRAG.Business.Features.Search.StateMachine;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Business.Features.Search.Strategies;

/// <summary>
/// 标准向量检索策略（纯向量检索，无关键词混合，无重排）
/// </summary>
public class StandardSearchStrategy : ISearchExecutionStrategy
{
    public QueryMode Mode => QueryMode.Standard;

    public async Task<SearchStrategyResult> ExecuteAsync(
        SearchExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var sm = context.StateMachine;
        sm.TransitionTo(SearchPipelinePhase.QueryPrepared, 0, 1, "向量搜索准备");

        sm.TransitionTo(SearchPipelinePhase.ParallelRetrieving, 1, 0, "密集向量推理与查询");
        var vector = context.PrecomputedQueryVector
            ?? await context.VectorAdapter.EncodeQueryAsync(context.Request.Query, cancellationToken);

        int candidateLimit = Math.Max(context.Request.TopK * 10, 50);
        if (context.Request.Sources?.Count > 0 || context.Request.Filters?.Folders?.Count > 0)
        {
            candidateLimit = Math.Max(candidateLimit, 100);
        }

        var rawResults = await context.VectorAdapter.SearchAsync(vector, candidateLimit, cancellationToken);

        // 清洗无意义噪声与空分块
        var validResults = rawResults
            .Where(r => string.IsNullOrEmpty(r.Content) || !MarkdownChunker.IsNoiseOrSeparator(r.Content))
            .ToList();

        // 过滤禁用源与请求源
        var filteredResults = validResults
            .Where(r => string.IsNullOrEmpty(r.Source) || context.EnabledSources.Contains(r.Source))
            .ToList();

        if (context.Request.Sources?.Count > 0)
        {
            filteredResults = filteredResults.Where(r => r.Source != null && context.Request.Sources.Contains(r.Source)).ToList();
        }

        sm.TransitionTo(SearchPipelinePhase.AssemblingResults, rawResults.Count, filteredResults.Count, "分块后过滤与清洗");

        return new SearchStrategyResult
        {
            Results = filteredResults.Take(context.Request.TopK).ToList(),
            RerankApplied = false,
            RerankStats = null
        };
    }
}

/// <summary>
/// 快速检索策略（轻量级低候选数召回）
/// </summary>
public class QuickSearchStrategy : ISearchExecutionStrategy
{
    public QueryMode Mode => QueryMode.Quick;

    public async Task<SearchStrategyResult> ExecuteAsync(
        SearchExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var sm = context.StateMachine;
        sm.TransitionTo(SearchPipelinePhase.QueryPrepared, 0, 1, "快速模式检索准备");

        sm.TransitionTo(SearchPipelinePhase.ParallelRetrieving, 1, 0, "密集向量极速检索");
        var vector = context.PrecomputedQueryVector
            ?? await context.VectorAdapter.EncodeQueryAsync(context.Request.Query, cancellationToken);

        int candidateLimit = Math.Max(context.Request.TopK * 10, 50);
        if (context.Request.Sources?.Count > 0 || context.Request.Filters?.Folders?.Count > 0)
        {
            candidateLimit = Math.Max(candidateLimit, 100);
        }
        var rawResults = await context.VectorAdapter.SearchAsync(vector, candidateLimit, cancellationToken);

        var validResults = rawResults
            .Where(r => string.IsNullOrEmpty(r.Content) || !MarkdownChunker.IsNoiseOrSeparator(r.Content))
            .Where(r => string.IsNullOrEmpty(r.Source) || context.EnabledSources.Contains(r.Source))
            .ToList();

        sm.TransitionTo(SearchPipelinePhase.AssemblingResults, rawResults.Count, validResults.Count, "极速输出清洗");

        return new SearchStrategyResult
        {
            Results = validResults.Take(context.Request.TopK).ToList(),
            RerankApplied = false,
            RerankStats = null
        };
    }
}

/// <summary>
/// 混合检索策略（BM25 与向量双路召回 + 分数级融合，无重排）
/// </summary>
public class HybridSearchStrategy : ISearchExecutionStrategy
{
    private readonly IHybridSearchService _hybridSearchService;

    public HybridSearchStrategy(IHybridSearchService hybridSearchService)
    {
        _hybridSearchService = hybridSearchService;
    }

    public QueryMode Mode => QueryMode.Hybrid;

    public async Task<SearchStrategyResult> ExecuteAsync(
        SearchExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var sm = context.StateMachine;
        sm.TransitionTo(SearchPipelinePhase.QueryPrepared, 0, 1, "混合搜索准备与参数校验");

        int recallFactor = Math.Max(context.Config.Rerank.RecallFactor, 3);
        if (context.Request.Sources?.Count > 0 || context.Request.Filters?.Folders?.Count > 0)
        {
            recallFactor = Math.Max(recallFactor, 5);
        }
        int recallTopK = context.Request.TopK * recallFactor;

        sm.TransitionTo(SearchPipelinePhase.ParallelRetrieving, 1, recallTopK, "BM25与Dense向量并行双路检索");

        var hybridResults = await _hybridSearchService.SearchAsync(
            context.Request.Query,
            recallTopK,
            context.Request.K1,
            context.Request.B,
            context.Request.Filters?.Folders,
            cancellationToken,
            precomputedQueryVector: context.PrecomputedQueryVector);

        sm.TransitionTo(SearchPipelinePhase.HybridFusing, hybridResults.Count, hybridResults.Count, "加权分数归一化与排序融合");

        var topResults = hybridResults
            .Where(h => string.IsNullOrEmpty(h.Content) || !MarkdownChunker.IsNoiseOrSeparator(h.Content))
            .Where(h => string.IsNullOrEmpty(h.Source) || context.EnabledSources.Contains(h.Source))
            .Select(h => new SearchResult
            {
                ChunkId = h.ChunkId,
                FileId = h.FileId,
                FilePath = h.FilePath,
                Title = h.Title,
                Content = h.Content,
                Score = h.Score,
                BM25Score = h.BM25Score,
                EmbeddingScore = h.EmbeddingScore,
                StartLine = h.StartLine,
                EndLine = h.EndLine,
                HeadingPath = h.HeadingPath,
                Source = h.Source
            })
            .ToList();

        if (context.Request.Sources?.Count > 0)
        {
            topResults = topResults.Where(r => r.Source != null && context.Request.Sources.Contains(r.Source)).ToList();
        }

        sm.TransitionTo(SearchPipelinePhase.AssemblingResults, hybridResults.Count, topResults.Count, "去噪与TopK截断");

        return new SearchStrategyResult
        {
            Results = topResults.Take(context.Request.TopK).ToList(),
            RerankApplied = false,
            RerankStats = null
        };
    }
}

/// <summary>
/// 混合重排策略（BM25 + 向量召回 + Cross-Encoder 深度重排）
/// </summary>
public class HybridRerankSearchStrategy : ISearchExecutionStrategy
{
    private readonly IHybridSearchService _hybridSearchService;

    public HybridRerankSearchStrategy(IHybridSearchService hybridSearchService)
    {
        _hybridSearchService = hybridSearchService;
    }

    public QueryMode Mode => QueryMode.HybridRerank;

    public async Task<SearchStrategyResult> ExecuteAsync(
        SearchExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var sm = context.StateMachine;
        sm.TransitionTo(SearchPipelinePhase.QueryPrepared, 0, 1, "混合重排阶段准备");

        var rerankConfig = context.Config.Rerank;
        int recallFactor = rerankConfig.RecallFactor;
        if (context.Request.Sources?.Count > 0 || context.Request.Filters?.Folders?.Count > 0)
        {
            recallFactor = Math.Max(recallFactor, 5);
        }
        int recallTopK = context.Request.TopK * recallFactor;

        sm.TransitionTo(SearchPipelinePhase.ParallelRetrieving, 1, recallTopK, "BM25+向量双路并行召回");

        var hybridResults = await _hybridSearchService.SearchAsync(
            context.Request.Query,
            recallTopK,
            context.Request.K1,
            context.Request.B,
            context.Request.Filters?.Folders,
            cancellationToken,
            precomputedQueryVector: context.PrecomputedQueryVector);

        sm.TransitionTo(SearchPipelinePhase.HybridFusing, hybridResults.Count, hybridResults.Count, "双路结果归一化分数融合");

        var candidates = hybridResults
            .Where(h => string.IsNullOrEmpty(h.Content) || !MarkdownChunker.IsNoiseOrSeparator(h.Content))
            .Where(h => string.IsNullOrEmpty(h.Source) || context.EnabledSources.Contains(h.Source))
            .Select(h => new SearchResult
            {
                ChunkId = h.ChunkId,
                FileId = h.FileId,
                FilePath = h.FilePath,
                Title = h.Title,
                Content = h.Content,
                Score = h.Score,
                BM25Score = h.BM25Score,
                EmbeddingScore = h.EmbeddingScore,
                StartLine = h.StartLine,
                EndLine = h.EndLine,
                HeadingPath = h.HeadingPath,
                Source = h.Source
            })
            .ToList();

        if (context.Request.Sources?.Count > 0)
        {
            candidates = candidates.Where(r => r.Source != null && context.Request.Sources.Contains(r.Source)).ToList();
        }

        bool shouldRerank = context.RerankAdapter.IsAvailable && candidates.Count > 0;
        RerankStats? rerankStats = null;
        List<SearchResult> finalResults;

        if (shouldRerank)
        {
            var topN = context.Request.RerankTopN ?? rerankConfig.TopN;
            var rerankCandidateLimit = Math.Clamp(topN * Math.Max(2, rerankConfig.RecallFactor), topN, 50);

            if (candidates.Count > rerankCandidateLimit)
            {
                candidates = candidates.Take(rerankCandidateLimit).ToList();
            }

            sm.TransitionTo(SearchPipelinePhase.Reranking, candidates.Count, topN, $"Cross-Encoder 精排候选池 (Limit={candidates.Count})");

            var sw = Stopwatch.StartNew();
            var docContents = candidates.Select(c => c.Content).ToList();
            var rerankResult = await context.RerankAdapter.RerankBatchAsync(context.Request.Query, docContents, cancellationToken);

            var reranked = new List<SearchResult>();
            foreach (var doc in rerankResult.Documents.Take(topN))
            {
                if (doc.Index >= 0 && doc.Index < candidates.Count)
                {
                    var orig = candidates[doc.Index];
                    var score = doc.RelevanceScore;
                    if (double.IsNaN(score) || double.IsInfinity(score)) score = 0.5;
                    orig.RerankScore = score;
                    orig.Score = (float)score;
                    reranked.Add(orig);
                }
            }

            if (rerankConfig.ScoreThreshold > 0)
            {
                reranked = reranked.Where(r => r.RerankScore >= rerankConfig.ScoreThreshold).ToList();
            }

            sw.Stop();

            double avgScore = reranked.Count > 0 ? reranked.Average(r => r.RerankScore ?? 0.0) : 0.0;
            rerankStats = new RerankStats
            {
                CandidatesCount = candidates.Count,
                RerankDurationMs = sw.ElapsedMilliseconds,
                ModelName = context.RerankAdapter.ModelInfo?.ModelName ?? "Reranker",
                AverageScore = avgScore
            };

            finalResults = reranked;
        }
        else
        {
            finalResults = candidates.Take(context.Request.TopK).ToList();
        }

        sm.TransitionTo(SearchPipelinePhase.AssemblingResults, candidates.Count, finalResults.Count, "重排结果清洗与装配");

        return new SearchStrategyResult
        {
            Results = finalResults,
            RerankApplied = shouldRerank,
            RerankStats = rerankStats
        };
    }
}

/// <summary>
/// 深度混合重排策略（BM25 + 向量海量双路召回 + 深度 Cross-Encoder 精排）
/// </summary>
public class DeepSearchStrategy : ISearchExecutionStrategy
{
    private readonly IHybridSearchService _hybridSearchService;

    public DeepSearchStrategy(IHybridSearchService hybridSearchService)
    {
        _hybridSearchService = hybridSearchService;
    }

    public QueryMode Mode => QueryMode.Deep;

    public async Task<SearchStrategyResult> ExecuteAsync(
        SearchExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var sm = context.StateMachine;
        sm.TransitionTo(SearchPipelinePhase.QueryPrepared, 0, 1, "深度搜索阶段准备");

        var rerankConfig = context.Config.Rerank;
        // Deep 模式深度召回：候选倍率扩大到至少 10 倍 (TopK 20 -> 200 候选)
        int recallFactor = Math.Max(rerankConfig.RecallFactor, 10);
        if (context.Request.Sources?.Count > 0 || context.Request.Filters?.Folders?.Count > 0)
        {
            recallFactor = Math.Max(recallFactor, 10);
        }
        int recallTopK = context.Request.TopK * recallFactor;

        sm.TransitionTo(SearchPipelinePhase.ParallelRetrieving, 1, recallTopK, "BM25+向量双路高倍率深度召回");

        var hybridResults = await _hybridSearchService.SearchAsync(
            context.Request.Query,
            recallTopK,
            context.Request.K1,
            context.Request.B,
            context.Request.Filters?.Folders,
            cancellationToken,
            precomputedQueryVector: context.PrecomputedQueryVector);

        sm.TransitionTo(SearchPipelinePhase.HybridFusing, hybridResults.Count, hybridResults.Count, "海量候选双路加权归一化融合");

        var candidates = hybridResults
            .Where(h => string.IsNullOrEmpty(h.Content) || !MarkdownChunker.IsNoiseOrSeparator(h.Content))
            .Where(h => string.IsNullOrEmpty(h.Source) || context.EnabledSources.Contains(h.Source))
            .Select(h => new SearchResult
            {
                ChunkId = h.ChunkId,
                FileId = h.FileId,
                FilePath = h.FilePath,
                Title = h.Title,
                Content = h.Content,
                Score = h.Score,
                BM25Score = h.BM25Score,
                EmbeddingScore = h.EmbeddingScore,
                StartLine = h.StartLine,
                EndLine = h.EndLine,
                HeadingPath = h.HeadingPath,
                Source = h.Source
            })
            .ToList();

        if (context.Request.Sources?.Count > 0)
        {
            candidates = candidates.Where(r => r.Source != null && context.Request.Sources.Contains(r.Source)).ToList();
        }

        bool shouldRerank = context.RerankAdapter.IsAvailable && candidates.Count > 0;
        RerankStats? rerankStats = null;
        List<SearchResult> finalResults;

        if (shouldRerank)
        {
            var topN = context.Request.RerankTopN ?? Math.Max(context.Request.TopK, rerankConfig.TopN);
            var rerankCandidateLimit = Math.Clamp(Math.Max(topN * 3, 50), topN, 100);

            if (candidates.Count > rerankCandidateLimit)
            {
                candidates = candidates.Take(rerankCandidateLimit).ToList();
            }

            sm.TransitionTo(SearchPipelinePhase.Reranking, candidates.Count, topN, $"Cross-Encoder 深度重排候选池 (Limit={candidates.Count})");

            var sw = Stopwatch.StartNew();
            var docContents = candidates.Select(c => c.Content).ToList();
            var rerankResult = await context.RerankAdapter.RerankBatchAsync(context.Request.Query, docContents, cancellationToken);

            var reranked = new List<SearchResult>();
            foreach (var doc in rerankResult.Documents.Take(topN))
            {
                if (doc.Index >= 0 && doc.Index < candidates.Count)
                {
                    var orig = candidates[doc.Index];
                    var score = doc.RelevanceScore;
                    if (double.IsNaN(score) || double.IsInfinity(score)) score = 0.5;
                    orig.RerankScore = score;
                    orig.Score = (float)score;
                    reranked.Add(orig);
                }
            }

            if (rerankConfig.ScoreThreshold > 0)
            {
                reranked = reranked.Where(r => r.RerankScore >= rerankConfig.ScoreThreshold).ToList();
            }

            sw.Stop();

            double avgScore = reranked.Count > 0 ? reranked.Average(r => r.RerankScore ?? 0.0) : 0.0;
            rerankStats = new RerankStats
            {
                CandidatesCount = candidates.Count,
                RerankDurationMs = sw.ElapsedMilliseconds,
                ModelName = context.RerankAdapter.ModelInfo?.ModelName ?? "Reranker",
                AverageScore = avgScore
            };

            finalResults = reranked;
        }
        else
        {
            finalResults = candidates.Take(context.Request.TopK).ToList();
        }

        sm.TransitionTo(SearchPipelinePhase.AssemblingResults, candidates.Count, finalResults.Count, "深度重排结果装配与去偏");

        return new SearchStrategyResult
        {
            Results = finalResults,
            RerankApplied = shouldRerank,
            RerankStats = rerankStats
        };
    }
}

