using System.Numerics.Tensors;
using Moq;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.ModelManagement.Services;
using ReferenceRAG.Core.Features.Search;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using Xunit;

namespace ReferenceRAG.Tests;

public class SearchPerformanceAndAccuracyTests
{
    [Fact]
    public void SimdCosineSimilarity_MatchesExpectedResult()
    {
        float[] a = [1.0f, 2.0f, 3.0f, 4.0f];
        float[] b = [2.0f, 4.0f, 6.0f, 8.0f]; // Exactly collinear, cosine should be 1.0f

        var similarity = MathHelper.CosineSimilarity(a, b);
        Assert.True(Math.Abs(similarity - 1.0f) < 1e-5f);

        float[] c = [1.0f, 0.0f, 0.0f];
        float[] d = [0.0f, 1.0f, 0.0f]; // Orthogonal, cosine should be 0.0f
        Assert.True(Math.Abs(MathHelper.CosineSimilarity(c, d)) < 1e-5f);
    }

    [Fact]
    public void PhoneticQueryExpander_ExpandsHomophonesAndTypos()
    {
        var expansions = PhoneticQueryExpander.GetExpansionTerms("月女剑里头那个很厉害的放洋小姑娘");
        Assert.Contains("越女剑", expansions);
        Assert.Contains("放羊", expansions);

        var expandedQuery = PhoneticQueryExpander.ExpandQueryString("连成决里面狄芸");
        Assert.Contains("连城诀", expandedQuery);
        Assert.Contains("狄云", expandedQuery);
    }

    [Fact]
    public void AdaptiveSearchFusion_CalculatesCorrectBiases()
    {
        // 1. 强实体/书名号
        var (wBm25Exact, wVecExact) = AdaptiveSearchFusionHelper.CalculateWeights("《越女剑》");
        Assert.True(wBm25Exact > wVecExact, "书名号应当赋予 BM25 更大权重");

        // 2. 长句场景与口语问答
        var (wBm25Colloquial, wVecColloquial) = AdaptiveSearchFusionHelper.CalculateWeights("月女剑里头那个很厉害的放洋小姑娘到底是跟哪只猴子学打架的呀");
        Assert.True(wVecColloquial > wBm25Colloquial, "口语复杂长句应当赋予 Vector 更大权重");

        // 3. 均衡常规问句（9-15字常规主题）
        var (wBm25Norm, wVecNorm) = AdaptiveSearchFusionHelper.CalculateWeights("金庸十四天书小说全集");
        Assert.Equal(0.5, wBm25Norm);
        Assert.Equal(0.5, wVecNorm);
    }

    [Fact]
    public async Task QueryEmbeddingCacheProxy_CachesAndInvalidates()
    {
        var mockInner = new Mock<IEmbeddingService>();
        mockInner.Setup(x => x.ModelName).Returns("bge-m3");
        mockInner.Setup(x => x.Dimension).Returns(1024);

        int encodeCallCount = 0;
        mockInner.Setup(x => x.EncodeAsync(It.IsAny<string>(), It.IsAny<EmbeddingMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string t, EmbeddingMode m, CancellationToken ct) =>
            {
                encodeCallCount++;
                return new float[] { 0.1f, 0.2f, 0.3f };
            });

        var proxy = new QueryEmbeddingCacheProxy(mockInner.Object, maxCapacity: 10);

        // 第一次调用：未命中缓存
        var vec1 = await proxy.EncodeAsync("金庸武侠小说?", CancellationToken.None);
        Assert.Equal(1, encodeCallCount);
        Assert.Equal(1, proxy.CachedCount);

        // 第二次调用规范化变体：应当命中缓存，不增加 encodeCallCount
        var vec2 = await proxy.EncodeAsync("金庸武侠小说？", CancellationToken.None);
        Assert.Equal(1, encodeCallCount);
        Assert.Equal(vec1, vec2);

        // 模型重载时清空缓存
        await proxy.ReloadModelAsync("new_path", "new_model");
        Assert.Equal(0, proxy.CachedCount);

        // 再次调用应当重新编码
        await proxy.EncodeAsync("金庸武侠小说", CancellationToken.None);
        Assert.Equal(2, encodeCallCount);
    }

    [Fact]
    public async Task QueryEmbeddingCacheProxy_BatchEncodingPartialCacheHit()
    {
        var mockInner = new Mock<IEmbeddingService>();
        mockInner.Setup(x => x.ModelName).Returns("bge-m3");

        mockInner.Setup(x => x.EncodeBatchAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> texts, EmbeddingMode m, CancellationToken ct) =>
            {
                return texts.Select(t => new float[] { t.Length }).ToArray();
            });

        var proxy = new QueryEmbeddingCacheProxy(mockInner.Object, maxCapacity: 50);

        // 先预热一条
        await proxy.EncodeAsync("越女剑", CancellationToken.None);

        // 批量编码 3 条，其中一条已在缓存中
        var batch = new[] { "越女剑", "连城诀", "雪山飞狐" };
        var results = await proxy.EncodeBatchAsync(batch, CancellationToken.None);

        Assert.Equal(3, results.Length);
        Assert.Equal(3, proxy.CachedCount);
    }
}
