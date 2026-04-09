using ObsidianRAG.Core.Services;

namespace ObsidianRAG.Tests;

public class SimpleTokenizerTests
{
    private readonly SimpleTokenizer _tokenizer;

    public SimpleTokenizerTests()
    {
        _tokenizer = new SimpleTokenizer();
    }

    [Fact]
    public void CountTokens_WithEmptyString_ReturnsZero()
    {
        var count = _tokenizer.CountTokens("");
        Assert.Equal(0, count);
    }

    [Fact]
    public void CountTokens_WithSimpleText_ReturnsCorrectCount()
    {
        // "Hello world" = 10 English chars -> 10/4 = 2 tokens
        var text = "Hello world";
        var count = _tokenizer.CountTokens(text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void CountTokens_WithChineseText_ReturnsCorrectEstimate()
    {
        // Chinese: 4 chars / 1.5 = 2.67 -> 2 tokens
        var text = "你好世界";
        var count = _tokenizer.CountTokens(text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void CountTokens_WithMixedText_ReturnsCorrectEstimate()
    {
        // "Hello 你好 world 世界" = 5E + 2C + 5E + 2C + 2O
        // English: 10/4 = 2, Chinese: 4/1.5 = 2, Other: 2/2 = 1
        // Total: 5 tokens
        var text = "Hello 你好 world 世界";
        var count = _tokenizer.CountTokens(text);
        Assert.Equal(5, count);
    }

    [Fact]
    public void CountTokens_WithPunctuation_CountsCorrectly()
    {
        // "Hello, world!" = 5E + 1O + 5E + 1O
        // English: 10/4 = 2, Other: 2/2 = 1
        // Total: 3 tokens
        var text = "Hello, world!";
        var count = _tokenizer.CountTokens(text);
        Assert.Equal(3, count);
    }

    [Theory]
    [InlineData("short text", 2)]  // 5E + 1O + 4E = 9E/4 + 1O/2 = 2 + 0 = 2
    [InlineData("a", 0)]           // 1E/4 = 0
    [InlineData("very long text with many words", 8)]  // ~28E + 1O chars
    public void CountTokens_WithVariousTexts_ReturnsExpectedCount(string text, int expected)
    {
        var count = _tokenizer.CountTokens(text);
        Assert.Equal(expected, count);
    }
}
