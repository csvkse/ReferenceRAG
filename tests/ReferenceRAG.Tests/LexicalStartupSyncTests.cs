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
using ReferenceRAG.Storage;

namespace ReferenceRAG.Tests;

public class LexicalStartupSyncTests
{
    [Fact]
    public async Task StartupSynchronizesLexicalFilesWithoutAccessingVectorStore()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-startup-{Guid.NewGuid():N}");
        var source = Path.Combine(dir, "notes");
        Directory.CreateDirectory(source);
        var path = Path.Combine(source, "note.md");
        await File.WriteAllTextAsync(path, "# Note\n\nuniquestartup content");
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = source } } };
            config.Indexing.EmbeddingEnabled = false;
            config.Search.EnableLexical = true;
            var manager = new ConfigManager(config);
            var services = new ServiceCollection();
            services.AddSingleton(coordinator);
            services.AddSingleton(new LexicalFileIndexer(new MarkdownChunker(), coordinator));
            await using var provider = services.BuildServiceProvider();
            var vector = new Mock<IVectorStore>(MockBehavior.Strict);
            var pipeline = new Mock<IFileIndexPipeline>(MockBehavior.Strict);
            var guard = new Mock<IFileProcessingGuard>();
            guard.Setup(g => g.TryAcquire(It.IsAny<string>())).Returns(true);
            var events = new Mock<IIndexEventPublisher>();
            events.Setup(e => e.PublishAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var indexService = new IndexService(provider, events.Object, manager, pipeline.Object, guard.Object, NullLogger<IndexService>.Instance);
            var startup = new StartupSyncService(vector.Object, manager, indexService, pipeline.Object, NullLogger<StartupSyncService>.Instance);
            var completion = new TaskCompletionSource<StartupSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            startup.SyncCompleted += (_, args) => completion.TrySetResult(args.Result);
            await startup.StartAsync(CancellationToken.None);
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(result.Success);
            var file = await coordinator.GetActiveFileByPathAsync(path);
            Assert.NotNull(file);
            Assert.Single(await coordinator.SearchAsync("uniquestartup", new[] { file!.Id }, 10));
            await startup.StopAsync(CancellationToken.None);
            vector.VerifyNoOtherCalls();
            pipeline.VerifyNoOtherCalls();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }
}
