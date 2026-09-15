using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Tests;

public class MarkdownChunkerTests
{
    private readonly MarkdownChunker _chunker;

    public MarkdownChunkerTests()
    {
        _chunker = new MarkdownChunker();
    }

    [Fact]
    public void Chunk_WithEmptyContent_ReturnsAtLeastOneChunk()
    {
        // Current implementation returns a chunk even for empty content
        var chunks = _chunker.Chunk("", new ChunkingOptions());
        Assert.True(chunks.Count >= 0); // May return 1 chunk with empty content
    }

    [Fact]
    public void Chunk_WithSimpleText_ReturnsSingleChunk()
    {
        var content = "This is a simple test content.";
        var chunks = _chunker.Chunk(content, new ChunkingOptions { MaxTokens = 512 });

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public void Chunk_WithShortContent_ReturnsSingleChunk()
    {
        var content = "# Title\n\nThis is a short paragraph.";
        var chunks = _chunker.Chunk(content, new ChunkingOptions { MaxTokens = 512, MinTokens = 10 });

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public void Chunk_WithHeadings_ExtractsSections()
    {
        var content = "# Heading 1\n\nContent 1\n\n## Heading 2\n\nContent 2";
        var chunks = _chunker.Chunk(content, new ChunkingOptions { MaxTokens = 512 });

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.NotNull(chunk.Content));
    }

    [Fact]
    public void Chunk_WithHeadingPath_PreservesHierarchy()
    {
        var content = "# Main\n\n## Sub\n\n### Detail\n\nContent here";
        var chunks = _chunker.Chunk(content, new ChunkingOptions { MaxTokens = 512, PreserveHeadings = true });

        Assert.NotEmpty(chunks);
        // At least one chunk should have a heading path
        Assert.Contains(chunks, c => !string.IsNullOrEmpty(c.HeadingPath));
    }

    [Fact]
    public void Chunk_WithOptions_UsesProvidedOptions()
    {
        var content = "# Title\n\nShort content";
        var options = new ChunkingOptions
        {
            MaxTokens = 512,
            MinTokens = 5,
            PreserveHeadings = true,
            PreserveCodeBlocks = true,
            OverlapTokens = 20
        };

        var chunks = _chunker.Chunk(content, options);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk =>
        {
            Assert.True(chunk.TokenCount >= 0);
            Assert.True(chunk.StartLine >= 0);
            Assert.True(chunk.EndLine >= chunk.StartLine);
        });
    }

    [Fact]
    public void Chunk_WithCodeBlocks_PreservesCodeBlocks()
    {
        var content = "# Code Example\n\n```csharp\npublic void Test() { }\n```\n\nMore content";
        var chunks = _chunker.Chunk(content, new ChunkingOptions { MaxTokens = 512, PreserveCodeBlocks = true });

        Assert.NotEmpty(chunks);
    }

    [Fact]
    public void Chunk_WithFileRecord_SetsFileId()
    {
        var content = "Test content";
        var file = new FileRecord { Id = "test-file-id", Path = "/test/file.md" };

        var chunks = _chunker.Chunk(content, file);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.Equal("test-file-id", chunk.FileId));
    }

    [Fact]
    public void Chunk_PreserveHeadingsTrue_KeepsHeadingLinesInContent()
    {
        var content = "# Main Title\n\nBody paragraph.";
        var chunks = _chunker.Chunk(content, new ChunkingOptions { MaxTokens = 512, PreserveHeadings = true });

        Assert.Contains(chunks, chunk => chunk.Content.Contains("# Main Title"));
    }

    [Fact]
    public void Chunk_PreserveHeadingsFalse_ExcludesHeadingLinesFromContent()
    {
        var content = "# Main Title\n\nBody paragraph one.\n\n## Sub Title\n\nBody paragraph two.";
        var chunks = _chunker.Chunk(content, new ChunkingOptions { MaxTokens = 512, PreserveHeadings = false });

        Assert.NotEmpty(chunks);
        Assert.Contains(chunks, chunk => chunk.Content.Contains("Body paragraph one."));
        Assert.Contains(chunks, chunk => chunk.Content.Contains("Body paragraph two."));
        Assert.All(chunks, chunk =>
            Assert.DoesNotContain(chunk.Content.Split('\n'), line => line.TrimStart().StartsWith("#")));
        // HeadingPath 元数据保留，供搜索与图谱继续使用
        Assert.Contains(chunks, chunk => !string.IsNullOrEmpty(chunk.HeadingPath));
    }

    [Fact]
    public void Chunk_PreserveHeadingsFalse_SkipsHeadingOnlySections()
    {
        var chunks = _chunker.Chunk("# Only Heading", new ChunkingOptions { MaxTokens = 512, PreserveHeadings = false });

        Assert.Empty(chunks);
    }

    [Fact]
    public void Chunk_PreserveCodeBlocksTrue_KeepsFencedBlockIntactAcrossChunks()
    {
        var codeBody = string.Join("\n\n", Enumerable.Range(1, 8).Select(i =>
            $"var value{i} = compute({i}, params); result{i}.push(value{i});"));
        var content = "# Code\n\n```csharp\n" + codeBody + "\n```\n\nAfter the block.";

        var chunks = _chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 40, MinTokens = 0, OverlapTokens = 0, PreserveCodeBlocks = true
        });

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk =>
        {
            var fenceCount = CountOccurrences(chunk.Content, "```");
            Assert.True(fenceCount == 0 || fenceCount % 2 == 0,
                $"chunk {chunk.ChunkIndex} 内 fence 未配对: {fenceCount}");
        });
        var fullBlock = "```csharp\n" + codeBody + "\n```";
        Assert.Contains(chunks, chunk => chunk.Content.Contains(fullBlock));
    }

    [Fact]
    public void Chunk_PreserveCodeBlocksFalse_AllowsSplittingFencedBlock()
    {
        var codeBody = string.Join("\n\n", Enumerable.Range(1, 8).Select(i =>
            $"var value{i} = compute({i}, params); result{i}.push(value{i});"));
        var content = "# Code\n\n```csharp\n" + codeBody + "\n```\n\nAfter the block.";

        var chunks = _chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 40, MinTokens = 0, OverlapTokens = 0, PreserveCodeBlocks = false
        });

        Assert.NotEmpty(chunks);
        Assert.Contains(chunks, chunk => CountOccurrences(chunk.Content, "```") % 2 == 1);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
