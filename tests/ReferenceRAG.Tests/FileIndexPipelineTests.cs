using Moq;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Tests;

public class FileIndexPipelineTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _originalDir;

    public FileIndexPipelineTests()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });

        _testDir = Path.Combine(Path.GetTempPath(), $"file-index-pipeline-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _originalDir = Directory.GetCurrentDirectory();
    }

    private FileIndexPipeline CreatePipeline(
        string chunkingJson,
        IMarkdownChunker? chunker = null,
        IEmbeddingTokenCounter? tokenCounter = null,
        string embeddingJson = "{\"mode\":\"openai\",\"apiMaxInputTokens\":512}")
    {
        var appSettings = "{\"ReferenceRAG\":{\"chunking\":" + chunkingJson + ",\"embedding\":" + embeddingJson + "}}";
        File.WriteAllText(Path.Combine(_testDir, "appsettings.json"), appSettings);
        Directory.SetCurrentDirectory(_testDir);

        var vectorStore = new Mock<IVectorStore>();
        vectorStore
            .Setup(s => s.GetFileByPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileRecord?)null);
        vectorStore
            .Setup(s => s.GetChunksByFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        vectorStore
            .Setup(s => s.UpsertFileAsync(It.IsAny<FileRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var bm25Store = new Mock<IBM25Store>();

        return new FileIndexPipeline(
            vectorStore.Object,
            bm25Store.Object,
            chunker ?? new MarkdownChunker(),
            new Mock<IEmbeddingService>().Object,
            new TextEnhancer(),
            new ConfigManager(),
            tokenCounter: tokenCounter);
    }

    [Fact]
    public async Task PrepareAsync_UsesChunkingConfigFromSettings()
    {
        const int maxTokens = 100;
        var pipeline = CreatePipeline("{\"maxTokens\":100,\"minTokens\":20,\"overlapTokens\":20}");

        var filePath = Path.Combine(_testDir, "long-doc.md");
        var paragraphs = Enumerable.Range(1, 30)
            .Select(i => $"第 {i} 段。这是一段用于验证分块配置的中文内容，包含足够多的常见词汇与句子结构，让 token 估算保持稳定，同时确保整篇文档会切出多个片段。");
        await File.WriteAllTextAsync(filePath, string.Join("\n\n", paragraphs));

        var context = await pipeline.PrepareAsync(filePath, [], force: true);

        Assert.NotNull(context);
        Assert.NotEmpty(context!.Chunks);
        Assert.All(context.Chunks, chunk =>
            Assert.True(
                TokenEstimator.EstimateTokens(chunk.Content) <= maxTokens,
                $"chunk {chunk.ChunkIndex} 超过配置的 MaxTokens: {TokenEstimator.EstimateTokens(chunk.Content)}"));
    }

    [Fact]
    public async Task PrepareAsync_PassesPreserveOptionsFromConfig()
    {
        ChunkingOptions? captured = null;
        var chunker = new Mock<IMarkdownChunker>();
        chunker
            .Setup(c => c.Chunk(It.IsAny<string>(), It.IsAny<ChunkingOptions>()))
            .Callback((string _, ChunkingOptions? options) => captured = options)
            .Returns(new List<ChunkRecord>());

        var pipeline = CreatePipeline(
            "{\"maxTokens\":345,\"minTokens\":60,\"overlapTokens\":15,\"preserveHeadings\":false,\"preserveCodeBlocks\":false}",
            chunker.Object);

        var filePath = Path.Combine(_testDir, "options-doc.md");
        await File.WriteAllTextAsync(filePath, "# Title\n\nContent.");

        await pipeline.PrepareAsync(filePath, [], force: true);

        Assert.NotNull(captured);
        Assert.Equal(345, captured!.MaxTokens);
        Assert.Equal(60, captured.MinTokens);
        Assert.Equal(15, captured.OverlapTokens);
        Assert.False(captured.PreserveHeadings);
        Assert.False(captured.PreserveCodeBlocks);
    }

    [Fact]
    public async Task PrepareAsync_SplitsEnhancedContentWithExactTokenCounter()
    {
        const int maxInputTokens = 32;
        var tokenCounter = new LengthTokenCounter();
        var pipeline = CreatePipeline(
            "{\"maxTokens\":100,\"minTokens\":20,\"overlapTokens\":20}",
            tokenCounter: tokenCounter,
            embeddingJson: "{\"mode\":\"openai\",\"apiMaxInputTokens\":32}");

        var filePath = Path.Combine(_testDir, "exact-token-doc.md");
        var paragraphs = Enumerable.Range(1, 30)
            .Select(i => $"第 {i} 段。这是一段用于验证精确分词和嵌入输入限制的中文内容，包含足够多的常见词汇与句子结构。");
        await File.WriteAllTextAsync(filePath, string.Join("\n\n", paragraphs));

        var context = await pipeline.PrepareAsync(filePath, [], force: true);

        Assert.NotNull(context);
        Assert.NotEmpty(context!.Chunks);
        Assert.True(context.Chunks.Count > 30);
        Assert.All(context.Chunks, chunk =>
        {
            Assert.NotNull(chunk.EnhancedContent);
            Assert.True(chunk.EnhancedContent!.Length <= maxInputTokens - 2);
        });
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        try
        {
            Directory.Delete(_testDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

file sealed class LengthTokenCounter : IEmbeddingTokenCounter
{
    public Task<int> CountTokensAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(text.Length);
}
