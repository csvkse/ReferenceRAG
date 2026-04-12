using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// EmbeddingService 单元测试
/// </summary>
public class EmbeddingServiceTests : IDisposable
{
    private readonly EmbeddingService _service;

    public EmbeddingServiceTests()
    {
        // 使用空路径创建服务（模拟模式）
        var options = new EmbeddingOptions
        {
            ModelPath = "",
            ModelName = "test-model"
        };
        _service = new EmbeddingService(options);
    }

    [Fact]
    public void ModelName_ReturnsConfiguredName()
    {
        // Assert
        Assert.Equal("test-model", _service.ModelName);
    }

    [Fact]
    public void Dimension_ReturnsPositiveValue()
    {
        // Assert
        Assert.True(_service.Dimension > 0);
    }

    [Fact]
    public async Task EncodeAsync_ReturnsNormalizedVector()
    {
        // Arrange
        var text = "测试文本";

        // Act
        var vector = await _service.EncodeAsync(text);

        // Assert
        Assert.Equal(_service.Dimension, vector.Length);
        
        // 检查归一化：L2范数应接近1
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        Assert.True(Math.Abs(norm - 1.0f) < 0.01f);
    }

    [Fact]
    public async Task EncodeBatchAsync_ReturnsCorrectCount()
    {
        // Arrange
        var texts = new[] { "文本1", "文本2", "文本3" };

        // Act
        var vectors = await _service.EncodeBatchAsync(texts);

        // Assert
        Assert.Equal(3, vectors.Length);
        Assert.All(vectors, v => Assert.Equal(_service.Dimension, v.Length));
    }

    [Fact]
    public void Normalize_ZeroVector_ReturnsZeroVector()
    {
        // Arrange
        var zero = new float[384];

        // Act
        var normalized = _service.Normalize(zero);

        // Assert
        Assert.Equal(384, normalized.Length);
        Assert.All(normalized, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Similarity_SameVectors_ReturnsOne()
    {
        // Arrange
        var vector = new float[] { 0.5f, 0.5f, 0.5f, 0.5f };
        var normalized = _service.Normalize(vector);

        // Act
        var similarity = _service.Similarity(normalized, normalized);

        // Assert
        Assert.True(Math.Abs(similarity - 1.0f) < 0.01f);
    }

    [Fact]
    public void Similarity_OrthogonalVectors_ReturnsZero()
    {
        // Arrange
        var a = new float[] { 1f, 0f };
        var b = new float[] { 0f, 1f };

        // Act
        var similarity = _service.Similarity(a, b);

        // Assert
        Assert.True(Math.Abs(similarity) < 0.01f);
    }

    public void Dispose()
    {
        _service.Dispose();
    }
}
