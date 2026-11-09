using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Tests;

public class LexicalIndexConsistencyTests
{
    [Fact]
    public async Task VectorRecoveryBlocksQueriesAndLeavesGlobalMarkerAfterInterruptedRebuild()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-vector-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var docs = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");
        try
        {
            await using (var coordinator = new LexicalIndexCoordinator(docs, index))
            {
                Task<IDisposable> pending;
                using (await coordinator.BeginVectorRecoveryAsync(new[] { "a", "b" }))
                {
                    Assert.True(LexicalIndexCoordinator.HasPersistedStaleVectors(docs));
                    pending = coordinator.AcquireVectorQueryLeaseAsync(new[] { "a" }, false);
                    Assert.False(pending.IsCompleted);
                }
                await Assert.ThrowsAsync<SearchCapabilityException>(() => pending);
            }
            await using (var coordinator = new LexicalIndexCoordinator(docs, index))
            {
                Assert.Contains("*", await coordinator.GetStaleVectorSourcesAsync());
                using (var maintenance = await coordinator.BeginVectorRecoveryAsync(new[] { "a" }))
                    await maintenance.CompleteMaintenanceAsync();
                Assert.Contains("*", await coordinator.GetStaleVectorSourcesAsync());
                using (var recovery = await coordinator.BeginVectorRecoveryAsync(new[] { "a", "b" }))
                    await recovery.CompleteVerifiedAsync(new[] { "a" });
                using (await coordinator.AcquireVectorQueryLeaseAsync(new[] { "a" }, false)) { }
                await Assert.ThrowsAsync<SearchCapabilityException>(() => coordinator.AcquireVectorQueryLeaseAsync(new[] { "b" }, false));
                Assert.DoesNotContain("*", await coordinator.GetStaleVectorSourcesAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CorruptStagedIndexCannotReplaceCommittedGenerationAndRecoveryIsIdempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-corrupt-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var documents = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");
        try
        {
            await using (var coordinator = new LexicalIndexCoordinator(documents, index))
            {
                var file = new FileRecord { Id = "f", Path = "note.md", Source = "notes" };
                await coordinator.StageDocumentsAsync(file, "old", new[] { new ChunkRecord { Id = "old", FileId = "f", Content = "oldvisible" } });
                await coordinator.StageIndexAsync("f", "old");
                await coordinator.PublishAsync("f", "old");
                await coordinator.StageDocumentsAsync(file, "new", new[] { new ChunkRecord { Id = "new", FileId = "f", Content = "newhidden" } });
                await coordinator.StageIndexAsync("f", "new");
                using (var damage = new SqliteConnection($"Data Source={index};Pooling=False"))
                {
                    await damage.OpenAsync();
                    using var cmd = damage.CreateCommand();
                    cmd.CommandText = "UPDATE lexical_generation_fts SET chunk_id='unexpected' WHERE file_id='f' AND generation='new'";
                    await cmd.ExecuteNonQueryAsync();
                }
                await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PublishAsync("f", "new"));
                Assert.Equal("old", Assert.Single(await coordinator.SearchAsync("oldvisible", new[] { "f" }, 5)).ChunkId);
                Assert.Empty(await coordinator.SearchAsync("newhidden", new[] { "f" }, 5));
            }
            await using var recovered = new LexicalIndexCoordinator(documents, index);
            await recovered.RecoverAsync();
            await recovered.RecoverAsync();
            Assert.Equal("old", Assert.Single(await recovered.SearchAsync("oldvisible", new[] { "f" }, 5)).ChunkId);
            Assert.False(await recovered.HasIndexedGenerationAsync("f", "new"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task CoverageRecoveryOnlyClearsSourcesWhoseRevisionHasNotChanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-coverage-epoch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            foreach (var source in new[] { "a", "b" })
            {
                await coordinator.StageDocumentsAsync(new FileRecord { Id = source, Path = source + ".md", Source = source }, "v1",
                    new[] { new ChunkRecord { Id = source + "1", FileId = source, Content = "first" } });
                await coordinator.StageIndexAsync(source, "v1");
                await coordinator.PublishAsync(source, "v1");
            }
            var snapshot = await coordinator.CaptureVectorCoverageAsync();
            await coordinator.StageDocumentsAsync(new FileRecord { Id = "a", Path = "a.md", Source = "a" }, "v2",
                new[] { new ChunkRecord { Id = "a2", FileId = "a", Content = "changed" } });
            await coordinator.StageIndexAsync("a", "v2");
            await coordinator.PublishAsync("a", "v2");
            var cleared = await coordinator.ClearVerifiedVectorCoverageAsync(snapshot, new[] { "a", "b" });
            Assert.DoesNotContain("a", cleared);
            Assert.Contains("b", cleared);
            Assert.Contains("a", await coordinator.GetStaleVectorSourcesAsync());
            Assert.DoesNotContain("b", await coordinator.GetStaleVectorSourcesAsync());
            var newer = await coordinator.CaptureVectorCoverageAsync();
            Assert.Contains("a", await coordinator.ClearVerifiedVectorCoverageAsync(newer, new[] { "a" }));
            await coordinator.StageDocumentsAsync(new FileRecord { Id = "a", Path = "a.md", Source = "a" }, "v3",
                new[] { new ChunkRecord { Id = "a3", FileId = "a", Content = "again" } });
            await coordinator.StageIndexAsync("a", "v3");
            await coordinator.PublishAsync("a", "v3");
            Assert.Empty(await coordinator.ClearVerifiedVectorCoverageAsync(newer, new[] { "a" }));
            Assert.Contains("a", await coordinator.GetStaleVectorSourcesAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CoverageProbeDoesNotCreateOrModifyAnExistingLegacyDatabase()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var database = Path.Combine(dir, "vectors.db");
        try
        {
            Assert.False(LexicalIndexCoordinator.HasPersistedStaleVectors(database));
            Assert.False(File.Exists(database));
            using (var db = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TABLE legacy (id TEXT)";
                cmd.ExecuteNonQuery();
            }
            Assert.False(LexicalIndexCoordinator.HasPersistedStaleVectors(database));
            using var read = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
            read.Open();
            using var check = read.CreateCommand();
            check.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='lexical_stale_vector_sources'";
            Assert.Equal(0L, check.ExecuteScalar());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task VectorQueryLeasePreventsConcurrentLexicalPublication()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-lease-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var file = new FileRecord { Id = "f", Path = "a.md", Source = "notes" };
            await coordinator.StageDocumentsAsync(file, "v1", new[] { new ChunkRecord { Id = "c", FileId = "f", Content = "text" } });
            await coordinator.StageIndexAsync("f", "v1");
            Task publishing;
            using (await coordinator.AcquireVectorQueryLeaseAsync(new[] { "notes" }, false))
            {
                publishing = coordinator.PublishAsync("f", "v1");
                Assert.False(publishing.IsCompleted);
            }
            await publishing;
            await Assert.ThrowsAsync<SearchCapabilityException>(() => coordinator.AcquireVectorQueryLeaseAsync(new[] { "notes" }, false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task PublishingAndDeletingPersistVectorCoverageInvalidationForOldAndNewSources()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-coverage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var documents = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");
        try
        {
            await using (var coordinator = new LexicalIndexCoordinator(documents, index))
            {
                var file = new FileRecord { Id = "f", Path = "a.md", Source = "old" };
                await coordinator.StageDocumentsAsync(file, "v1", new[] { new ChunkRecord { Id = "c1", FileId = "f", Content = "text" } });
                await coordinator.StageIndexAsync("f", "v1");
                await coordinator.PublishAsync("f", "v1");
                file.Source = "new";
                await coordinator.StageDocumentsAsync(file, "v2", new[] { new ChunkRecord { Id = "c2", FileId = "f", Content = "updated" } });
                await coordinator.StageIndexAsync("f", "v2");
                await coordinator.PublishAsync("f", "v2");
                await coordinator.DeleteFileAsync("f");
            }
            await using var reopened = new LexicalIndexCoordinator(documents, index);
            var stale = await reopened.GetStaleVectorSourcesAsync();
            Assert.Contains("old", stale);
            Assert.Contains("new", stale);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SearchChecksCurrentSourceAndFolderBeforeLimitingResults()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var safe = new FileRecord { Id = "safe", Path = "/notes/a.md", Source = "public", Tags = new() { "keep" } };
            var hidden = new FileRecord { Id = "secret", Path = "/notes-private/a.md", Source = "public", Tags = new() { "keep" } };
            foreach (var file in new[] { safe, hidden })
            {
                await coordinator.StageDocumentsAsync(file, "v1", new[] { new ChunkRecord { Id = file.Id + "-chunk", FileId = file.Id, Content = "common common" } });
                await coordinator.StageIndexAsync(file.Id, "v1");
                await coordinator.PublishAsync(file.Id, "v1");
            }
            var roots = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["public"] = new[] { "/notes" } };
            var scope = new LexicalSearchScope(new[] { "public" }, new[] { "/notes" }, new[] { "keep" }, sourceRoots: roots);
            Assert.Equal("safe", Assert.Single(await coordinator.SearchAuthorizedAsync("common", scope, 1)).FileId);
            Assert.Empty(await coordinator.SearchAuthorizedAsync("common", new LexicalSearchScope(Array.Empty<string>()), 10));
            Assert.Empty(await coordinator.SearchAuthorizedAsync("common", new LexicalSearchScope(new[] { "public" }, new[] { " " }), 10));
            safe.Source = "private";
            await coordinator.StageDocumentsAsync(safe, "v2", new[] { new ChunkRecord { Id = "changed", FileId = "safe", Content = "common" } });
            await coordinator.StageIndexAsync("safe", "v2");
            await coordinator.PublishAsync("safe", "v2");
            Assert.Empty(await coordinator.SearchAuthorizedAsync("common", scope, 10));
            var movedRoot = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["public"] = new[] { "/other" } };
            Assert.Empty(await coordinator.SearchAuthorizedAsync("common", new LexicalSearchScope(new[] { "public" }, sourceRoots: movedRoot), 10));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ConcurrentSearchesNeverMixPublishedGenerations()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-concurrent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var file = new FileRecord { Id = "f", Path = "note.md" };
            await coordinator.StageDocumentsAsync(file, "v0", new[] { new ChunkRecord { Id = "c0", FileId = "f", Content = "shared versionzero" } });
            await coordinator.StageIndexAsync("f", "v0");
            await coordinator.PublishAsync("f", "v0");
            await coordinator.StageDocumentsAsync(file, "v1", new[] { new ChunkRecord { Id = "c1", FileId = "f", Content = "shared versionone" } });
            await coordinator.StageIndexAsync("f", "v1");
            var searches = Enumerable.Range(0, 20).Select(async _ =>
                await coordinator.SearchAsync("shared", new[] { "f" }, 10)).ToArray();
            await Task.WhenAll(searches.Cast<Task>().Append(coordinator.PublishAsync("f", "v1")));
            foreach (var search in searches)
            {
                var hit = Assert.Single(await search);
                Assert.True((hit.ChunkId == "c0" && hit.Content == "shared versionzero") ||
                            (hit.ChunkId == "c1" && hit.Content == "shared versionone"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DeletingAnActiveFileHidesItAndRecoveryCleansItsIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var file = new FileRecord { Id = "f", Path = "a.md" };
            await coordinator.StageDocumentsAsync(file, "v1", new[] { new ChunkRecord { Id = "c", FileId = "f", Content = "dragon" } });
            await coordinator.StageIndexAsync("f", "v1");
            await coordinator.PublishAsync("f", "v1");
            Assert.NotNull(await coordinator.GetActiveFileByPathAsync("a.md"));
            await coordinator.DeleteFileAsync("f");
            Assert.Null(await coordinator.GetActiveFileByPathAsync("a.md"));
            Assert.Empty(await coordinator.SearchAsync("dragon", new[] { "f" }, 10));
            await coordinator.RecoverAsync();
            Assert.False(await coordinator.HasIndexedGenerationAsync("f", "v1"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ConcurrentCoordinatorForSameDataDirectoryIsRejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-lock-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var documents = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");
        try
        {
            await using (var first = new LexicalIndexCoordinator(documents, index))
                Assert.ThrowsAny<IOException>(() => new LexicalIndexCoordinator(documents, index));
            await using var next = new LexicalIndexCoordinator(documents, index);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SearchUsesActiveGenerationAndAppliesScopeBeforeLimit()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var visible = new FileRecord { Id = "visible", Path = "visible.md", Title = "Original title", Source = "public" };
            await coordinator.StageDocumentsAsync(visible, "v1", new[] { new ChunkRecord { Id = "old", FileId = "visible", Content = "database original", StartLine = 7 } });
            await coordinator.StageIndexAsync("visible", "v1");
            await coordinator.PublishAsync("visible", "v1");
            var hidden = new FileRecord { Id = "hidden", Path = "hidden.md", Source = "private" };
            await coordinator.StageDocumentsAsync(hidden, "v1", Enumerable.Range(0, 20).Select(i =>
                new ChunkRecord { Id = $"hidden-{i}", FileId = "hidden", Content = "database database database" }).ToArray());
            await coordinator.StageIndexAsync("hidden", "v1");
            await coordinator.PublishAsync("hidden", "v1");
            var hit = Assert.Single(await coordinator.SearchAsync("database", new[] { "visible" }, 1));
            Assert.Equal("old", hit.ChunkId);
            Assert.Equal("database original", hit.Content);
            Assert.Equal(7, hit.StartLine);
            Assert.Equal("Original title", hit.Title);
            Assert.Empty(await coordinator.SearchAsync("database", Array.Empty<string>(), 1));
            await coordinator.StageDocumentsAsync(visible, "v2", new[] { new ChunkRecord { Id = "new", FileId = "visible", Content = "replacement" } });
            await coordinator.StageIndexAsync("visible", "v2");
            await coordinator.PublishAsync("visible", "v2");
            Assert.Empty(await coordinator.SearchAsync("database", new[] { "visible" }, 1));
            Assert.Equal("new", Assert.Single(await coordinator.SearchAsync("replacement", new[] { "visible" }, 1)).ChunkId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task EmptyGenerationMustBeIndexedAndPublishedGenerationCannotBeRestaged()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var file = new FileRecord { Id = "empty", Path = "empty.md" };
            await coordinator.StageDocumentsAsync(file, "v1", Array.Empty<ChunkRecord>());
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PublishAsync("empty", "v1"));
            await coordinator.StageIndexAsync("empty", "v1");
            await coordinator.PublishAsync("empty", "v1");
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StageIndexAsync("empty", "v1"));
            Assert.Empty(await coordinator.GetActiveChunksAsync("empty"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task StagedGenerationsStayInvisibleAndRecoveryPreservesPublishedGeneration()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-generation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var documents = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");
        try
        {
            await using (var coordinator = new LexicalIndexCoordinator(documents, index))
            {
                var file = new FileRecord { Id = "file", Path = "note.md", Source = "notes" };
                var oldChunk = new ChunkRecord { Id = "old", FileId = "file", Content = "old text", StartLine = 1 };
                await coordinator.StageDocumentsAsync(file, "v1", new[] { oldChunk });
                await coordinator.StageIndexAsync("file", "v1");
                await coordinator.PublishAsync("file", "v1");

                var newChunk = new ChunkRecord { Id = "new", FileId = "file", Content = "new text", StartLine = 3 };
                await coordinator.StageDocumentsAsync(file, "v2", new[] { newChunk });
                await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.PublishAsync("file", "v2"));
                Assert.Equal("old", Assert.Single(await coordinator.GetActiveChunksAsync("file")).Id);
                await coordinator.StageIndexAsync("file", "v2");
                Assert.Equal("old", Assert.Single(await coordinator.GetActiveChunksAsync("file")).Id);
            }

            await using (var recovered = new LexicalIndexCoordinator(documents, index))
            {
                await recovered.RecoverAsync();
                Assert.Equal("old", Assert.Single(await recovered.GetActiveChunksAsync("file")).Id);
                Assert.False(await recovered.HasIndexedGenerationAsync("file", "v2"));
                var file = new FileRecord { Id = "file", Path = "note.md", Source = "notes" };
                await recovered.StageDocumentsAsync(file, "v3", new[] { new ChunkRecord { Id = "newer", FileId = "file", Content = "fresh" } });
                await recovered.StageIndexAsync("file", "v3");
                await recovered.PublishAsync("file", "v3");
                Assert.Equal("newer", Assert.Single(await recovered.GetActiveChunksAsync("file")).Id);
                await recovered.RecoverAsync();
                Assert.Equal("newer", Assert.Single(await recovered.GetActiveChunksAsync("file")).Id);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    private static async Task<(Process process, int pid)> StartCrashWorkerAsync(string dir, string action, string fileId = "test-file", string generation = "v1", string token = "crash_token")
    {
        var workerDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../LexicalRecoveryCrashWorker/bin/Debug/net10.0/LexicalRecoveryCrashWorker.dll"));
        if (!File.Exists(workerDll))
            throw new FileNotFoundException($"Crash worker dll not found at {workerDll}");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{workerDll}\" --action {action} --dir \"{dir}\" --fileId {fileId} --gen {generation} --token {token}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = new Process { StartInfo = psi };
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null && e.Data.StartsWith("SIGNAL:READY:PID:"))
            {
                var pidStr = e.Data.Substring("SIGNAL:READY:PID:".Length);
                if (int.TryParse(pidStr, out var pid))
                    tcs.TrySetResult(pid);
            }
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        var pid = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        return (proc, pid);
    }

    private static async Task KillWorkerAsync(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync();
            }
        }
        catch { }
        finally { proc.Dispose(); }
    }

    [Fact]
    public async Task P02_LegacyDatabase_SchemaMigration_And_Idempotence()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p02-legacy-db-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var docs = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");

        try
        {
            // 1. Create a pure legacy database (files, chunks, bm25_documents) without any lexical tables
            using (var conn = new SqliteConnection($"Data Source={docs};Pooling=False"))
            {
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE files (
                        id TEXT PRIMARY KEY,
                        path TEXT UNIQUE NOT NULL,
                        file_name TEXT,
                        parent_folder TEXT,
                        source TEXT,
                        title TEXT,
                        content_length INTEGER,
                        content_hash TEXT,
                        chunking_hash TEXT,
                        chunk_count INTEGER,
                        modified_at TEXT,
                        indexed_at TEXT,
                        indexed_status TEXT
                    );
                    CREATE TABLE chunks (
                        id TEXT PRIMARY KEY,
                        file_id TEXT,
                        source TEXT,
                        chunk_index INTEGER,
                        start_line INTEGER,
                        end_line INTEGER,
                        heading_path TEXT,
                        content TEXT,
                        content_hash TEXT,
                        parent_chunk_id TEXT
                    );
                    INSERT INTO files (id, path, file_name, source, title, chunk_count)
                    VALUES ('f-legacy', 'legacy.md', 'legacy.md', 'source-a', 'Legacy File', 1);
                    INSERT INTO chunks (id, file_id, source, chunk_index, content)
                    VALUES ('c-legacy', 'f-legacy', 'source-a', 0, 'Legacy chunk content');
                ";
                await cmd.ExecuteNonQueryAsync();
            }

            using (var conn = new SqliteConnection($"Data Source={index};Pooling=False"))
            {
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE bm25_documents (
                        chunk_id TEXT PRIMARY KEY,
                        file_id TEXT,
                        source TEXT,
                        content TEXT
                    );
                    INSERT INTO bm25_documents (chunk_id, file_id, source, content)
                    VALUES ('c-legacy', 'f-legacy', 'source-a', 'Legacy chunk content');
                ";
                await cmd.ExecuteNonQueryAsync();
            }

            // 2. Open LexicalIndexCoordinator on the legacy DB
            await using (var coordinator = new LexicalIndexCoordinator(docs, index))
            {
                // Verify schema tables were created without corrupting legacy data
                using (var conn = new SqliteConnection($"Data Source={docs};Pooling=False"))
                {
                    await conn.OpenAsync();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM files WHERE id='f-legacy'";
                    Assert.Equal(1L, await cmd.ExecuteScalarAsync());
                    cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE id='c-legacy'";
                    Assert.Equal(1L, await cmd.ExecuteScalarAsync());
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='lexical_active_generations'";
                    Assert.Equal(1L, await cmd.ExecuteScalarAsync());
                }

                // 3. Repeated RecoverAsync calls are idempotent
                await coordinator.RecoverAsync();
                await coordinator.RecoverAsync();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task P02_D1_DocumentStaged_WorkerCrash_RecoversCleanly()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p02-d1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var docs = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");

        try
        {
            var (proc, pid) = await StartCrashWorkerAsync(dir, "stage-docs", "d1-file", "v-crash", "d1_crash_token");
            Assert.True(pid > 0);
            await KillWorkerAsync(proc);

            // Reopen coordinator and recover
            await using var coordinator = new LexicalIndexCoordinator(docs, index);
            await coordinator.RecoverAsync();

            // Uncommitted generation must be cleaned up and invisible
            Assert.False(await coordinator.HasIndexedGenerationAsync("d1-file", "v-crash"));
            var results = await coordinator.SearchAsync("d1_crash_token", new[] { "d1-file" }, 10);
            Assert.Empty(results);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task P02_D2_FtsStaged_WorkerCrash_RecoversCleanly()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p02-d2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var docs = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");

        try
        {
            var (proc, pid) = await StartCrashWorkerAsync(dir, "stage-fts", "d2-file", "v-crash", "d2_crash_token");
            Assert.True(pid > 0);
            await KillWorkerAsync(proc);

            await using var coordinator = new LexicalIndexCoordinator(docs, index);
            await coordinator.RecoverAsync();

            Assert.False(await coordinator.HasIndexedGenerationAsync("d2-file", "v-crash"));
            var results = await coordinator.SearchAsync("d2_crash_token", new[] { "d2-file" }, 10);
            Assert.Empty(results);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task P02_D3_ActiveGenerationCommitted_WorkerCrash_PreservesPublished()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p02-d3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var docs = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");

        try
        {
            var (proc, pid) = await StartCrashWorkerAsync(dir, "publish", "d3-file", "v-pub", "d3_published_token");
            Assert.True(pid > 0);
            await KillWorkerAsync(proc);

            await using var coordinator = new LexicalIndexCoordinator(docs, index);
            await coordinator.RecoverAsync();

            // Published generation must be preserved and searchable
            var chunks = await coordinator.GetActiveChunksAsync("d3-file");
            Assert.NotEmpty(chunks);
            var results = await coordinator.SearchAsync("d3_published_token", new[] { "d3-file" }, 10);
            Assert.NotEmpty(results);
            Assert.Contains("d3_published_token", results[0].Content);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task P02_D4_FileDeleted_WorkerCrash_PreservesDeletedState()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p02-d4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var docs = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");

        try
        {
            // 1. First publish file
            await using (var initial = new LexicalIndexCoordinator(docs, index))
            {
                var file = new FileRecord { Id = "d4-file", Path = Path.Combine(dir, "d4-file.md"), Source = "source-a" };
                await initial.StageDocumentsAsync(file, "v1", new[] { new ChunkRecord { Id = "c-d4", FileId = "d4-file", Content = "d4_content" } });
                await initial.StageIndexAsync("d4-file", "v1");
                await initial.PublishAsync("d4-file", "v1");
            }

            // 2. Run delete-file in worker and kill it
            var (proc, pid) = await StartCrashWorkerAsync(dir, "delete-file", "d4-file");
            Assert.True(pid > 0);
            await KillWorkerAsync(proc);

            // 3. Open coordinator and recover
            await using var coordinator = new LexicalIndexCoordinator(docs, index);
            await coordinator.RecoverAsync();

            // Deleted file does not resurrect
            var active = await coordinator.GetActiveFileByPathAsync(Path.Combine(dir, "d4-file.md"));
            Assert.Null(active);
            Assert.Empty(await coordinator.SearchAsync("d4_content", new[] { "d4-file" }, 10));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task P02_D5_InterruptedRecovery_RepeatedRecoverIdempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p02-d5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var docs = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");

        try
        {
            await using (var coordinator = new LexicalIndexCoordinator(docs, index))
            {
                var file = new FileRecord { Id = "d5-file", Path = Path.Combine(dir, "d5-file.md"), Source = "source-a" };
                await coordinator.StageDocumentsAsync(file, "v-stale", new[] { new ChunkRecord { Id = "c-d5", FileId = "d5-file", Content = "d5_stale" } });
                await coordinator.StageIndexAsync("d5-file", "v-stale");
                // Do not publish: left as staging

                // Recover twice
                await coordinator.RecoverAsync();
                await coordinator.RecoverAsync();

                Assert.False(await coordinator.HasIndexedGenerationAsync("d5-file", "v-stale"));

                // Can stage and publish normally after recovery
                await coordinator.StageDocumentsAsync(file, "v-clean", new[] { new ChunkRecord { Id = "c-d5-clean", FileId = "d5-file", Content = "d5_clean" } });
                await coordinator.StageIndexAsync("d5-file", "v-clean");
                await coordinator.PublishAsync("d5-file", "v-clean");

                Assert.NotEmpty(await coordinator.SearchAsync("d5_clean", new[] { "d5-file" }, 10));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task P02_D6_VectorRecovery_WorkerCrash_LeavesStaleMarker()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p02-d6-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var docs = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");

        try
        {
            var (proc, pid) = await StartCrashWorkerAsync(dir, "begin-recovery");
            Assert.True(pid > 0);
            await KillWorkerAsync(proc);

            await using var coordinator = new LexicalIndexCoordinator(docs, index);
            var stale = await coordinator.GetStaleVectorSourcesAsync();
            Assert.Contains("*", stale);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task P02_D7_DamagedFtsOrMissingBm25Db_SafeHandling()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p02-d7-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var docs = Path.Combine(dir, "vectors.db");
        var index = Path.Combine(dir, "bm25.db");

        try
        {
            // 1. Initial publish
            await using (var initial = new LexicalIndexCoordinator(docs, index))
            {
                var file = new FileRecord { Id = "d7-file", Path = Path.Combine(dir, "d7-file.md"), Source = "source-a" };
                await initial.StageDocumentsAsync(file, "v1", new[] { new ChunkRecord { Id = "c-d7", FileId = "d7-file", Content = "d7_token" } });
                await initial.StageIndexAsync("d7-file", "v1");
                await initial.PublishAsync("d7-file", "v1");
            }

            // 2. Simulate complete bm25.db missing or corrupted
            SqliteConnection.ClearAllPools();
            if (File.Exists(index)) File.Delete(index);

            // 3. Coordinator handles missing FTS file gracefully
            await using (var recovered = new LexicalIndexCoordinator(docs, index))
            {
                await recovered.RecoverAsync();
                // Does not throw unhandled exception; search gracefully returns empty or incomplete
                var results = await recovered.SearchAsync("d7_token", new[] { "d7-file" }, 10);
                Assert.Empty(results);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }
}
