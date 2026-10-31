using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class DualChannelEmbeddingServiceTests
{
    [Fact]
    public async Task DualChannel_Routes_Query_To_SearchService_And_Document_To_IngestionService()
    {
        var searchServiceMock = new Mock<IEmbeddingService>();
        var ingestionServiceMock = new Mock<IEmbeddingService>();

        searchServiceMock.Setup(s => s.Dimension).Returns(1024);
        searchServiceMock.Setup(s => s.ModelName).Returns("bge-m3");
        ingestionServiceMock.Setup(s => s.Dimension).Returns(1024);
        ingestionServiceMock.Setup(s => s.ModelName).Returns("bge-m3");

        var localVec = new float[] { 0.1f, 0.2f, 0.3f };
        var onlineVec = new float[] { 0.9f, 0.8f, 0.7f };

        searchServiceMock
            .Setup(s => s.EncodeAsync("query text", EmbeddingMode.Query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(localVec);

        ingestionServiceMock
            .Setup(s => s.EncodeAsync("doc text", EmbeddingMode.Document, It.IsAny<CancellationToken>()))
            .ReturnsAsync(onlineVec);

        using var dualService = new DualChannelEmbeddingService(
            searchService: searchServiceMock.Object,
            ingestionService: ingestionServiceMock.Object,
            enableFallback: true
        );

        // 1. 验证搜索查询走本地
        var queryResult = await dualService.EncodeAsync("query text", EmbeddingMode.Query);
        Assert.Equal(localVec, queryResult);
        searchServiceMock.Verify(s => s.EncodeAsync("query text", EmbeddingMode.Query, It.IsAny<CancellationToken>()), Times.Once);
        ingestionServiceMock.Verify(s => s.EncodeAsync(It.IsAny<string>(), EmbeddingMode.Query, It.IsAny<CancellationToken>()), Times.Never);

        // 2. 验证向量生成走线上/入库服务
        var docResult = await dualService.EncodeAsync("doc text", EmbeddingMode.Document);
        Assert.Equal(onlineVec, docResult);
        ingestionServiceMock.Verify(s => s.EncodeAsync("doc text", EmbeddingMode.Document, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void DualChannel_Throws_When_Dimensions_Mismatch()
    {
        var searchServiceMock = new Mock<IEmbeddingService>();
        var ingestionServiceMock = new Mock<IEmbeddingService>();

        searchServiceMock.Setup(s => s.Dimension).Returns(384);
        ingestionServiceMock.Setup(s => s.Dimension).Returns(1024);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DualChannelEmbeddingService(searchServiceMock.Object, ingestionServiceMock.Object));

        Assert.Contains("维度 (384) 与生成通道模型维度 (1024) 不一致", ex.Message);
    }

    [Fact]
    public async Task DualChannel_Gracefully_FallsBack_To_SearchService_On_Ingestion_Failure()
    {
        var searchServiceMock = new Mock<IEmbeddingService>();
        var ingestionServiceMock = new Mock<IEmbeddingService>();

        searchServiceMock.Setup(s => s.Dimension).Returns(1024);
        ingestionServiceMock.Setup(s => s.Dimension).Returns(1024);

        var fallbackVec = new float[] { 0.5f, 0.5f, 0.5f };

        // 模拟线上生成失败（如网络超时/429）
        ingestionServiceMock
            .Setup(s => s.EncodeAsync(It.IsAny<string>(), EmbeddingMode.Document, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("SiliconFlow 503 Service Unavailable"));

        // 模拟本地备用正常
        searchServiceMock
            .Setup(s => s.EncodeAsync(It.IsAny<string>(), EmbeddingMode.Document, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fallbackVec);

        using var dualService = new DualChannelEmbeddingService(
            searchService: searchServiceMock.Object,
            ingestionService: ingestionServiceMock.Object,
            enableFallback: true
        );

        var result = await dualService.EncodeAsync("important doc", EmbeddingMode.Document);
        Assert.Equal(fallbackVec, result);
        searchServiceMock.Verify(s => s.EncodeAsync("important doc", EmbeddingMode.Document, It.IsAny<CancellationToken>()), Times.Once);
    }
}
