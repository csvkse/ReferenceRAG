using Moq;
using ReferenceRAG.Business.Features.Search.Adapters;
using ReferenceRAG.Business.Features.Search.StateMachine;
using ReferenceRAG.Business.Features.Search.Strategies;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Tests;

public class SearchPipelineStrategyTests
{
    [Fact]
    public void SearchPipelineStateMachine_TracksPhasesAndTelemetryCorrectly()
    {
        var sm = new SearchPipelineStateMachine("HybridRerank");
        sm.EmbeddingModel = new ModelExecutionInfo
        {
            ModelName = "bge-small-zh-v1.5",
            Variant = "Q4_K_M",
            Format = "gguf",
            Device = "CUDA 0 (GPU)",
            IsGpu = true,
            State = "Ready"
        };
        sm.RerankModel = new ModelExecutionInfo
        {
            ModelName = "bge-reranker-large",
            Variant = "Q8_0",
            Format = "gguf",
            Device = "CUDA 0 (GPU)",
            IsGpu = true,
            State = "Ready"
        };

        // Transition through stages
        sm.TransitionTo(SearchPipelinePhase.QueryPrepared, 0, 1, "请求接收");
        Assert.Equal(SearchPipelinePhase.QueryPrepared, sm.CurrentPhase);

        sm.TransitionTo(SearchPipelinePhase.ParallelRetrieving, 0, 1, "查询分析准备完成");
        Assert.Equal(SearchPipelinePhase.ParallelRetrieving, sm.CurrentPhase);

        sm.TransitionTo(SearchPipelinePhase.HybridFusing, 1, 40, "BM25与向量并行双路检索完成");
        Assert.Equal(SearchPipelinePhase.HybridFusing, sm.CurrentPhase);

        sm.TransitionTo(SearchPipelinePhase.Reranking, 40, 20, "分数加权归一化融合完成");
        Assert.Equal(SearchPipelinePhase.Reranking, sm.CurrentPhase);

        sm.TransitionTo(SearchPipelinePhase.AssemblingResults, 20, 10, "Cross-Encoder 重排精排完成");
        Assert.Equal(SearchPipelinePhase.AssemblingResults, sm.CurrentPhase);

        var telemetry = sm.Complete(10);
        Assert.Equal(SearchPipelinePhase.Completed, sm.CurrentPhase);

        Assert.NotNull(telemetry);
        Assert.Equal("HybridRerank", telemetry.StrategyName);
        Assert.NotNull(telemetry.EmbeddingModel);
        Assert.Equal("Q4_K_M", telemetry.EmbeddingModel.Variant);
        Assert.True(telemetry.EmbeddingModel.IsGpu);
        Assert.NotNull(telemetry.RerankModel);
        Assert.Equal("bge-reranker-large", telemetry.RerankModel.ModelName);

        Assert.NotEmpty(telemetry.Steps);
        Assert.Contains(telemetry.Steps, s => s.Phase == SearchPipelinePhase.ParallelRetrieving.ToString() && s.OutputCandidates == 40);
        Assert.Contains(telemetry.Steps, s => s.Phase == SearchPipelinePhase.Reranking.ToString() && s.OutputCandidates == 10);
    }

    [Theory]
    [InlineData(QueryMode.Standard, typeof(StandardSearchStrategy))]
    [InlineData(QueryMode.Quick, typeof(QuickSearchStrategy))]
    [InlineData(QueryMode.Deep, typeof(StandardSearchStrategy))] // 当没有 hybrid 时回退到 Standard
    [InlineData(QueryMode.Hybrid, typeof(StandardSearchStrategy))] // 当没有 hybrid 时回退到 Standard
    public void SearchStrategyFactory_ResolvesCorrectStrategy_WithoutHybrid(QueryMode mode, Type expectedType)
    {
        var factory = new SearchStrategyFactory(null);
        var strategy = factory.GetStrategy(mode);

        Assert.NotNull(strategy);
        Assert.IsType(expectedType, strategy);
    }

    [Fact]
    public void DefensiveChunkHygiene_FiltersSeparatorAndWhitespaceInCandidates()
    {
        var candidates = new List<SearchResult>
        {
            new() { ChunkId = "1", Content = "这是第一条正常有意义的正文内容", Score = 0.9f },
            new() { ChunkId = "2", Content = "=== (43-48) ===", Score = 0.85f }, // 噪声
            new() { ChunkId = "3", Content = "   \n\n   ", Score = 0.8f }, // 空白
            new() { ChunkId = "4", Content = "-------------------", Score = 0.75f }, // 纯分隔符
            new() { ChunkId = "5", Content = "这是第五条正常有意义的正文内容", Score = 0.7f }
        };

        var filtered = candidates
            .Where(r => !string.IsNullOrWhiteSpace(r.Content) && !MarkdownChunker.IsNoiseOrSeparator(r.Content))
            .ToList();

        Assert.Equal(2, filtered.Count);
        Assert.Equal("1", filtered[0].ChunkId);
        Assert.Equal("5", filtered[1].ChunkId);
    }

    [Fact]
    public async Task QuickSearchStrategy_PreventsCandidateStarvation_WhenTopNoiseChunksPresent()
    {
        var vectorAdapterMock = new Mock<IDenseVectorRetrievalAdapter>();
        vectorAdapterMock
            .Setup(a => a.EncodeQueryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[] { 0.1f, 0.2f });

        int requestedCandidateLimit = 0;
        vectorAdapterMock
            .Setup(a => a.SearchAsync(It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<float[], int, CancellationToken>((_, limit, _) =>
            {
                requestedCandidateLimit = limit;
                var list = new List<SearchResult>();
                // 前 10 条为噪声/纯分隔符分块
                for (int i = 0; i < 10; i++)
                {
                    list.Add(new SearchResult { ChunkId = $"noise-{i}", Content = $"=== ({i}-{i + 1}) ===", Score = 0.9f - i * 0.01f });
                }
                // 后续为正常正文内容
                for (int i = 10; i < limit; i++)
                {
                    list.Add(new SearchResult { ChunkId = $"valid-{i}", Content = $"这是第 {i} 条有效文档内容", Score = 0.8f - i * 0.01f });
                }
                return Task.FromResult(list);
            });

        var strategy = new QuickSearchStrategy();
        var context = new SearchExecutionContext
        {
            Request = new AIQueryRequest { Query = "你好", TopK = 5 },
            VectorAdapter = vectorAdapterMock.Object,
            StateMachine = new SearchPipelineStateMachine("Quick")
        };

        var result = await strategy.ExecuteAsync(context);

        // 验证召回上限至少保证 50 个候选池容量，彻底杜绝候选饥饿
        Assert.True(requestedCandidateLimit >= 50, $"QuickSearchStrategy should request >= 50 candidates, but got {requestedCandidateLimit}");
        Assert.Equal(5, result.Results.Count);
        Assert.All(result.Results, r => Assert.StartsWith("valid-", r.ChunkId));
    }
}
