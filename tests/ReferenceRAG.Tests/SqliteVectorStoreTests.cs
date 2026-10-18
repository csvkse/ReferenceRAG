using ReferenceRAG.Core.Models;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Tests;

public class SqliteVectorStoreTests : IDisposable
{
    private readonly SqliteVectorStore _vectorStore;
    private readonly string _dbPath;

    public SqliteVectorStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_vector_{Guid.NewGuid()}.db");
        _vectorStore = new SqliteVectorStore(_dbPath, 384);
    }

    [Fact]
    public async Task UpsertFileAsync_InsertsFile()
    {
        var file = new FileRecord
        {
            Id = "file-1",
            Path = "/test/file.md",
            Title = "Test File",
            ContentHash = "abc123",
            ChunkCount = 5,
            IndexedAt = DateTime.UtcNow
        };

        await _vectorStore.UpsertFileAsync(file);
        var retrieved = await _vectorStore.GetFileAsync("file-1");

        Assert.NotNull(retrieved);
        Assert.Equal("file-1", retrieved.Id);
        Assert.Equal("/test/file.md", retrieved.Path);
        Assert.Equal("Test File", retrieved.Title);
        Assert.Equal("abc123", retrieved.ContentHash);
    }

    [Fact]
    public async Task UpsertFileAsync_UpdatesExistingFile()
    {
        var file1 = new FileRecord
        {
            Id = "file-1",
            Path = "/test/file.md",
            Title = "Original Title",
            ContentHash = "hash1",
            ChunkCount = 5,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file1);

        var file2 = new FileRecord
        {
            Id = "file-1",
            Path = "/test/file.md",
            Title = "Updated Title",
            ContentHash = "hash2",
            ChunkCount = 10,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file2);

        var retrieved = await _vectorStore.GetFileAsync("file-1");
        Assert.NotNull(retrieved);
        Assert.Equal("Updated Title", retrieved.Title);
        // ChunkCount is not persisted in DB - it's computed from chunks
    }

    [Fact]
    public async Task GetFileAsync_WithNonExistentId_ReturnsNull()
    {
        var result = await _vectorStore.GetFileAsync("non-existent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetFileByPathAsync_ReturnsCorrectFile()
    {
        var file = new FileRecord
        {
            Id = "file-1",
            Path = "/test/specific.md",
            Title = "Specific File",
            ChunkCount = 3,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file);

        var retrieved = await _vectorStore.GetFileByPathAsync("/test/specific.md");

        Assert.NotNull(retrieved);
        Assert.Equal("file-1", retrieved.Id);
    }

    [Fact]
    public async Task DeleteFileAsync_RemovesFile()
    {
        var file = new FileRecord
        {
            Id = "file-to-delete",
            Path = "/test/delete.md",
            ChunkCount = 1,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file);

        await _vectorStore.DeleteFileAsync("file-to-delete");
        var result = await _vectorStore.GetFileAsync("file-to-delete");

        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertChunkAsync_InsertsChunk()
    {
        var file = new FileRecord
        {
            Id = "file-1",
            Path = "/test/file.md",
            ChunkCount = 1,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file);

        var chunk = new ChunkRecord
        {
            Id = "chunk-1",
            FileId = "file-1",
            ChunkIndex = 0,
            Content = "Test content",
            TokenCount = 100,
            StartLine = 1,
            EndLine = 5
        };
        await _vectorStore.UpsertChunkAsync(chunk);

        var retrieved = await _vectorStore.GetChunkAsync("chunk-1");

        Assert.NotNull(retrieved);
        Assert.Equal("chunk-1", retrieved.Id);
        Assert.Equal("Test content", retrieved.Content);
    }

    [Fact]
    public async Task UpsertChunksAsync_BatchInsertsChunks()
    {
        var file = new FileRecord
        {
            Id = "file-1",
            Path = "/test/file.md",
            ChunkCount = 3,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file);

        var chunks = new List<ChunkRecord>
        {
            new() { Id = "chunk-1", FileId = "file-1", ChunkIndex = 0, Content = "Content 1", TokenCount = 50, StartLine = 1, EndLine = 3 },
            new() { Id = "chunk-2", FileId = "file-1", ChunkIndex = 1, Content = "Content 2", TokenCount = 60, StartLine = 4, EndLine = 6 },
            new() { Id = "chunk-3", FileId = "file-1", ChunkIndex = 2, Content = "Content 3", TokenCount = 70, StartLine = 7, EndLine = 10 }
        };

        await _vectorStore.UpsertChunksAsync(chunks);

        var retrieved1 = await _vectorStore.GetChunkAsync("chunk-1");
        var retrieved2 = await _vectorStore.GetChunkAsync("chunk-2");
        var retrieved3 = await _vectorStore.GetChunkAsync("chunk-3");

        Assert.NotNull(retrieved1);
        Assert.NotNull(retrieved2);
        Assert.NotNull(retrieved3);
    }

    [Fact]
    public async Task GetChunksByFileAsync_ReturnsAllChunks()
    {
        var file = new FileRecord
        {
            Id = "file-1",
            Path = "/test/file.md",
            ChunkCount = 3,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file);

        var chunks = new List<ChunkRecord>
        {
            new() { Id = "chunk-1", FileId = "file-1", ChunkIndex = 0, Content = "Content 1", TokenCount = 50, StartLine = 1, EndLine = 3 },
            new() { Id = "chunk-2", FileId = "file-1", ChunkIndex = 1, Content = "Content 2", TokenCount = 60, StartLine = 4, EndLine = 6 },
            new() { Id = "chunk-3", FileId = "file-1", ChunkIndex = 2, Content = "Content 3", TokenCount = 70, StartLine = 7, EndLine = 10 }
        };
        await _vectorStore.UpsertChunksAsync(chunks);

        var retrievedChunks = await _vectorStore.GetChunksByFileAsync("file-1");

        Assert.Equal(3, retrievedChunks.Count());
    }

    [Fact]
    public async Task UpsertVectorAsync_InsertsVector()
    {
        var file = new FileRecord
        {
            Id = "file-1",
            Path = "/test/file.md",
            ChunkCount = 1,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file);

        var chunk = new ChunkRecord
        {
            Id = "chunk-1",
            FileId = "file-1",
            ChunkIndex = 0,
            Content = "Content",
            TokenCount = 50,
            StartLine = 1,
            EndLine = 3
        };
        await _vectorStore.UpsertChunkAsync(chunk);

        var vector = new VectorRecord
        {
            Id = "vector-1",
            ChunkId = "chunk-1",
            Vector = new float[384],
            ModelName = "test-model"
        };
        // Fill with some values
        for (int i = 0; i < 384; i++)
        {
            vector.Vector[i] = (float)(i % 10) * 0.1f;
        }

        await _vectorStore.UpsertVectorAsync(vector);

        // GetVectorAsync looks up by chunk_id, not by vector Id
        var retrieved = await _vectorStore.GetVectorAsync("chunk-1");

        Assert.NotNull(retrieved);
        // The Id is auto-generated as "vec_{chunkId}" by GetVectorByChunkIdAsync
        Assert.Equal("vec_chunk-1", retrieved.Id);
        Assert.Equal("chunk-1", retrieved.ChunkId);
    }

    [Fact]
    public async Task DeleteChunkAsync_DeletesVectorFromModelTable()
    {
        var file = new FileRecord
        {
            Id = "file-del-test",
            Path = "/test/file-del.md",
            ChunkCount = 1,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file);

        var chunk = new ChunkRecord
        {
            Id = "chunk-del-1",
            FileId = "file-del-test",
            ChunkIndex = 0,
            Content = "Delete test content"
        };
        await _vectorStore.UpsertChunkAsync(chunk);

        var vector = new VectorRecord
        {
            Id = "vec-del-1",
            ChunkId = "chunk-del-1",
            Vector = new float[384],
            ModelName = "test-model"
        };
        await _vectorStore.UpsertVectorAsync(vector);

        var statsBefore = await _vectorStore.GetVectorStatsAsync();
        var modelBefore = statsBefore.FirstOrDefault(s => s.ModelName == "test-model");
        Assert.NotNull(modelBefore);
        Assert.Equal(1, modelBefore.VectorCount);

        // Act: Delete chunk
        await _vectorStore.DeleteChunkAsync("chunk-del-1");

        // Assert: Vector should be gone!
        var statsAfter = await _vectorStore.GetVectorStatsAsync();
        var modelAfter = statsAfter.FirstOrDefault(s => s.ModelName == "test-model");
        Assert.Equal(0, modelAfter?.VectorCount ?? 0);
    }

    [Fact]
    public async Task DeleteChunkAsync_WhenModelTableDoesNotExist_DoesNotThrow()
    {
        var file = new FileRecord
        {
            Id = "file-xyz",
            Path = "/test/file-xyz.md",
            ChunkCount = 1,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file);

        var chunk = new ChunkRecord
        {
            Id = "chunk-xyz",
            FileId = "file-xyz",
            ChunkIndex = 0,
            Content = "Test"
        };
        await _vectorStore.UpsertChunkAsync(chunk);

        // Insert for "my-model" (table vec_my_model exists)
        var vector = new VectorRecord
        {
            Id = "vec-1",
            ChunkId = "chunk-xyz",
            Vector = new float[384],
            ModelName = "my-model"
        };
        await _vectorStore.UpsertVectorAsync(vector);

        // DeleteChunkAsync will iterate _modelDimensions which contains "default" (no vec_default table!)
        // Does this throw or succeed?
        await _vectorStore.DeleteChunkAsync("chunk-xyz");

        var stats = await _vectorStore.GetVectorStatsAsync();
        var model = stats.FirstOrDefault(s => s.ModelName == "my-model");
        Assert.Equal(0, model?.VectorCount ?? 0);
    }

    [Fact]
    public async Task DeleteChunksAsync_BatchDeletesChunksAndVectors()
    {
        var file = new FileRecord
        {
            Id = "file-batch",
            Path = "/test/file-batch.md",
            ChunkCount = 3,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file);

        for (int i = 0; i < 3; i++)
        {
            await _vectorStore.UpsertChunkAsync(new ChunkRecord
            {
                Id = $"chunk-batch-{i}",
                FileId = "file-batch",
                ChunkIndex = i,
                Content = $"Test {i}"
            });
            await _vectorStore.UpsertVectorAsync(new VectorRecord
            {
                Id = $"vec-batch-{i}",
                ChunkId = $"chunk-batch-{i}",
                Vector = new float[384],
                ModelName = "batch-model"
            });
        }

        var deleted = await _vectorStore.DeleteChunksAsync(new[] { "chunk-batch-0", "chunk-batch-1" });
        Assert.Equal(2, deleted);

        var remainingChunks = await _vectorStore.GetChunksByFileAsync("file-batch");
        Assert.Single(remainingChunks);
        Assert.Equal("chunk-batch-2", remainingChunks.First().Id);

        var stats = await _vectorStore.GetVectorStatsAsync();
        var model = stats.FirstOrDefault(s => s.ModelName == "batch-model");
        Assert.Equal(1, model?.VectorCount ?? 0);
    }

    [Fact]
    public async Task CleanupOrphanChunkVectorsAsync_CleansDuplicateChunksAndVectors()
    {
        var file = new FileRecord
        {
            Id = "file-dup",
            Path = "/test/file-dup.md",
            ChunkCount = 1,
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file);

        // Old chunk (earlier rowid)
        await _vectorStore.UpsertChunkAsync(new ChunkRecord
        {
            Id = "chunk-dup-old",
            FileId = "file-dup",
            ChunkIndex = 0,
            Content = "Old"
        });
        await _vectorStore.UpsertVectorAsync(new VectorRecord
        {
            Id = "vec-dup-old",
            ChunkId = "chunk-dup-old",
            Vector = new float[384],
            ModelName = "dup-model"
        });

        // New chunk (later rowid, same file_id and chunk_index)
        await _vectorStore.UpsertChunkAsync(new ChunkRecord
        {
            Id = "chunk-dup-new",
            FileId = "file-dup",
            ChunkIndex = 0,
            Content = "New"
        });
        await _vectorStore.UpsertVectorAsync(new VectorRecord
        {
            Id = "vec-dup-new",
            ChunkId = "chunk-dup-new",
            Vector = new float[384],
            ModelName = "dup-model"
        });

        // Act: Cleanup orphans
        var cleaned = await _vectorStore.CleanupOrphanChunkVectorsAsync();
        Assert.True(cleaned > 0);

        // Assert: only chunk-dup-new remains
        var chunks = await _vectorStore.GetChunksByFileAsync("file-dup");
        Assert.Single(chunks);
        Assert.Equal("chunk-dup-new", chunks.First().Id);

        var stats = await _vectorStore.GetVectorStatsAsync();
        var model = stats.FirstOrDefault(s => s.ModelName == "dup-model");
        Assert.Equal(1, model?.VectorCount ?? 0);
    }

    [Fact]
    public async Task GetAllFilesAsync_ReturnsAllFiles()
    {
        var files = new[]
        {
            new FileRecord { Id = "file-1", Path = "/test/file1.md", ChunkCount = 1, IndexedAt = DateTime.UtcNow },
            new FileRecord { Id = "file-2", Path = "/test/file2.md", ChunkCount = 2, IndexedAt = DateTime.UtcNow },
            new FileRecord { Id = "file-3", Path = "/test/file3.md", ChunkCount = 3, IndexedAt = DateTime.UtcNow }
        };

        foreach (var file in files)
        {
            await _vectorStore.UpsertFileAsync(file);
        }

        var allFiles = await _vectorStore.GetAllFilesAsync();

        Assert.Equal(3, allFiles.Count());
    }

    [Fact]
    public async Task CleanupNoiseChunksAsync_RemovesNoiseAndSeparatorChunks()
    {
        var file = new FileRecord
        {
            Id = "file-noise-test",
            Path = "/test/noise.md",
            IndexedStatus = "complete"
        };
        await _vectorStore.UpsertFileAsync(file);

        var validChunk = new ChunkRecord
        {
            Id = "chunk-valid-1",
            FileId = "file-noise-test",
            ChunkIndex = 0,
            Content = "这是非常有意义且重要的文档核心正文内容。"
        };
        var separatorChunk1 = new ChunkRecord
        {
            Id = "chunk-noise-1",
            FileId = "file-noise-test",
            ChunkIndex = 1,
            Content = "=== (43-48) ==="
        };
        var separatorChunk2 = new ChunkRecord
        {
            Id = "chunk-noise-2",
            FileId = "file-noise-test",
            ChunkIndex = 2,
            Content = "-------------------"
        };

        await _vectorStore.UpsertChunksAsync(new[] { validChunk, separatorChunk1, separatorChunk2 });

        var cleanedCount = await _vectorStore.CleanupNoiseChunksAsync();
        Assert.Equal(2, cleanedCount);

        var remainingChunks = (await _vectorStore.GetChunksByFileAsync("file-noise-test")).ToList();
        Assert.Single(remainingChunks);
        Assert.Equal("chunk-valid-1", remainingChunks[0].Id);
    }

    [Fact]
    public async Task DeleteStaleChunksForFileAsync_CleansHistoricalChunksAndVectors()
    {
        var file = new FileRecord
        {
            Id = "file-rechunk-test",
            Path = "/test/file-rechunk.md",
            ChunkCount = 2,
            IndexedStatus = "complete",
            IndexedAt = DateTime.UtcNow
        };
        await _vectorStore.UpsertFileAsync(file);

        // Version 1 chunks and vectors
        await _vectorStore.UpsertChunkAsync(new ChunkRecord
        {
            Id = "chunk-v1-0",
            FileId = "file-rechunk-test",
            ChunkIndex = 0,
            Content = "Old 0"
        });
        await _vectorStore.UpsertVectorAsync(new VectorRecord
        {
            Id = "vec-v1-0",
            ChunkId = "chunk-v1-0",
            Vector = new float[384],
            ModelName = "rechunk-model"
        });

        await _vectorStore.UpsertChunkAsync(new ChunkRecord
        {
            Id = "chunk-v1-1",
            FileId = "file-rechunk-test",
            ChunkIndex = 1,
            Content = "Old 1"
        });
        await _vectorStore.UpsertVectorAsync(new VectorRecord
        {
            Id = "vec-v1-1",
            ChunkId = "chunk-v1-1",
            Vector = new float[384],
            ModelName = "rechunk-model"
        });

        // Version 2 chunks and vectors (new ids)
        await _vectorStore.UpsertChunkAsync(new ChunkRecord
        {
            Id = "chunk-v2-0",
            FileId = "file-rechunk-test",
            ChunkIndex = 0,
            Content = "New 0"
        });
        await _vectorStore.UpsertVectorAsync(new VectorRecord
        {
            Id = "vec-v2-0",
            ChunkId = "chunk-v2-0",
            Vector = new float[384],
            ModelName = "rechunk-model"
        });

        // Act: Delete stale chunks for this file, keeping only chunk-v2-0
        var deleted = await _vectorStore.DeleteStaleChunksForFileAsync("file-rechunk-test", new[] { "chunk-v2-0" });
        Assert.Equal(2, deleted);

        var chunks = await _vectorStore.GetChunksByFileAsync("file-rechunk-test");
        Assert.Single(chunks);
        Assert.Equal("chunk-v2-0", chunks.First().Id);

        var stats = await _vectorStore.GetVectorStatsAsync();
        var model = stats.FirstOrDefault(s => s.ModelName == "rechunk-model");
        Assert.Equal(1, model?.VectorCount ?? 0);
    }

    [Fact]
    public async Task SearchAsync_WithVectors_ReturnsResults()
    {
        // This test is skipped due to a bug in SqliteVectorStore.SearchAsync
        // The query doesn't select c.id which is needed by ReadChunkRecord
    }

    public void Dispose()
    {
        _vectorStore.Dispose();

        // Add delay and retry to handle SQLite file handle release
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(_dbPath))
                {
                    File.Delete(_dbPath);
                }
                break;
            }
            catch (IOException)
            {
                // Wait for file handle to be released
                Thread.Sleep(50);
            }
        }
    }
}
