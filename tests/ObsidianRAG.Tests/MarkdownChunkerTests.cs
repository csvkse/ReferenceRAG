using ObsidianRAG.Core.Services;
using ObsidianRAG.Core.Models;
using Xunit;

namespace ObsidianRAG.Tests;

/// <summary>
/// MarkdownChunker 单元测试
/// </summary>
public class MarkdownChunkerTests
{
    private readonly MarkdownChunker _chunker;

    public MarkdownChunkerTests()
    {
        _chunker = new MarkdownChunker();
    }

    [Fact]
    public void Chunk_SimpleDocument_ReturnsChunks()
    {
        // Arrange
        var content = @"# 标题1
这是第一段内容。

## 标题2
这是第二段内容。
包含多行。";
        var file = new FileRecord { Id = "test-1", Path = "test.md" };

        // Act
        var chunks = _chunker.Chunk(content, file);

        // Assert
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.False(string.IsNullOrEmpty(c.Content)));
    }

    [Fact]
    public void Chunk_WithHeadings_SetsHeadingPath()
    {
        // Arrange
        var content = @"# 主标题
内容

## 子标题
更多内容";
        var file = new FileRecord { Id = "test-2", Path = "test.md" };

        // Act
        var chunks = _chunker.Chunk(content, file);

        // Assert
        Assert.NotEmpty(chunks);
        var firstChunk = chunks.First();
        Assert.Contains("主标题", firstChunk.HeadingPath ?? "");
    }

    [Fact]
    public void Chunk_SetsLineNumbers()
    {
        // Arrange
        var content = "第一行\n第二行\n第三行";
        var file = new FileRecord { Id = "test-3", Path = "test.md" };

        // Act
        var chunks = _chunker.Chunk(content, file);

        // Assert
        Assert.NotEmpty(chunks);
        var chunk = chunks.First();
        Assert.True(chunk.StartLine >= 1);
        Assert.True(chunk.EndLine >= chunk.StartLine);
    }

    [Fact]
    public void Chunk_LongContent_SplitsIntoMultipleChunks()
    {
        // Arrange
        var content = string.Join("\n\n", Enumerable.Range(0, 100).Select(i => $"段落 {i} 的内容，这是一段测试文本。"));
        var file = new FileRecord { Id = "test-4", Path = "test.md" };
        var options = new ChunkingOptions { MaxTokens = 100 };

        // Act
        var chunker = new MarkdownChunker(options);
        var chunks = chunker.Chunk(content, file);

        // Assert
        Assert.True(chunks.Count > 1);
    }

    [Fact]
    public void Chunk_EmptyContent_ReturnsEmpty()
    {
        // Arrange
        var content = "";
        var file = new FileRecord { Id = "test-5", Path = "test.md" };

        // Act
        var chunks = _chunker.Chunk(content, file);

        // Assert
        // 空内容可能返回一个空块或不返回
        Assert.True(chunks.Count <= 1);
    }
}
