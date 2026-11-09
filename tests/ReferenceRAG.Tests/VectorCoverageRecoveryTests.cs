using System.Security.Cryptography;
using System.Text;
using Moq;
using ReferenceRAG.Business.Features.Indexing.Services;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Storage;
using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Business.Features.Indexing.StateMachine;
using ReferenceRAG.Business.Features.Indexing.Strategies;
using ReferenceRAG.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Extensions;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Service.Hubs;

namespace ReferenceRAG.Tests;

public class VectorCoverageRecoveryTests
{
    [Fact]
    public async Task FullRebuildRestoresVectorQueriesOnlyAfterRealIndexesAreVerified()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var root = Path.Combine(Path.GetTempPath(), $"coverage-rebuild-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "source");
        Directory.CreateDirectory(sourcePath);
        try
        {
            var path = Path.Combine(sourcePath, "note.md");
            await File.WriteAllTextAsync(path, "# Rebuild note\n\nThis paragraph contains substantial recovery verification content for the test document.");
            var config = new ObsidianRagConfig { DataPath = root, Sources = new() { new SourceFolder { Name = "notes", Path = sourcePath } } };
            config.Search.EnableLexical = true;
            config.Indexing.EmbeddingEnabled = false;
            var manager = new ConfigManager(config);
            using var vectorStore = new SqliteVectorStore(Path.Combine(root, "vectors.db"), 3);
            using var bm25 = new Fts5BM25Store(Path.Combine(root, "bm25.db"));
            using var graph = new SqliteGraphStore(Path.Combine(root, "graph.db"));
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(root, "vectors.db"), Path.Combine(root, "bm25.db"));
            await new LexicalFileIndexer(new MarkdownChunker(), coordinator, manager).IndexAsync(path, "notes");
            Assert.Contains("notes", await coordinator.GetStaleVectorSourcesAsync());
            config.Indexing.EmbeddingEnabled = true;
            config.Search.EnableLexical = false;
            var embedding = new Mock<IEmbeddingService>();
            embedding.SetupGet(e => e.ModelName).Returns("recovery-model");
            embedding.SetupGet(e => e.Dimension).Returns(3);
            embedding.Setup(e => e.EncodeBatchAsync(It.IsAny<IEnumerable<string>>(), EmbeddingMode.Document, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<string> input, EmbeddingMode _, CancellationToken _) => input.Select(_ => new float[] { 1, 0, 0 }).ToArray());
            var enhancer = new Mock<ITextEnhancer>();
            enhancer.Setup(e => e.Enhance(It.IsAny<ChunkRecord>(), It.IsAny<FileRecord>())).Returns<ChunkRecord, FileRecord>((c, _) => c.Content);
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton(manager);
            services.AddSingleton(coordinator);
            services.AddSingleton<IVectorStore>(vectorStore);
            services.AddSingleton<IBM25Store>(bm25);
            services.AddSingleton<IGraphStore>(graph);
            services.AddSingleton<IMarkdownChunker>(new MarkdownChunker());
            services.AddSingleton(embedding.Object);
            services.AddSingleton(enhancer.Object);
            services.AddIndexingPipeline();
            await using var provider = services.BuildServiceProvider();
            var pipeline = provider.GetRequiredService<IFileIndexPipeline>();
            var service = new IndexService(provider, new Mock<IIndexEventPublisher>().Object, manager, pipeline,
                provider.GetRequiredService<IFileProcessingGuard>(), NullLogger<IndexService>.Instance,
                provider.GetRequiredService<IIndexStorageAdapter>(), provider.GetRequiredService<IndexStrategyFactory>());
            var job = await service.StartIndexAsync(new IndexRequest { Force = true, Sources = new() { "notes" } });
            await service.WaitForCompletionAsync(job).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(job.Status == IndexStatus.Completed, job.ErrorMessage);
            Assert.Empty(await coordinator.GetStaleVectorSourcesAsync());
            using (await coordinator.AcquireVectorQueryLeaseAsync(new[] { "notes" }, false)) { }
            var indexedFile = await vectorStore.GetFileByPathAsync(path);
            Assert.NotNull(indexedFile);
            Assert.NotEmpty(await vectorStore.GetChunksByFileAsync(indexedFile!.Id));
            await File.WriteAllTextAsync(path, "# Updated note\n\nA later update makes the previous vector generation obsolete and requires another rebuild.");
            await provider.GetRequiredService<LexicalFileIndexer>().IndexAsync(path, "notes");
            embedding.Setup(e => e.EncodeBatchAsync(It.IsAny<IEnumerable<string>>(), EmbeddingMode.Document, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("injected embedding failure"));
            var failed = await service.StartIndexAsync(new IndexRequest { Force = true });
            await service.WaitForCompletionAsync(failed).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(IndexStatus.Failed, failed.Status);
            Assert.Contains("*", await coordinator.GetStaleVectorSourcesAsync());
            await Assert.ThrowsAsync<SearchCapabilityException>(() => coordinator.AcquireVectorQueryLeaseAsync(new[] { "notes" }, false));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RecoveryRebuildRejectsSkippedFilesBeforePublishingCompletion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"coverage-busy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "note.md");
            await File.WriteAllTextAsync(path, "busy file content");
            var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = root } } };
            var adapter = new Mock<IIndexStorageAdapter>();
            var guard = new Mock<IFileProcessingGuard>();
            guard.Setup(g => g.TryAcquire(path)).Returns(false);
            var job = new IndexJob { Request = new IndexRequest { Force = true } };
            var context = new IndexExecutionContext
            {
                Request = job.Request, StorageAdapter = adapter.Object, Pipeline = new Mock<IFileIndexPipeline>().Object,
                VectorStore = new Mock<IVectorStore>().Object, EmbeddingService = new Mock<IEmbeddingService>().Object,
                ConfigManager = new ConfigManager(config), Guard = guard.Object,
                ActiveFiles = new ConcurrentDictionary<string, byte>(), HeldFiles = new ConcurrentDictionary<string, byte>(),
                Logger = NullLogger.Instance, StrictRecovery = true
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => new RebuildIndexStrategy().ExecuteAsync(context, new IndexJobStateMachine(job), CancellationToken.None));
            Assert.NotEqual(IndexStatus.Completed, job.Status);
            adapter.Verify(a => a.CleanDeletedFilesSafeAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<ISet<string>>(), It.IsAny<ISet<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RecoveryRebuildDoesNotReportSuccessWhenSourceCannotBeScanned()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-coverage-{Guid.NewGuid():N}");
        var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = missing } } };
        var adapter = new Mock<IIndexStorageAdapter>();
        adapter.Setup(a => a.NormalizePath(It.IsAny<string>())).Returns<string>(p => p);
        var job = new IndexJob { Request = new IndexRequest { Force = true } };
        var state = new IndexJobStateMachine(job);
        var context = new IndexExecutionContext
        {
            Request = job.Request, StorageAdapter = adapter.Object, Pipeline = new Mock<IFileIndexPipeline>().Object,
            VectorStore = new Mock<IVectorStore>().Object, EmbeddingService = new Mock<IEmbeddingService>().Object,
            ConfigManager = new ConfigManager(config), Guard = new FileProcessingGuard(),
            ActiveFiles = new ConcurrentDictionary<string, byte>(), HeldFiles = new ConcurrentDictionary<string, byte>(),
            Logger = NullLogger.Instance, StrictRecovery = true
        };
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => new RebuildIndexStrategy().ExecuteAsync(context, state, CancellationToken.None));
        Assert.NotEqual(IndexStatus.Completed, job.Status);
    }

    [Fact]
    public async Task RecoveryPublishesOnlyWhenFilesVectorsAndBm25Match()
    {
        var root = Path.Combine(Path.GetTempPath(), $"coverage-verify-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "source");
        Directory.CreateDirectory(sourcePath);
        var path = Path.Combine(sourcePath, "note.md");
        const string text = "# Note\n\ncoverage verification";
        try
        {
            await File.WriteAllTextAsync(path, text);
            var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = sourcePath } } };
            var file = new FileRecord { Id = "f", Path = path, Source = "notes", IndexedStatus = "complete", ChunkCount = 1,
                ContentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) };
            var chunk = new ChunkRecord { Id = "c", FileId = "f", Content = text };
            var vectors = new Dictionary<string, VectorRecord> { ["c"] = new() { ChunkId = "c", ModelName = "model", Dimension = 2, Vector = new float[] { 1, 0 } } };
            var store = new Mock<IVectorStore>();
            store.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { file });
            store.Setup(s => s.GetChunksByFileIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { chunk });
            store.Setup(s => s.GetVectorsByChunkIdsAsync(It.IsAny<IEnumerable<string>>(), "model", It.IsAny<CancellationToken>())).ReturnsAsync(() => vectors);
            var embedding = new Mock<IEmbeddingService>();
            embedding.SetupGet(s => s.ModelName).Returns("model");
            embedding.SetupGet(s => s.Dimension).Returns(2);
            using var bm25 = new Fts5BM25Store(Path.Combine(root, "bm25.db"));
            await bm25.IndexDocumentAsync(chunk.Id, chunk.Content);
            var verifier = new VectorCoverageVerifier(store.Object, bm25, embedding.Object, new FileProcessingGuard());
            var committed = false;
            await verifier.VerifyAndCommitAsync(config, () => { committed = true; return Task.CompletedTask; });
            Assert.True(committed);
            vectors.Clear();
            committed = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.VerifyAndCommitAsync(config, () => { committed = true; return Task.CompletedTask; }));
            Assert.False(committed);
            await File.WriteAllTextAsync(path, text);
            vectors["c"] = new VectorRecord { ChunkId = "c", ModelName = "model", Dimension = 2, Vector = new float[] { 1, 0 } };
            var changingBm25 = new Mock<IBM25Store>();
            changingBm25.Setup(s => s.VerifyExactIndexAsync(It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()))
                .Returns(async () => { await File.WriteAllTextAsync(path, "changed during verification"); return true; });
            var racing = new VectorCoverageVerifier(store.Object, changingBm25.Object, embedding.Object, new FileProcessingGuard());
            await Assert.ThrowsAsync<InvalidOperationException>(() => racing.VerifyAndCommitAsync(config, () => { committed = true; return Task.CompletedTask; }));
            Assert.False(committed);
            await File.WriteAllTextAsync(path, "changed on disk");
            await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.VerifyAndCommitAsync(config, () => { committed = true; return Task.CompletedTask; }));
            Assert.False(committed);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task P01_SourceDeletion_FileWatcher_And_PartialJob_Concurrency()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var root = Path.Combine(Path.GetTempPath(), $"p01-partial-{Guid.NewGuid():N}");
        var sourceA = Path.Combine(root, "source-a");
        var sourceB = Path.Combine(root, "source-b");
        Directory.CreateDirectory(sourceA);
        Directory.CreateDirectory(sourceB);

        var pathA = Path.Combine(sourceA, "note-a.md");
        var pathB = Path.Combine(sourceB, "note-b.md");
        await File.WriteAllTextAsync(pathA, "# Note A\n\np01_old_token\n\nSubstantial content for note A.");
        await File.WriteAllTextAsync(pathB, "# Note B\n\np01_b_token\n\nSubstantial content for note B.");

        var config = new ObsidianRagConfig
        {
            DataPath = root,
            Sources = new()
            {
                new SourceFolder { Name = "A", Path = sourceA },
                new SourceFolder { Name = "B", Path = sourceB }
            }
        };
        var manager = new ConfigManager(config);
        using var vectorStore = new SqliteVectorStore(Path.Combine(root, "vectors.db"), 3);
        using var bm25 = new Fts5BM25Store(Path.Combine(root, "bm25.db"));
        using var graph = new SqliteGraphStore(Path.Combine(root, "graph.db"));
        await using var coordinator = new LexicalIndexCoordinator(Path.Combine(root, "vectors.db"), Path.Combine(root, "bm25.db"));

        var embedding = new Mock<IEmbeddingService>();
        embedding.SetupGet(e => e.ModelName).Returns("recovery-model");
        embedding.SetupGet(e => e.Dimension).Returns(3);
        embedding.Setup(e => e.EncodeBatchAsync(It.IsAny<IEnumerable<string>>(), EmbeddingMode.Document, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> input, EmbeddingMode _, CancellationToken _) => input.Select(_ => new float[] { 1, 0, 0 }).ToArray());

        var enhancer = new Mock<ITextEnhancer>();
        enhancer.Setup(e => e.Enhance(It.IsAny<ChunkRecord>(), It.IsAny<FileRecord>())).Returns<ChunkRecord, FileRecord>((c, _) => c.Content);

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(manager);
        services.AddSingleton(coordinator);
        services.AddSingleton<IVectorStore>(vectorStore);
        services.AddSingleton<IBM25Store>(bm25);
        services.AddSingleton<IGraphStore>(graph);
        services.AddSingleton(embedding.Object);
        services.AddSingleton<IMarkdownChunker>(new MarkdownChunker());
        services.AddSingleton(enhancer.Object);
        services.AddIndexingPipeline();

        await using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IFileIndexPipeline>();
        var service = new IndexService(provider, new Mock<IIndexEventPublisher>().Object, manager, pipeline,
            provider.GetRequiredService<IFileProcessingGuard>(), NullLogger<IndexService>.Instance,
            provider.GetRequiredService<IIndexStorageAdapter>(), provider.GetRequiredService<IndexStrategyFactory>());

        try
        {
            // Initial build
            var initJob = await service.StartIndexAsync(new IndexRequest { Force = true });
            await service.WaitForCompletionAsync(initJob);
            Assert.Equal(IndexStatus.Completed, initJob.Status);

            // Stale A
            await new LexicalFileIndexer(new MarkdownChunker(), coordinator, manager).IndexAsync(pathA, "A");
            Assert.Contains("A", await coordinator.GetStaleVectorSourcesAsync());

            // Pause Point A
            var reachedPauseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowContinueA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IndexService.AfterRecoveryLeaseAcquiredHook = async () =>
            {
                reachedPauseA.TrySetResult();
                await allowContinueA.Task.WaitAsync(TimeSpan.FromSeconds(10));
            };

            var recoveryJob = await service.StartIndexAsync(new IndexRequest { Force = true });
            await reachedPauseA.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // While recovery is held at Pause A, attempt DeleteSourceAsync on B
            var deleteSourceTask = Task.Run(async () =>
            {
                await pipeline.DeleteSourceAsync("B");
            });

            await Task.Delay(100);
            Assert.False(deleteSourceTask.IsCompleted, "DeleteSourceAsync should queue behind recovery lease");

            // Attempt partial index job for A
            var partialJob = await service.StartIndexAsync(new IndexRequest { Sources = new() { "A" }, Force = true });
            await Task.Delay(100);
            Assert.NotEqual(IndexStatus.Completed, partialJob.Status);

            // Release Pause A
            allowContinueA.TrySetResult();

            await service.WaitForCompletionAsync(recoveryJob);
            Assert.Equal(IndexStatus.Completed, recoveryJob.Status);

            await deleteSourceTask.WaitAsync(TimeSpan.FromSeconds(10));
            await service.WaitForCompletionAsync(partialJob).WaitAsync(TimeSpan.FromSeconds(10));

            // Partial job and deleted source B cannot lift global protection falsely
            var staleAfter = await coordinator.GetStaleVectorSourcesAsync();
            // Source B was deleted via DeleteSourceAsync, so B is marked stale
            Assert.Contains("B", staleAfter);
        }
        finally
        {
            IndexService.AfterRecoveryLeaseAcquiredHook = null;
            IndexService.BeforeCommitVerifiedHook = null;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task P01_SourceConfig_DiskContent_And_Model_Modifications()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var root = Path.Combine(Path.GetTempPath(), $"p01-config-{Guid.NewGuid():N}");
        var sourceA = Path.Combine(root, "source-a");
        Directory.CreateDirectory(sourceA);

        var pathA = Path.Combine(sourceA, "note-a.md");
        await File.WriteAllTextAsync(pathA, "# Note A\n\np01_token\n\nSubstantial text for note A.");

        var config = new ObsidianRagConfig
        {
            DataPath = root,
            Sources = new() { new SourceFolder { Name = "A", Path = sourceA } }
        };
        var manager = new ConfigManager(config);
        using var vectorStore = new SqliteVectorStore(Path.Combine(root, "vectors.db"), 3);
        using var bm25 = new Fts5BM25Store(Path.Combine(root, "bm25.db"));
        using var graph = new SqliteGraphStore(Path.Combine(root, "graph.db"));
        await using var coordinator = new LexicalIndexCoordinator(Path.Combine(root, "vectors.db"), Path.Combine(root, "bm25.db"));

        var embedding = new Mock<IEmbeddingService>();
        embedding.SetupGet(e => e.ModelName).Returns("recovery-model");
        embedding.SetupGet(e => e.Dimension).Returns(3);
        embedding.Setup(e => e.EncodeBatchAsync(It.IsAny<IEnumerable<string>>(), EmbeddingMode.Document, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> input, EmbeddingMode _, CancellationToken _) => input.Select(_ => new float[] { 1, 0, 0 }).ToArray());

        var enhancer = new Mock<ITextEnhancer>();
        enhancer.Setup(e => e.Enhance(It.IsAny<ChunkRecord>(), It.IsAny<FileRecord>())).Returns<ChunkRecord, FileRecord>((c, _) => c.Content);

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(manager);
        services.AddSingleton(coordinator);
        services.AddSingleton<IVectorStore>(vectorStore);
        services.AddSingleton<IBM25Store>(bm25);
        services.AddSingleton<IGraphStore>(graph);
        services.AddSingleton(embedding.Object);
        services.AddSingleton<IMarkdownChunker>(new MarkdownChunker());
        services.AddSingleton(enhancer.Object);
        services.AddIndexingPipeline();

        await using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IFileIndexPipeline>();
        var service = new IndexService(provider, new Mock<IIndexEventPublisher>().Object, manager, pipeline,
            provider.GetRequiredService<IFileProcessingGuard>(), NullLogger<IndexService>.Instance,
            provider.GetRequiredService<IIndexStorageAdapter>(), provider.GetRequiredService<IndexStrategyFactory>());

        try
        {
            // Initial build & stale
            var initJob = await service.StartIndexAsync(new IndexRequest { Force = true });
            await service.WaitForCompletionAsync(initJob);
            await new LexicalFileIndexer(new MarkdownChunker(), coordinator, manager).IndexAsync(pathA, "A");

            // Pause Point B: modify disk files during pause B
            var reachedPauseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowContinueB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IndexService.BeforeCommitVerifiedHook = async () =>
            {
                reachedPauseB.TrySetResult();
                await allowContinueB.Task.WaitAsync(TimeSpan.FromSeconds(10));
            };

            var rebuildJob = await service.StartIndexAsync(new IndexRequest { Force = true });
            await reachedPauseB.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Create a new file on disk during Pause Point B
            await File.WriteAllTextAsync(Path.Combine(sourceA, "new-note.md"), "# New\n\nNew file during pause B.");

            // Release B
            allowContinueB.TrySetResult();

            await service.WaitForCompletionAsync(rebuildJob);
            // Rebuild must fail because disk inventory changed before commit!
            Assert.Equal(IndexStatus.Failed, rebuildJob.Status);
            // Stale marker remains intact!
            Assert.Contains("*", await coordinator.GetStaleVectorSourcesAsync());
        }
        finally
        {
            IndexService.AfterRecoveryLeaseAcquiredHook = null;
            IndexService.BeforeCommitVerifiedHook = null;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task P01_DuplicateTrigger_Cancellation_And_FaultInjection()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var root = Path.Combine(Path.GetTempPath(), $"p01-cancel-{Guid.NewGuid():N}");
        var sourceA = Path.Combine(root, "source-a");
        Directory.CreateDirectory(sourceA);

        var pathA = Path.Combine(sourceA, "note-a.md");
        await File.WriteAllTextAsync(pathA, "# Note A\n\np01_token\n\nSubstantial text for note A.");

        var config = new ObsidianRagConfig
        {
            DataPath = root,
            Sources = new() { new SourceFolder { Name = "A", Path = sourceA } }
        };
        var manager = new ConfigManager(config);
        using var vectorStore = new SqliteVectorStore(Path.Combine(root, "vectors.db"), 3);
        using var bm25 = new Fts5BM25Store(Path.Combine(root, "bm25.db"));
        using var graph = new SqliteGraphStore(Path.Combine(root, "graph.db"));
        await using var coordinator = new LexicalIndexCoordinator(Path.Combine(root, "vectors.db"), Path.Combine(root, "bm25.db"));

        var embedding = new Mock<IEmbeddingService>();
        embedding.SetupGet(e => e.ModelName).Returns("recovery-model");
        embedding.SetupGet(e => e.Dimension).Returns(3);
        embedding.Setup(e => e.EncodeBatchAsync(It.IsAny<IEnumerable<string>>(), EmbeddingMode.Document, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> input, EmbeddingMode _, CancellationToken _) => input.Select(_ => new float[] { 1, 0, 0 }).ToArray());

        var enhancer = new Mock<ITextEnhancer>();
        enhancer.Setup(e => e.Enhance(It.IsAny<ChunkRecord>(), It.IsAny<FileRecord>())).Returns<ChunkRecord, FileRecord>((c, _) => c.Content);

        var publisher = new Mock<IIndexEventPublisher>();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(manager);
        services.AddSingleton(coordinator);
        services.AddSingleton<IVectorStore>(vectorStore);
        services.AddSingleton<IBM25Store>(bm25);
        services.AddSingleton<IGraphStore>(graph);
        services.AddSingleton(embedding.Object);
        services.AddSingleton<IMarkdownChunker>(new MarkdownChunker());
        services.AddSingleton(enhancer.Object);
        services.AddSingleton(publisher.Object);
        services.AddIndexingPipeline();

        await using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IFileIndexPipeline>();
        var service = new IndexService(provider, publisher.Object, manager, pipeline,
            provider.GetRequiredService<IFileProcessingGuard>(), NullLogger<IndexService>.Instance,
            provider.GetRequiredService<IIndexStorageAdapter>(), provider.GetRequiredService<IndexStrategyFactory>());

        try
        {
            // Initial build & stale
            var initJob = await service.StartIndexAsync(new IndexRequest { Force = true });
            await service.WaitForCompletionAsync(initJob);
            await new LexicalFileIndexer(new MarkdownChunker(), coordinator, manager).IndexAsync(pathA, "A");

            // Pause Point A
            var reachedPauseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowContinueA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IndexService.AfterRecoveryLeaseAcquiredHook = async () =>
            {
                reachedPauseA.TrySetResult();
                await allowContinueA.Task.WaitAsync(TimeSpan.FromSeconds(10));
            };

            var job1 = await service.StartIndexAsync(new IndexRequest { Force = true });
            await reachedPauseA.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Duplicate trigger: start job2 while job1 is in recovery zone
            var job2 = await service.StartIndexAsync(new IndexRequest { Force = true });
            await Task.Delay(100);
            Assert.NotEqual(IndexStatus.Completed, job2.Status);

            // Cancel both jobs before commit
            await service.StopIndexAsync(job2.Id);
            await service.StopIndexAsync(job1.Id);
            allowContinueA.TrySetResult();

            await service.WaitForCompletionAsync(job1);
            await service.WaitForCompletionAsync(job2);
            Assert.Equal(IndexStatus.Cancelled, job1.Status);
            Assert.Equal(IndexStatus.Cancelled, job2.Status);

            // Verify cancellation did NOT publish IndexCompleted with Success=true
            publisher.Verify(p => p.PublishAsync("IndexCompleted", It.Is<IndexCompletedEvent>(e => (e.IndexId == job1.Id || e.IndexId == job2.Id) && e.Success), It.IsAny<CancellationToken>()), Times.Never);

            // Across DB close/reopen: stale remains
            Assert.True(LexicalIndexCoordinator.HasPersistedStaleVectors(Path.Combine(root, "vectors.db")));

            // Full retry succeeds and clears stale
            var retryJob = await service.StartIndexAsync(new IndexRequest { Force = true });
            await service.WaitForCompletionAsync(retryJob);
            Assert.Equal(IndexStatus.Completed, retryJob.Status);
            Assert.False(LexicalIndexCoordinator.HasPersistedStaleVectors(Path.Combine(root, "vectors.db")));
        }
        finally
        {
            IndexService.AfterRecoveryLeaseAcquiredHook = null;
            IndexService.BeforeCommitVerifiedHook = null;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
