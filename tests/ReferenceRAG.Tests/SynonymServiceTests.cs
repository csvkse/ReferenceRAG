using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Tests;

public class SynonymServiceTests
{
    private readonly SynonymService _synonymService;

    public SynonymServiceTests()
    {
        _synonymService = new SynonymService();
    }

    [Fact]
    public void ExpandQuery_WithEmptyQuery_ReturnsEmpty()
    {
        var result = _synonymService.ExpandQuery("");
        Assert.Equal("", result);
    }

    [Fact]
    public void ExpandQuery_WithNull_ReturnsNull()
    {
        var result = _synonymService.ExpandQuery(null!);
        Assert.Null(result);
    }

    [Fact]
    public void ExpandQuery_WithProgrammingTerm_AddsSynonyms()
    {
        var result = _synonymService.ExpandQuery("编程");
        Assert.Contains("编程", result);
    }

    [Fact]
    public void ExpandQuery_WithConfigTerm_AddsSynonyms()
    {
        var result = _synonymService.ExpandQuery("配置");
        Assert.Contains("配置", result);
    }

    [Fact]
    public void ExpandQuery_WithSearchTerm_AddsSynonyms()
    {
        var result = _synonymService.ExpandQuery("搜索");
        Assert.Contains("搜索", result);
    }

    [Fact]
    public void ExpandQuery_WithVectorTerm_AddsSynonyms()
    {
        var result = _synonymService.ExpandQuery("向量");
        Assert.Contains("向量", result);
    }

    [Fact]
    public void ExpandQuery_WithMultipleWords_ReturnsExpandedQuery()
    {
        var result = _synonymService.ExpandQuery("编程 学习");
        var words = result.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("编程", words);
        Assert.Contains("学习", words);
    }

    [Fact]
    public void GetSynonyms_WithKnownWord_ReturnsSynonyms()
    {
        var synonyms = _synonymService.GetSynonyms("代码");
        Assert.NotEmpty(synonyms);
        Assert.Contains("程序", synonyms);
    }

    [Fact]
    public void GetSynonyms_WithUnknownWord_ReturnsEmpty()
    {
        var synonyms = _synonymService.GetSynonyms("完全不认识的词");
        Assert.Empty(synonyms);
    }

    [Theory]
    [InlineData("的")]
    [InlineData("是")]
    [InlineData("在")]
    [InlineData("了")]
    [InlineData("和")]
    public void IsStopWord_WithStopWords_ReturnsTrue(string word)
    {
        Assert.True(_synonymService.IsStopWord(word));
    }

    [Theory]
    [InlineData("编程")]
    [InlineData("配置")]
    [InlineData("搜索")]
    [InlineData("向量")]
    public void IsStopWord_WithMeaningfulWords_ReturnsFalse(string word)
    {
        Assert.False(_synonymService.IsStopWord(word));
    }
}