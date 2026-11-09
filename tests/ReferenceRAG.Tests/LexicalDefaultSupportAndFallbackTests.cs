using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Business.Features.Search.Adapters;
using ReferenceRAG.Business.Features.Search.StateMachine;
using ReferenceRAG.Business.Features.Search.Strategies;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.Resilience;
using Xunit;

namespace ReferenceRAG.Tests;

public class LexicalDefaultSupportAndFallbackTests
{
    private class FakeBm25Adapter : IBM25RetrievalAdapter
    {
        public Task<List<BM25SearchResult>> SearchAsync(
            string query, int topK, float k1 = 1.5f, float b = 0.75f, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new List<BM25SearchResult>
            {
                new() { ChunkId = "c1", Content = "张三丰太极拳", Score = 12.5, Rank = 1 },
                new() { ChunkId = "c2", Content = "令狐冲独孤九剑", Score = 8.2, Rank = 2 }
            });
        }
    }

    private class FailingStrategy : ISearchExecutionStrategy
    {
        public QueryMode Mode => QueryMode.HybridRerank;

        public Task<SearchStrategyResult> ExecuteAsync(
            SearchExecutionContext context, CancellationToken cancellationToken = default)
        {
            throw new HttpRequestException("Cloud Embedding API timeout 429 Too Many Requests");
        }
    }

    [Fact]
    public async Task LexicalSearchStrategy_ExecutesAndNormalizesScores()
    {
        var strategy = new LexicalSearchStrategy();
        var context = new SearchExecutionContext
        {
            Request = new AIQueryRequest { Query = "武功", TopK = 5, Mode = QueryMode.Lexical },
            BM25Adapter = new FakeBm25Adapter(),
            StateMachine = new SearchPipelineStateMachine("Lexical")
        };

        var result = await strategy.ExecuteAsync(context);

        Assert.NotNull(result);
        Assert.Equal(2, result.Results.Count);
        Assert.All(result.Results, r => Assert.True(r.Score >= 0.5 && r.Score <= 1.0));
    }

    [Fact]
    public async Task FallbackResilientSearchStrategy_SmoothlyDegradesWhenPrimaryFails()
    {
        var primary = new FailingStrategy();
        var fallback = new LexicalSearchStrategy();
        var cb = new ResilienceCircuitBreaker(failureThreshold: 3);

        var resilient = new FallbackResilientSearchStrategy(primary, fallback, cb);

        var context = new SearchExecutionContext
        {
            Request = new AIQueryRequest { Query = "武功", TopK = 5, Mode = QueryMode.HybridRerank },
            BM25Adapter = new FakeBm25Adapter(),
            Config = new ObsidianRagConfig(),
            StateMachine = new SearchPipelineStateMachine("HybridRerank")
        };

        var result = await resilient.ExecuteAsync(context);

        Assert.NotNull(result);
        Assert.Equal(2, result.Results.Count);
        Assert.True(context.StateMachine.IsDegraded);
        Assert.Contains("向量服务异常", context.StateMachine.DegradeReason);
    }

    [Fact]
    public async Task FallbackResilientSearchStrategy_DirectlyDegradesWhenEmbeddingDisabled()
    {
        var primary = new FailingStrategy();
        var fallback = new LexicalSearchStrategy();
        var cb = new ResilienceCircuitBreaker(failureThreshold: 3);

        var resilient = new FallbackResilientSearchStrategy(primary, fallback, cb);

        var config = new ObsidianRagConfig();
        config.Indexing.EmbeddingEnabled = false;

        var context = new SearchExecutionContext
        {
            Request = new AIQueryRequest { Query = "武功", TopK = 5, Mode = QueryMode.HybridRerank },
            BM25Adapter = new FakeBm25Adapter(),
            Config = config,
            StateMachine = new SearchPipelineStateMachine("HybridRerank")
        };

        var result = await resilient.ExecuteAsync(context);

        Assert.NotNull(result);
        Assert.Equal(2, result.Results.Count);
        Assert.True(context.StateMachine.IsDegraded);
        Assert.Contains("全局配置已禁用", context.StateMachine.DegradeReason);
    }
}
