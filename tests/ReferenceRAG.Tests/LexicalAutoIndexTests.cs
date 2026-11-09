using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReferenceRAG.Business.Features.Indexing.Services;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Tests;

public class LexicalAutoIndexTests
{
    [Fact]
    public async Task FailedLexicalChangeIsRetriedAfterFileBecomesReadable()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "note.md");
        try
        {
            await File.WriteAllTextAsync(path, "# Note\n\nretryunique content");
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = dir } } };
            config.Indexing.EmbeddingEnabled = false;
            config.Search.EnableLexical = true;
            using var provider = new ServiceCollection().AddSingleton(new LexicalFileIndexer(new MarkdownChunker(), coordinator)).BuildServiceProvider();
            var guard = new Mock<IFileProcessingGuard>();
            guard.Setup(g => g.TryAcquire(It.IsAny<string>())).Returns(true);
            using var service = new AutoIndexService(new Mock<IFileMonitorService>().Object, new Mock<IFileIndexPipeline>().Object,
                guard.Object, new ConfigManager(config), serviceProvider: provider) { QuietPeriod = TimeSpan.Zero };
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                service.OnFileChanged(this, new FileChangeEventArgs { FilePath = path, ChangeType = ChangeType.Created });
                await service.ProcessReadyChangesInternalAsync(DateTime.UtcNow.AddSeconds(1));
                Assert.Null(await coordinator.GetActiveFileByPathAsync(path));
            }
            await service.ProcessReadyChangesInternalAsync(DateTime.UtcNow.AddSeconds(1));
            Assert.NotNull(await coordinator.GetActiveFileByPathAsync(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task MonitorChangesUpdateAndDeleteWithoutEmbeddingPipeline()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-monitor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "note.md");
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = dir } } };
            config.Indexing.EmbeddingEnabled = false;
            config.Search.EnableLexical = true;
            var services = new ServiceCollection();
            services.AddSingleton(new LexicalFileIndexer(new MarkdownChunker(), coordinator));
            using var provider = services.BuildServiceProvider();
            var pipeline = new Mock<IFileIndexPipeline>(MockBehavior.Strict);
            var guard = new Mock<IFileProcessingGuard>();
            guard.Setup(g => g.TryAcquire(It.IsAny<string>())).Returns(true);
            using var service = new AutoIndexService(new Mock<IFileMonitorService>().Object, pipeline.Object,
                guard.Object, new ConfigManager(config), serviceProvider: provider) { QuietPeriod = TimeSpan.Zero };
            await File.WriteAllTextAsync(path, "# Note\n\nfirst uniquemonitor content");
            service.OnFileChanged(this, new FileChangeEventArgs { FilePath = path, ChangeType = ChangeType.Created });
            await service.ProcessReadyChangesInternalAsync(DateTime.UtcNow.AddSeconds(1));
            var file = await coordinator.GetActiveFileByPathAsync(path);
            Assert.NotNull(file);
            Assert.Single(await coordinator.SearchAsync("uniquemonitor", new[] { file!.Id }, 10));
            File.Delete(path);
            service.OnFileChanged(this, new FileChangeEventArgs { FilePath = path, ChangeType = ChangeType.Deleted });
            await service.ProcessReadyChangesInternalAsync(DateTime.UtcNow.AddSeconds(1));
            Assert.Empty(await coordinator.SearchAsync("uniquemonitor", new[] { file.Id }, 10));
            pipeline.VerifyNoOtherCalls();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }
}
