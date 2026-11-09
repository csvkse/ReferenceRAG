using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReferenceRAG.Business.Features.Indexing.Services;
using ReferenceRAG.Core.Extensions;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Tests;

public class LexicalFileIndexerTests
{
    [Fact]
    public async Task SingleFilePipelineIndexesAndRenamesWithoutResolvingModels()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-single-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = dir } } };
            config.Indexing.EmbeddingEnabled = false;
            config.Search.EnableLexical = true;
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new ConfigManager(config));
            services.AddSingleton(coordinator);
            services.AddSingleton<IVectorStore>(new Mock<IVectorStore>(MockBehavior.Strict).Object);
            services.AddSingleton<IBM25Store>(new Mock<IBM25Store>(MockBehavior.Strict).Object);
            services.AddSingleton<IMarkdownChunker>(new MarkdownChunker());
            services.AddSingleton<ITextEnhancer>(new Mock<ITextEnhancer>().Object);
            services.AddSingleton<IEmbeddingService>(_ => throw new InvalidOperationException("embedding resolved"));
            services.AddSingleton<IEmbeddingTokenCounter>(_ => throw new InvalidOperationException("tokenizer resolved"));
            services.AddIndexingPipeline();
            services.AddSingleton<IGraphIndexingService>(new Mock<IGraphIndexingService>().Object);
            await using var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<IFileIndexPipeline>();
            var oldPath = Path.Combine(dir, "old.md");
            var newPath = Path.Combine(dir, "new.md");
            await File.WriteAllTextAsync(oldPath, "# Note\n\nsinglelexical unique content");
            Assert.True(await pipeline.IndexSingleAsync(oldPath, config.Sources));
            var old = await coordinator.GetActiveFileByPathAsync(oldPath);
            Assert.NotNull(old);
            File.Move(oldPath, newPath);
            Assert.True(await pipeline.IndexSingleAsync(newPath, config.Sources, oldPath));
            Assert.Null(await coordinator.GetActiveFileByPathAsync(oldPath));
            var renamed = await coordinator.GetActiveFileByPathAsync(newPath);
            Assert.NotNull(renamed);
            Assert.Single(await coordinator.SearchAsync("singlelexical", new[] { renamed!.Id }, 10));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DeleteSourceRemovesItsLexicalGenerations()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-delete-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var indexer = new LexicalFileIndexer(new MarkdownChunker(), coordinator);
            var path = Path.Combine(dir, "note.md");
            await File.WriteAllTextAsync(path, "# Note\n\ndeletesourceunique text");
            var file = await indexer.IndexAsync(path, "notes");
            var store = new Mock<IVectorStore>();
            store.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FileRecord>());
            var config = new ObsidianRagConfig();
            config.Search.EnableLexical = true;
            var guard = new FileProcessingGuard();
            var pipeline = new FileIndexPipeline(store.Object, new Mock<IBM25Store>().Object, new MarkdownChunker(),
                new Lazy<IEmbeddingService>(() => throw new InvalidOperationException("model resolved")),
                new Mock<ITextEnhancer>().Object, new ConfigManager(config), null,
                new Lazy<IEmbeddingTokenCounter?>(() => null), lexicalFactory: () => coordinator, guard: guard);
            Assert.True(guard.TryAcquire(path));
            await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.DeleteSourceAsync("notes"));
            Assert.NotNull(await coordinator.GetActiveFileByPathAsync(path));
            guard.Release(path);
            await pipeline.DeleteSourceAsync("notes");
            Assert.Null(await coordinator.GetActiveFileByPathAsync(path));
            Assert.Empty(await coordinator.SearchAsync("deletesourceunique", new[] { file.Id }, 10));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ChangingChunkConfigurationRebuildsUnchangedContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "note.md");
        try
        {
            await File.WriteAllTextAsync(path, "# Note\n\nconfigurationunique content");
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var config = new ObsidianRagConfig();
            var indexer = new LexicalFileIndexer(new MarkdownChunker(), coordinator, new ConfigManager(config));
            var first = await indexer.IndexAsync(path, "notes");
            var oldChunk = Assert.Single(await coordinator.SearchAsync("configurationunique", new[] { first.Id }, 10));
            config.Chunking.MaxTokens = 256;
            var second = await indexer.IndexAsync(path, "notes");
            Assert.NotEqual(first.ChunkingHash, second.ChunkingHash);
            Assert.NotEqual(oldChunk.ChunkId, Assert.Single(await coordinator.SearchAsync("configurationunique", new[] { first.Id }, 10)).ChunkId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ResolvingIndexPipelineDoesNotResolveEmbeddingOrModelTokenizer()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IVectorStore>(new Mock<IVectorStore>().Object);
        services.AddSingleton<IBM25Store>(new Mock<IBM25Store>().Object);
        services.AddSingleton<IMarkdownChunker>(new MarkdownChunker());
        services.AddSingleton<ITextEnhancer>(new Mock<ITextEnhancer>().Object);
        services.AddSingleton(new ConfigManager(new ObsidianRagConfig()));
        services.AddSingleton<IEmbeddingService>(_ => throw new InvalidOperationException("embedding was resolved"));
        services.AddSingleton<IEmbeddingTokenCounter>(_ => throw new InvalidOperationException("model tokenizer was resolved"));
        services.AddIndexingPipeline();
        services.AddSingleton<IGraphIndexingService>(new Mock<IGraphIndexingService>().Object);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IFileIndexPipeline>());
    }

    [Fact]
    public async Task IndexAndUpdateFileWithoutAnyModelDependencies()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-indexer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "note.md");
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var indexer = new LexicalFileIndexer(new MarkdownChunker(), coordinator);
            await File.WriteAllTextAsync(path, "# Note\n\nFirst uniquealpha document text.");
            var original = await indexer.IndexAsync(path, "notes");
            var first = Assert.Single(await coordinator.SearchAsync("uniquealpha", new[] { original.Id }, 10));
            Assert.Contains("uniquealpha", first.Content);
            await indexer.IndexAsync(path, "notes");
            Assert.Equal(first.ChunkId, Assert.Single(await coordinator.SearchAsync("uniquealpha", new[] { original.Id }, 10)).ChunkId);
            using (var damage = new SqliteConnection($"Data Source={Path.Combine(dir, "bm25.db")};Pooling=False"))
            {
                await damage.OpenAsync();
                using var cmd = damage.CreateCommand();
                cmd.CommandText = "DELETE FROM lexical_generation_fts WHERE chunk_id=$id";
                cmd.Parameters.AddWithValue("$id", first.ChunkId);
                await cmd.ExecuteNonQueryAsync();
            }
            await indexer.IndexAsync(path, "notes");
            Assert.NotEqual(first.ChunkId, Assert.Single(await coordinator.SearchAsync("uniquealpha", new[] { original.Id }, 10)).ChunkId);
            await File.WriteAllTextAsync(path, "# Note\n\nSecond uniquebeta document text.");
            var updated = await indexer.IndexAsync(path, "notes");
            Assert.Equal(original.Id, updated.Id);
            Assert.Empty(await coordinator.SearchAsync("uniquealpha", new[] { original.Id }, 10));
            Assert.NotEmpty(await coordinator.SearchAsync("uniquebeta", new[] { original.Id }, 10));
            await indexer.DeleteAsync(path);
            Assert.Empty(await coordinator.SearchAsync("uniquebeta", new[] { original.Id }, 10));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }
}
