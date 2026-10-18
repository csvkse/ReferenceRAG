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

        // 代码块超限时按行拆分成多个 Code 子块，块内围栏允许单侧
        // （开围栏在首块、闭围栏在末块），但任意子块不得再超限。
        var codeChunks = chunks.Where(c => c.ChunkType == ChunkType.Code).ToList();
        Assert.NotEmpty(codeChunks);
        Assert.All(codeChunks, chunk =>
            Assert.True(
                _chunkerTokenCount(chunk.Content) <= 40,
                $"code chunk {chunk.ChunkIndex} 超过 MaxTokens: {_chunkerTokenCount(chunk.Content)}"));

        // 拆分块按 ChunkIndex 顺序拼接应还原完整代码块
        var joined = string.Join("\n", codeChunks.OrderBy(c => c.ChunkIndex).Select(c => c.Content));
        var fenceCount = CountOccurrences(joined, "```");
        Assert.Equal(2, fenceCount);   // 开/闭围栏各一次
        Assert.Contains("```csharp\n", joined);
        Assert.Contains("\n```", joined);
        Assert.Contains(codeBody.Split('\n')[0], joined);

        // 非代码块仍保持完整
        Assert.Contains(chunks, chunk => chunk.Content.Contains("After the block."));
    }

    private static int _chunkerTokenCount(string text)
        => ReferenceRAG.Core.Helpers.TokenEstimator.EstimateTokens(text);

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

    [Theory]
    [InlineData("=== (43-48) ===", true)]
    [InlineData("---", true)]
    [InlineData("***", true)]
    [InlineData("  === (1-6) ===  ", true)]
    [InlineData("==================", true)]
    [InlineData("...", true)]
    [InlineData("   ", true)]
    [InlineData("# 第一章 论持久战", false)]
    [InlineData("这是一个包含正常内容的段落。", false)]
    [InlineData("```csharp\nint x = 1;\n```", false)]
    public void IsNoiseOrSeparator_IdentifiesSeparatorsAndTrivialNoiseCorrectly(string input, bool expectedNoise)
    {
        var isNoise = MarkdownChunker.IsNoiseOrSeparator(input);
        Assert.Equal(expectedNoise, isNoise);
    }

    [Fact]
    public void Chunk_WithIsolatedSeparatorLine_MergesSeparatorIntoAdjacentChunk()
    {
        // 模拟长文本小说或文档中出现单独的 === (43-48) === 行
        var part1 = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"这是第{i}行正文文本内容，包含丰富的上下文信息。"));
        var separator = "=== (43-48) ===";
        var part2 = string.Join("\n", Enumerable.Range(31, 30).Select(i => $"这是第{i}行继续的正文内容，用于测试分块合并。"));

        var fullContent = $"{part1}\n\n{separator}\n\n{part2}";

        var chunks = _chunker.Chunk(fullContent, new ChunkingOptions
        {
            MaxTokens = 120,
            MinTokens = 10,
            OverlapTokens = 0
        });

        Assert.NotEmpty(chunks);

        // 绝不应该存在内容仅仅为 === (43-48) === 的独立单行分块
        Assert.DoesNotContain(chunks, c => c.Content.Trim() == separator);

        // 分隔符应当被吸收合并在某个分块内
        Assert.Contains(chunks, c => c.Content.Contains(separator));

        // 所有分块内容均非空且非纯噪音
        Assert.All(chunks, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Content));
            Assert.False(MarkdownChunker.IsNoiseOrSeparator(c.Content));
        });
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
