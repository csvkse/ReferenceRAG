using ObsidianRAG.Core.Services;
using Xunit;

namespace ObsidianRAG.Tests;

/// <summary>
/// SimpleTokenizer 单元测试
/// </summary>
public class SimpleTokenizerTests
{
    private readonly SimpleTokenizer _tokenizer;

    public SimpleTokenizerTests()
    {
        _tokenizer = new SimpleTokenizer();
    }

    [Fact]
    public void CountTokens_ChineseText_ReturnsEstimate()
    {
        // Arrange
        var text = "这是一段中文测试文本";

        // Act
        var count = _tokenizer.CountTokens(text);

        // Assert
        Assert.True(count > 0);
        // 中文约 1.5 字符/token
        Assert.True(count < text.Length);
    }

    [Fact]
    public void CountTokens_EnglishText_ReturnsEstimate()
    {
        // Arrange
        var text = "This is an English test text for token counting";

        // Act
        var count = _tokenizer.CountTokens(text);

        // Assert
        Assert.True(count > 0);
        // 英文约 4 字符/token
        Assert.True(count < text.Length);
    }

    [Fact]
    public void CountTokens_MixedText_ReturnsEstimate()
    {
        // Arrange
        var text = "这是中文 mixed with English text";

        // Act
        var count = _tokenizer.CountTokens(text);

        // Assert
        Assert.True(count > 0);
    }

    [Fact]
    public void CountTokens_EmptyText_ReturnsZero()
    {
        // Arrange
        var text = "";

        // Act
        var count = _tokenizer.CountTokens(text);

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void CountTokens_LongText_ReturnsReasonableCount()
    {
        // Arrange
        var text = string.Join(" ", Enumerable.Repeat("test", 1000));

        // Act
        var count = _tokenizer.CountTokens(text);

        // Assert
        Assert.True(count > 0);
        Assert.True(count < text.Length);
    }
}
