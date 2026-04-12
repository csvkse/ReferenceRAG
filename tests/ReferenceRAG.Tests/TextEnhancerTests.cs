using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Models;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// TextEnhancer 单元测试
/// </summary>
public class TextEnhancerTests
{
    private readonly TextEnhancer _enhancer;

    public TextEnhancerTests()
    {
        _enhancer = new TextEnhancer();
    }

    [Fact]
    public void Enhance_WithTitle_AddsTitlePrefix()
    {
        // Arrange
        var content = "这是正文内容";
        var context = new EnhancementContext
        {
            Title = "文档标题"
        };

        // Act
        var enhanced = _enhancer.Enhance(content, context);

        // Assert
        Assert.Contains("文档标题", enhanced);
    }

    [Fact]
    public void Enhance_WithTags_AddsTags()
    {
        // Arrange
        var content = "正文内容";
        var context = new EnhancementContext
        {
            Tags = new List<string> { "标签1", "标签2" }
        };

        // Act
        var enhanced = _enhancer.Enhance(content, context);

        // Assert
        Assert.Contains("标签1", enhanced);
        Assert.Contains("标签2", enhanced);
    }

    [Fact]
    public void Enhance_WithHeadingPath_AddsPath()
    {
        // Arrange
        var content = "段落内容";
        var context = new EnhancementContext
        {
            HeadingPath = "主标题/子标题"
        };

        // Act
        var enhanced = _enhancer.Enhance(content, context);

        // Assert
        Assert.Contains("主标题", enhanced);
    }

    [Fact]
    public void Enhance_WithAllContext_CombinesAll()
    {
        // Arrange
        var content = "原始内容";
        var context = new EnhancementContext
        {
            Title = "标题",
            Tags = new List<string> { "tag" },
            HeadingPath = "章节",
            FilePath = "path/to/file.md"
        };

        // Act
        var enhanced = _enhancer.Enhance(content, context);

        // Assert
        Assert.Contains("标题", enhanced);
        Assert.Contains("tag", enhanced);
        Assert.Contains("章节", enhanced);
        Assert.Contains("原始内容", enhanced);
    }

    [Fact]
    public void Enhance_EmptyContext_ReturnsOriginalContent()
    {
        // Arrange
        var content = "原始内容";
        var context = new EnhancementContext();

        // Act
        var enhanced = _enhancer.Enhance(content, context);

        // Assert
        Assert.Contains("原始内容", enhanced);
    }
}
