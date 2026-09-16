using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Tests;

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
        // Chinese: 1 char/token（对齐 BERT/BGE 类 tokenizer 的保守口径）
        var text = "你好世界";
        var count = _tokenizer.CountTokens(text);
        Assert.Equal(4, count);
    }

    [Fact]
    public void CountTokens_WithMixedText_ReturnsCorrectEstimate()
    {
        // "Hello 你好 world 世界" = 5E + 2C + 5E + 2C + 2O(空白不计)
        // English: 10/4 = 2, Chinese: 4/1 = 4, Other: 0
        // Total: 6 tokens
        var text = "Hello 你好 world 世界";
        var count = _tokenizer.CountTokens(text);
        Assert.Equal(6, count);
    }

    [Fact]
    public void CountTokens_MatchesTokenEstimator()
    {
        // 唯一实现：SimpleTokenizer 与 TokenEstimator 必须完全一致
        var samples = new[] { "你好世界", "Hello world", "Hello 你好 world 世界", "", "1234567890", "### Heading\n\n正文内容。" };
        foreach (var sample in samples)
        {
            Assert.Equal(
                ReferenceRAG.Core.Helpers.TokenEstimator.EstimateTokens(sample),
                _tokenizer.CountTokens(sample));
        }
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
    [InlineData("short text", 2)]  // 9E/4 = 2，空格不计
    [InlineData("a", 0)]           // 1E/4 = 0
    [InlineData("very long text with many words", 6)]  // 25E/4 = 6，空格不计
    public void CountTokens_WithVariousTexts_ReturnsExpectedCount(string text, int expected)
    {
        var count = _tokenizer.CountTokens(text);
        Assert.Equal(expected, count);
    }
}
