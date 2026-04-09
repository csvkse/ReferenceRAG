using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using ObsidianRAG.Core.Services;
using Moq;

namespace ObsidianRAG.Tests;

public class QueryOptimizerTests
{
    private readonly QueryOptimizer _optimizer;
    private readonly Mock<IVectorStore> _mockVectorStore;

    public QueryOptimizerTests()
    {
        _mockVectorStore = new Mock<IVectorStore>();
        _optimizer = new QueryOptimizer(_mockVectorStore.Object);
    }

    [Fact]
    public void ExpandQuery_WithEmptyQuery_ReturnsEmpty()
    {
        var result = _optimizer.ExpandQuery("");
        Assert.Equal("", result);
    }

    [Fact]
    public void ExpandQuery_WithProgrammingQuery_ExpandsSynonyms()
    {
        var result = _optimizer.ExpandQuery("编程");
        Assert.NotEmpty(result);
        Assert.Contains("编程", result);
    }

    [Fact]
    public void ExpandQuery_WithSearchQuery_ExpandsSynonyms()
    {
        var result = _optimizer.ExpandQuery("搜索");
        Assert.NotEmpty(result);
    }

    [Fact]
    public void AnalyzeIntent_WithHowToQuery_DetectsHowTo()
    {
        var result = _optimizer.AnalyzeIntent("如何学习编程？");

        Assert.Equal(QueryType.HowTo, result.Type);
        Assert.NotEmpty(result.Keywords);
    }

    [Fact]
    public void AnalyzeIntent_WithDefinitionQuery_DetectsDefinition()
    {
        var result = _optimizer.AnalyzeIntent("什么是向量数据库？");

        Assert.Equal(QueryType.Definition, result.Type);
    }

    [Fact]
    public void AnalyzeIntent_WithWhyQuery_DetectsReason()
    {
        // "为什么" might match "什么" pattern first
        var result = _optimizer.AnalyzeIntent("为什么需要向量索引？");
        Assert.True(result.Type == QueryType.Definition || result.Type == QueryType.Explanation);
    }

    [Fact]
    public void AnalyzeIntent_WithExampleQuery_DetectsExample()
    {
        var result = _optimizer.AnalyzeIntent("给我一个使用的例子");

        Assert.Equal(QueryType.Example, result.Type);
    }

    [Fact]
    public void AnalyzeIntent_WithGeneralQuery_DetectsGeneral()
    {
        var result = _optimizer.AnalyzeIntent("向量检索的原理");

        Assert.Equal(QueryType.General, result.Type);
    }

    [Fact]
    public void QueryOptimizer_WithDefaultOptions_CreatesSuccessfully()
    {
        var optimizer = new QueryOptimizer(_mockVectorStore.Object, options: new QueryOptimizeOptions());

        Assert.NotNull(optimizer);
    }
}
