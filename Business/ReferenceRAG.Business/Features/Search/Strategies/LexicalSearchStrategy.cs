using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Business.Features.Search.Services;
using ReferenceRAG.Business.Features.Search.StateMachine;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Search.Strategies;

/// <summary>
/// 纯词法检索策略 (零向量模型依赖，基于 FTS5 BM25 + jieba.NET + DPP 多样性采样)
/// </summary>
public class LexicalSearchStrategy : ISearchExecutionStrategy
{
    private readonly Func<IBM25Store>? _bm25StoreFactory;

    public LexicalSearchStrategy(Func<IBM25Store>? bm25StoreFactory = null)
    {
        _bm25StoreFactory = bm25StoreFactory;
    }

    public QueryMode Mode => QueryMode.Lexical;

    public async Task<SearchStrategyResult> ExecuteAsync(
        SearchExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var sm = context.StateMachine;
        sm.TransitionTo(SearchPipelinePhase.QueryPrepared, 0, 1, "词法检索准备");
        sm.TransitionTo(SearchPipelinePhase.ParallelRetrieving, 1, context.Request.TopK, "执行本地高速词法检索 (FTS5 BM25)");

        int candidateLimit = Math.Max(context.Request.TopK * 4, 30);
        List<BM25SearchResult> bm25Results;

        if (context.BM25Adapter != null)
        {
            bm25Results = await context.BM25Adapter.SearchAsync(context.Request.Query, candidateLimit, cancellationToken: cancellationToken);
        }
        else if (_bm25StoreFactory != null)
        {
            bm25Results = await _bm25StoreFactory().SearchAsync(context.Request.Query, candidateLimit);
        }
        else
        {
            bm25Results = new List<BM25SearchResult>();
        }

        // 过滤空内容与分隔符噪声
        var validCandidates = bm25Results
            .Where(r => !string.IsNullOrWhiteSpace(r.Content) && !MarkdownChunker.IsNoiseOrSeparator(r.Content))
            .ToList();

        // 执行 DPP 免向量多样性核去重，解决同文档同质分块霸榜问题
        var diversified = DppKernelDiversitySelector.Diversify(
            validCandidates,
            c => c.Content,
            c => c.Score,
            targetK: context.Request.TopK
        );

        // 评分归一化映射至 [0.55, 0.95]
        var normalizedResults = NormalizeBm25Scores(diversified);

        sm.TransitionTo(SearchPipelinePhase.AssemblingResults, bm25Results.Count, normalizedResults.Count, "词法分块装配与多样性去重就绪");

        return new SearchStrategyResult
        {
            Results = normalizedResults,
            RerankApplied = false,
            RerankStats = null
        };
    }

    private static List<SearchResult> NormalizeBm25Scores(List<BM25SearchResult> list)
    {
        if (list.Count == 0) return new List<SearchResult>();

        var maxScore = list.Max(r => r.Score);
        var minScore = list.Min(r => r.Score);
        var range = Math.Max(maxScore - minScore, 0.0001);

        return list.Select(r => new SearchResult
        {
            ChunkId = r.ChunkId,
            Content = r.Content,
            Score = (float)(0.6 + 0.35 * ((r.Score - minScore) / range)),
            BM25Score = (float)r.Score
        }).ToList();
    }
}
