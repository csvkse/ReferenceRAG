using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Business.Features.Indexing.Services;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Service.Hubs;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Tests;

public class LexicalIndexJobTests
{
    private sealed class Events : IIndexEventPublisher
    {
        public readonly TaskCompletionSource<IndexCompletedEvent> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PublishAsync(string name, object payload, CancellationToken cancellationToken = default)
        {
            if (name == "IndexCompleted") Done.TrySetResult((IndexCompletedEvent)payload);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ExplicitFileIndexCannotBypassSourceExclusions()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var root = Path.Combine(Path.GetTempPath(), $"lexical-file-filter-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        var hiddenDir = Path.Combine(source, ".git");
        Directory.CreateDirectory(hiddenDir);
        var path = Path.Combine(hiddenDir, "secret.md");
        try
        {
            await File.WriteAllTextAsync(path, "excludedunique");
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(root, "vectors.db"), Path.Combine(root, "bm25.db"));
            var indexer = new LexicalFileIndexer(new MarkdownChunker(), coordinator);
            var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = source } } };
            config.Indexing.EmbeddingEnabled = false;
            config.Search.EnableLexical = true;
            await using var provider = new ServiceCollection().AddSingleton(coordinator).AddSingleton(indexer).BuildServiceProvider();
            var guard = new Mock<IFileProcessingGuard>();
            guard.Setup(g => g.TryAcquire(It.IsAny<string>())).Returns(true);
            var service = new IndexService(provider, new Events(), new ConfigManager(config), new Mock<IFileIndexPipeline>().Object,
                guard.Object, NullLogger<IndexService>.Instance);
            var job = await service.StartIndexAsync(new IndexRequest { Files = new() { path } });
            await service.WaitForCompletionAsync(job).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(IndexStatus.Failed, job.Status);
            Assert.Null(await coordinator.GetActiveFileByPathAsync(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FullScanFailsIfDeletedFileCannotBeLockedForCleanup()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var root = Path.Combine(Path.GetTempPath(), $"lexical-cleanup-lock-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        var path = Path.Combine(source, "note.md");
        try
        {
            await File.WriteAllTextAsync(path, "# Note\n\ncleanupunique content");
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(root, "vectors.db"), Path.Combine(root, "bm25.db"));
            var indexer = new LexicalFileIndexer(new MarkdownChunker(), coordinator);
            await indexer.IndexAsync(path, "notes");
            File.Delete(path);
            var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = source } } };
            config.Indexing.EmbeddingEnabled = false;
            config.Search.EnableLexical = true;
            var services = new ServiceCollection().AddSingleton(coordinator).AddSingleton(indexer);
            await using var provider = services.BuildServiceProvider();
            var guard = new Mock<IFileProcessingGuard>();
            guard.Setup(g => g.TryAcquire(path)).Returns(false);
            var service = new IndexService(provider, new Events(), new ConfigManager(config), new Mock<IFileIndexPipeline>().Object,
                guard.Object, NullLogger<IndexService>.Instance);
            var job = await service.StartIndexAsync(new IndexRequest());
            await service.WaitForCompletionAsync(job).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(IndexStatus.Failed, job.Status);
            Assert.NotNull(await coordinator.GetActiveFileByPathAsync(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task IndexJobBuildsLexicalIndexWithoutResolvingEmbedding()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var root = Path.Combine(Path.GetTempPath(), $"lexical-job-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "note.md"), "# Note\n\nA uniqueword appears in this document.");
        Directory.CreateDirectory(Path.Combine(source, ".git"));
        await File.WriteAllTextAsync(Path.Combine(source, ".git", "hidden.md"), "uniquehidden");
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(root, "vectors.db"), Path.Combine(root, "bm25.db"));
            var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = source } } };
            config.Search.EnableLexical = true;
            config.Indexing.EmbeddingEnabled = false;
            var services = new ServiceCollection();
            services.AddSingleton(coordinator);
            services.AddSingleton(new LexicalFileIndexer(new MarkdownChunker(), coordinator));
            await using var provider = services.BuildServiceProvider();
            var events = new Events();
            var guard = new Mock<IFileProcessingGuard>();
            guard.Setup(g => g.TryAcquire(It.IsAny<string>())).Returns(true);
            var pipeline = new Mock<IFileIndexPipeline>(MockBehavior.Strict);
            var service = new IndexService(provider, events, new ConfigManager(config), pipeline.Object,
                guard.Object, NullLogger<IndexService>.Instance);
            var job = await service.StartIndexAsync(new IndexRequest());
            await events.Done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(IndexStatus.Completed, job.Status);
            Assert.Equal(0, job.TotalVectors);
            var results = await coordinator.SearchAuthorizedAsync("uniqueword", new LexicalSearchScope(new[] { "notes" }), 10);
            Assert.Single(results);
            Assert.Empty(await coordinator.SearchAuthorizedAsync("uniquehidden", new LexicalSearchScope(new[] { "notes" }), 10));
            File.Delete(Path.Combine(source, "note.md"));
            var second = await service.StartIndexAsync(new IndexRequest());
            await service.WaitForCompletionAsync(second).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(IndexStatus.Completed, second.Status);
            Assert.Empty(await coordinator.SearchAuthorizedAsync("uniqueword", new LexicalSearchScope(new[] { "notes" }), 10));
            pipeline.VerifyNoOtherCalls();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
