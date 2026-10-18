using Moq;
using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Business.Features.Indexing.StateMachine;
using ReferenceRAG.Business.Features.Indexing.Strategies;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Service.Hubs;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class IndexArchitectureTests
{
    private sealed class TestEventPublisher : IIndexEventPublisher
    {
        public List<(string Name, object Payload)> Published { get; } = new();

        public Task PublishAsync(string name, object payload, CancellationToken cancellationToken = default)
        {
            Published.Add((name, payload));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void IndexJobStateMachine_LifecycleTransitions_CalculateProgressSmoothly()
    {
        var job = new IndexJob { Id = "test-job-123" };
        var publisher = new TestEventPublisher();
        var sm = new IndexJobStateMachine(job, publisher);

        Assert.Equal(IndexJobPhase.Queued, sm.CurrentPhase);
        Assert.Equal(0.0, job.ProgressPercent);

        // 1. Scanning
        sm.TransitionTo(IndexJobPhase.Scanning, "扫描文件变动");
        Assert.Equal(IndexJobPhase.Scanning, sm.CurrentPhase);
        Assert.Equal(2.0, job.ProgressPercent);

        // 2. Preparing
        sm.TotalFiles = 10;
        sm.TransitionTo(IndexJobPhase.Preparing, "准备文件分块 (0/10)");
        Assert.Equal(2.0, job.ProgressPercent);

        sm.ReportPreparingProgress("doc1.md", 5, 10);
        // preparing: 2.0 + (18.0 * 0.5) = 11.0%
        Assert.Equal(11.0, job.ProgressPercent);
        Assert.Equal("doc1.md", job.CurrentFile);

        // 3. Embedding
        sm.TotalChunks = 100;
        sm.TransitionTo(IndexJobPhase.Embedding, "向量推理 (共 100 分块)");
        Assert.Equal(20.0, job.ProgressPercent);

        sm.ReportEmbeddingProgress(5, 10, 50, 100);
        // embedding: 20.0 + (65.0 * 0.5) = 52.5%
        Assert.Equal(52.5, job.ProgressPercent);

        // 4. Finalizing
        sm.TransitionTo(IndexJobPhase.Finalizing, "收尾后处理");
        Assert.Equal(88.0, job.ProgressPercent);

        sm.ReportFinalizingProgress("doc1.md", 5, 10);
        // finalizing: 88.0 + (11.0 * 0.5) = 93.5%
        Assert.Equal(93.5, job.ProgressPercent);

        // 5. Completed
        sm.ReportCompleted(TimeSpan.FromSeconds(5));
        Assert.Equal(IndexJobPhase.Completed, sm.CurrentPhase);
        Assert.Equal(IndexStatus.Completed, job.Status);
        Assert.Equal(100.0, job.ProgressPercent);

        // Verify events were published
        Assert.Contains(publisher.Published, p => p.Name == "IndexProgress");
        Assert.Contains(publisher.Published, p => p.Name == "IndexCompleted");
    }

    [Fact]
    public void IndexJobStateMachine_Faulted_MarksFailedAndPublishesCompletedEvent()
    {
        var job = new IndexJob { Id = "test-job-fault" };
        var publisher = new TestEventPublisher();
        var sm = new IndexJobStateMachine(job, publisher);

        sm.ReportFaulted(new InvalidOperationException("Fatal database error"), "IndexFailure");

        Assert.Equal(IndexJobPhase.Faulted, sm.CurrentPhase);
        Assert.Equal(IndexStatus.Failed, job.Status);
        Assert.Equal("IndexFailure: Fatal database error", job.ErrorMessage);
        Assert.Contains(publisher.Published, p => p.Name == "IndexCompleted");
    }

    [Fact]
    public async Task SqliteIndexStorageAdapter_SafeUpsertChunksAsync_AutoHealsMissingParentRecord()
    {
        var vectorStore = new Mock<IVectorStore>();
        var pipeline = new Mock<IFileIndexPipeline>();
        var guard = new Mock<IFileProcessingGuard>();

        FileRecord? savedFile = null;
        vectorStore
            .Setup(v => v.GetFileAsync("file-orphan", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FileRecord?)null);

        vectorStore
            .Setup(v => v.UpsertFileAsync(It.IsAny<FileRecord>(), It.IsAny<CancellationToken>()))
            .Callback<FileRecord, CancellationToken>((f, _) => savedFile = f)
            .Returns(Task.CompletedTask);

        List<ChunkRecord>? upsertedChunks = null;
        vectorStore
            .Setup(v => v.UpsertChunksAsync(It.IsAny<IEnumerable<ChunkRecord>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChunkRecord>, CancellationToken>((c, _) => upsertedChunks = c.ToList())
            .Returns(Task.CompletedTask);

        var adapter = new SqliteIndexStorageAdapter(vectorStore.Object, pipeline.Object, guard.Object);

        var orphanChunk = new ChunkRecord
        {
            Id = "chunk-1",
            FileId = "file-orphan",
            Content = "Test content",
            EnhancedContent = "Test content"
        };

        await adapter.SafeUpsertChunksAsync(new[] { orphanChunk });

        // Assert: The adapter detected missing parent record and auto-healed it before inserting chunk
        Assert.NotNull(savedFile);
        Assert.Equal("file-orphan", savedFile.Id);
        Assert.NotNull(upsertedChunks);
        Assert.Single(upsertedChunks);
    }

    [Fact]
    public void SqliteIndexStorageAdapter_NormalizePath_StandardizesSlashes()
    {
        var adapter = new SqliteIndexStorageAdapter(
            new Mock<IVectorStore>().Object,
            new Mock<IFileIndexPipeline>().Object,
            new Mock<IFileProcessingGuard>().Object);

        var path1 = @"C:\Test\Folder\file.txt";
        var path2 = @"C:/Test/Folder/file.txt";

        Assert.Equal(adapter.NormalizePath(path1), adapter.NormalizePath(path2));
    }

    [Fact]
    public async Task SqliteIndexStorageAdapter_CleanDeletedFilesSafeAsync_PreservesActiveAndExistingFiles()
    {
        var vectorStore = new Mock<IVectorStore>();
        var pipeline = new Mock<IFileIndexPipeline>();
        var guard = new Mock<IFileProcessingGuard>();

        guard.Setup(g => g.TryAcquire(It.IsAny<string>())).Returns(true);

        var tempFile = Path.GetTempFileName();
        try
        {
            var dbFiles = new List<FileRecord>
            {
                // File 1: In active files list (processing now) -> Do not delete!
                new() { Id = "f1", Path = "C:/docs/active.md", Source = "default" },
                // File 2: Exists on disk -> Do not delete!
                new() { Id = "f2", Path = tempFile, Source = "default" },
                // File 3: Truly deleted from disk and not active -> Delete!
                new() { Id = "f3", Path = "C:/docs/truly_deleted_9999.md", Source = "default" }
            };

            vectorStore
                .Setup(v => v.GetAllFilesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(dbFiles);

            var deletedIds = new List<string>();
            pipeline
                .Setup(p => p.DeleteFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, string, CancellationToken>((id, _, _) => deletedIds.Add(id))
                .Returns(Task.CompletedTask);

            var adapter = new SqliteIndexStorageAdapter(vectorStore.Object, pipeline.Object, guard.Object);

            var cleaned = await adapter.CleanDeletedFilesSafeAsync(
                new[] { "default" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase), // disk has 0 matches
                new HashSet<string>(new[] { "C:/docs/active.md" }, StringComparer.OrdinalIgnoreCase));

            Assert.Equal(1, cleaned);
            Assert.Contains("f3", deletedIds);
            Assert.DoesNotContain("f1", deletedIds);
            Assert.DoesNotContain("f2", deletedIds);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void IndexStrategyFactory_ResolvesAppropriateStrategy()
    {
        var incremental = new IncrementalIndexStrategy();
        var rebuild = new RebuildIndexStrategy();
        var vectorOnly = new VectorOnlyIndexStrategy();
        var rechunk = new RechunkIndexStrategy();

        var factory = new IndexStrategyFactory(new IIndexExecutionStrategy[] { incremental, rebuild, vectorOnly, rechunk });

        // 1. VectorOnly
        var strat1 = factory.GetStrategy(new IndexRequest { VectorOnly = true });
        Assert.IsType<VectorOnlyIndexStrategy>(strat1);

        // 2. Rechunk
        var strat2 = factory.GetStrategy(new IndexRequest { Rechunk = true });
        Assert.IsType<RechunkIndexStrategy>(strat2);

        // 3. Rebuild (Force = true)
        var strat3 = factory.GetStrategy(new IndexRequest { Force = true });
        Assert.IsType<RebuildIndexStrategy>(strat3);

        // 4. Incremental (Normal)
        var strat4 = factory.GetStrategy(new IndexRequest { Force = false, VectorOnly = false, Rechunk = false });
        Assert.IsType<IncrementalIndexStrategy>(strat4);
    }

    [Fact]
    public async Task Rechunk_Workflow_ReplacesOldChunksAndVectorsWithoutDoubling()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"test_rechunk_{Guid.NewGuid():N}.db");
        try
        {
            using var store = new ReferenceRAG.Storage.SqliteVectorStore(tempDb, dimension: 1024);

            var fileId = "file-test-1";
            var model = "Qwen3-Embedding-0.6B";

            // 1. Initial indexing: 1 file, 2 chunks, 2 vectors
            var file = new FileRecord
            {
                Id = fileId,
                Path = "E:\\Content\\Down\\doc.md",
                Source = "test-source",
                ChunkCount = 2,
                IndexedStatus = "complete"
            };
            await store.UpsertFileAsync(file);

            var oldChunk1 = new ChunkRecord { Id = "chunk-old-1", FileId = fileId, Content = "Old 1" };
            var oldChunk2 = new ChunkRecord { Id = "chunk-old-2", FileId = fileId, Content = "Old 2" };
            await store.UpsertChunksAsync(new[] { oldChunk1, oldChunk2 });

            var oldVec1 = new VectorRecord { Id = "vec-old-1", ChunkId = "chunk-old-1", ModelName = model, Vector = new float[1024] };
            var oldVec2 = new VectorRecord { Id = "vec-old-2", ChunkId = "chunk-old-2", ModelName = model, Vector = new float[1024] };
            await store.UpsertVectorsAsync(new[] { oldVec1, oldVec2 });

            var stats1 = await store.GetVectorStatsAsync();
            Assert.Equal(2, stats1.First(s => s.ModelName == model).VectorCount);

            // 2. Rechunk occurs:
            // PrepareAsync gets existing file and old chunks
            var existing = await store.GetFileByPathAsync("E:\\Content\\Down\\doc.md");
            Assert.NotNull(existing);
            var oldChunks = (await store.GetChunksByFileAsync(existing.Id)).Select(c => c.Id).ToList();
            Assert.Equal(2, oldChunks.Count);

            // New chunks generated with new IDs
            var newChunk1 = new ChunkRecord { Id = "chunk-new-1", FileId = fileId, Content = "New 1" };
            var newChunk2 = new ChunkRecord { Id = "chunk-new-2", FileId = fileId, Content = "New 2" };
            await store.UpsertChunksAsync(new[] { newChunk1, newChunk2 });

            var newVec1 = new VectorRecord { Id = "vec-new-1", ChunkId = "chunk-new-1", ModelName = model, Vector = new float[1024] };
            var newVec2 = new VectorRecord { Id = "vec-new-2", ChunkId = "chunk-new-2", ModelName = model, Vector = new float[1024] };
            await store.UpsertVectorsAsync(new[] { newVec1, newVec2 });

            // Finalize: delete old chunks
            foreach (var oldId in oldChunks)
            {
                await store.DeleteChunkAsync(oldId);
            }

            var stats2 = await store.GetVectorStatsAsync();
            Assert.Equal(2, stats2.First(s => s.ModelName == model).VectorCount);
        }
        finally
        {
            if (File.Exists(tempDb))
                try { File.Delete(tempDb); } catch { }
        }
    }
}
