using Moq;
using ReferenceRAG.Business.Features.Search.Adapters;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Business.Features.Search.StateMachine;
using ReferenceRAG.Business.Features.Search.Strategies;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using Xunit;

namespace ReferenceRAG.Tests;

public class DeepSearchStrategyTests
{
    [Fact]
    public void SearchStrategyFactory_ResolvesDeepSearchStrategy_WhenHybridAvailable()
    {
        var hybridMock = new Mock<IHybridSearchService>();
        var factory = new SearchStrategyFactory(hybridMock.Object);

        var strategy = factory.GetStrategy(QueryMode.Deep);

        Assert.NotNull(strategy);
        Assert.IsType<DeepSearchStrategy>(strategy);
        Assert.Equal(QueryMode.Deep, strategy.Mode);
    }

    [Fact]
    public async Task DeepSearchStrategy_ExecutesMassiveDualRecallAndDeepRerank()
    {
        var hybridMock = new Mock<IHybridSearchService>();
        int requestedRecallTopK = 0;

        hybridMock
            .Setup(h => h.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<float>(),
                It.IsAny<float>(),
                It.IsAny<IEnumerable<string>?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<float[]?>()))
            .Returns<string, int, float, float, IEnumerable<string>?, CancellationToken, float[]?>(
                (query, topK, k1, b, folders, ct, vec) =>
                {
                    requestedRecallTopK = topK;
                    var candidates = new List<HybridSearchResult>();
                    for (int i = 0; i < 60; i++)
                    {
                        candidates.Add(new HybridSearchResult
                        {
                            ChunkId = $"chunk-{i}",
                            FileId = $"file-{i % 5}",
                            FilePath = $"docs/guide-{i % 5}.md",
                            Content = $"这是第 {i} 条技术文档深度语义分块",
                            Score = 0.9f - i * 0.005f,
                            BM25Score = 10f - i * 0.1f,
                            EmbeddingScore = 0.85f
                        });
                    }
                    return Task.FromResult(candidates);
                });

        var rerankAdapterMock = new Mock<IRerankExecutionAdapter>();
        rerankAdapterMock.Setup(r => r.IsAvailable).Returns(true);
        rerankAdapterMock.Setup(r => r.ModelInfo).Returns(new ModelExecutionInfo { ModelName = "bge-reranker-large" });

        int rerankDocumentsCount = 0;
        rerankAdapterMock
            .Setup(r => r.RerankBatchAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .Returns<string, List<string>, CancellationToken>((q, docs, ct) =>
            {
                rerankDocumentsCount = docs.Count;
                var rerankDocs = docs.Select((d, idx) => new RerankDocument
                {
                    Index = idx,
                    RelevanceScore = 0.95 - idx * 0.01
                }).ToList();

                return Task.FromResult(new RerankResult
                {
                    Query = q,
                    Documents = rerankDocs,
                    DurationMs = 25
                });
            });

        var strategy = new DeepSearchStrategy(hybridMock.Object);
        var context = new SearchExecutionContext
        {
            Request = new AIQueryRequest
            {
                Query = "分布式状态机与重排",
                Mode = QueryMode.Deep,
                TopK = 20
            },
            Config = new ObsidianRagConfig
            {
                Rerank = new RerankConfig
                {
                    Enabled = true,
                    RecallFactor = 10,
                    TopN = 20
                }
            },
            RerankAdapter = rerankAdapterMock.Object,
            StateMachine = new SearchPipelineStateMachine("Deep")
        };

        var result = await strategy.ExecuteAsync(context);

        // 验证 1：深度模式双路召回池容量大幅扩充 (TopK 20 * 10 = 200)
        Assert.True(requestedRecallTopK >= 200, $"DeepSearchStrategy should request >= 200 candidates, but got {requestedRecallTopK}");

        // 验证 2：深度 Cross-Encoder 精排候选池不低于 50 条
        Assert.True(rerankDocumentsCount >= 50, $"Rerank documents count should be >= 50, but got {rerankDocumentsCount}");

        // 验证 3：返回 Top 20 高精度深度排序结果
        Assert.Equal(20, result.Results.Count);
        Assert.True(result.RerankApplied);
        Assert.NotNull(result.RerankStats);
        Assert.All(result.Results, r => Assert.NotNull(r.RerankScore));
    }
}
