using System.Text.Json;
using JiebaNet.Segmenter;
using Microsoft.Data.Sqlite;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Storage;

/// <summary>
/// Coordinates immutable document generations across the document and FTS databases.
/// Only the document database's active pointer makes a generation visible.
/// The caller must use a single coordinator per data directory.
/// </summary>
public sealed class LexicalIndexCoordinator : IAsyncDisposable
{
    private readonly SqliteConnection _documents;
    private readonly SqliteConnection _index;
    private readonly FileStream _exclusiveLease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JiebaSegmenter Segmenter = new();

    public LexicalIndexCoordinator(string documentDatabasePath, string indexDatabasePath)
    {
        _exclusiveLease = new FileStream(Path.GetFullPath(documentDatabasePath) + ".lexical.lock",
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        SqliteConnection? documents = null;
        SqliteConnection? index = null;
        try
        {
            documents = Open(documentDatabasePath);
            index = Open(indexDatabasePath);
            Execute(documents, @"
            CREATE TABLE IF NOT EXISTS lexical_generations (
                file_id TEXT NOT NULL, generation TEXT NOT NULL, file_json TEXT NOT NULL,
                PRIMARY KEY(file_id, generation));
            CREATE TABLE IF NOT EXISTS lexical_generation_chunks (
                chunk_id TEXT PRIMARY KEY, file_id TEXT NOT NULL, generation TEXT NOT NULL,
                chunk_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_lexical_generation_chunks
                ON lexical_generation_chunks(file_id, generation);
            CREATE TABLE IF NOT EXISTS lexical_active_generations (
                file_id TEXT PRIMARY KEY, generation TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS lexical_published_generations (
                file_id TEXT NOT NULL, generation TEXT NOT NULL, PRIMARY KEY(file_id, generation));
            CREATE TABLE IF NOT EXISTS lexical_stale_vector_sources (
                source TEXT PRIMARY KEY COLLATE NOCASE);
            CREATE TABLE IF NOT EXISTS lexical_vector_source_revisions (
                source TEXT PRIMARY KEY COLLATE NOCASE, revision INTEGER NOT NULL);
            INSERT OR IGNORE INTO lexical_vector_source_revisions(source,revision)
                SELECT source,1 FROM lexical_stale_vector_sources;");
            Execute(index, @"
            CREATE VIRTUAL TABLE IF NOT EXISTS lexical_generation_fts USING fts5(
                file_id UNINDEXED, generation UNINDEXED, chunk_id UNINDEXED, content,
                tokenize='unicode61 remove_diacritics 1');
            CREATE TABLE IF NOT EXISTS lexical_index_manifests (
                file_id TEXT NOT NULL, generation TEXT NOT NULL, chunk_count INTEGER NOT NULL,
                PRIMARY KEY(file_id, generation));");
            _documents = documents;
            _index = index;
        }
        catch
        {
            index?.Dispose();
            documents?.Dispose();
            _exclusiveLease.Dispose();
            throw;
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>Read-only compatibility probe; does not create lexical tables on legacy installations.</summary>
    public static bool HasPersistedStaleVectors(string documentDatabasePath)
    {
        if (!File.Exists(documentDatabasePath)) return false;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = documentDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='lexical_stale_vector_sources'";
        if (cmd.ExecuteScalar() == null) return false;
        cmd.CommandText = "SELECT 1 FROM lexical_stale_vector_sources LIMIT 1";
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>Determine whether lexical data exists without modifying a legacy database.</summary>
    public static bool HasPersistedLexicalIndex(string documentDatabasePath)
    {
        if (!File.Exists(documentDatabasePath)) return false;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = documentDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ConnectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='lexical_generations'";
        return cmd.ExecuteScalar() != null;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public async Task StageDocumentsAsync(FileRecord file, string generation,
        IReadOnlyCollection<ChunkRecord> chunks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(chunks);
        if (string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(generation))
            throw new ArgumentException("File and generation identifiers are required.");
        if (chunks.Any(c => string.IsNullOrWhiteSpace(c.Id) || c.FileId != file.Id) ||
            chunks.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != chunks.Count)
            throw new ArgumentException("Chunks must have unique identifiers and belong to the file.", nameof(chunks));

        await _gate.WaitAsync(ct);
        try
        {
            if (await WasPublishedAsync(file.Id, generation, ct))
                throw new InvalidOperationException("A published generation cannot be reused.");
            using var tx = _documents.BeginTransaction();
            using (var cmd = _documents.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO lexical_generations(file_id,generation,file_json) VALUES($file,$generation,$json)";
                cmd.Parameters.AddWithValue("$file", file.Id);
                cmd.Parameters.AddWithValue("$generation", generation);
                cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(file));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            using (var cmd = _documents.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO lexical_generation_chunks(chunk_id,file_id,generation,chunk_json) VALUES($id,$file,$generation,$json)";
                var id = cmd.Parameters.Add("$id", SqliteType.Text);
                cmd.Parameters.AddWithValue("$file", file.Id);
                cmd.Parameters.AddWithValue("$generation", generation);
                var json = cmd.Parameters.Add("$json", SqliteType.Text);
                foreach (var chunk in chunks)
                {
                    id.Value = chunk.Id;
                    json.Value = JsonSerializer.Serialize(chunk);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }
            tx.Commit();
        }
        finally { _gate.Release(); }
    }

    public async Task StageIndexAsync(string fileId, string generation, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var chunks = await ReadGenerationAsync(fileId, generation, ct);
            if (await WasPublishedAsync(fileId, generation, ct))
                throw new InvalidOperationException("A published generation is immutable.");
            using var tx = _index.BeginTransaction();
            using (var delete = _index.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM lexical_generation_fts WHERE file_id=$file AND generation=$generation";
                delete.Parameters.AddWithValue("$file", fileId);
                delete.Parameters.AddWithValue("$generation", generation);
                await delete.ExecuteNonQueryAsync(ct);
            }
            using (var insert = _index.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO lexical_generation_fts(file_id,generation,chunk_id,content) VALUES($file,$generation,$id,$content)";
                insert.Parameters.AddWithValue("$file", fileId);
                insert.Parameters.AddWithValue("$generation", generation);
                var id = insert.Parameters.Add("$id", SqliteType.Text);
                var content = insert.Parameters.Add("$content", SqliteType.Text);
                foreach (var chunk in chunks)
                {
                    id.Value = chunk.Id;
                    content.Value = string.Join(' ', Segmenter.CutForSearch(chunk.Content));
                    await insert.ExecuteNonQueryAsync(ct);
                }
            }
            using (var manifest = _index.CreateCommand())
            {
                manifest.Transaction = tx;
                manifest.CommandText = @"INSERT INTO lexical_index_manifests(file_id,generation,chunk_count)
                    VALUES($file,$generation,$count) ON CONFLICT(file_id,generation)
                    DO UPDATE SET chunk_count=excluded.chunk_count";
                manifest.Parameters.AddWithValue("$file", fileId);
                manifest.Parameters.AddWithValue("$generation", generation);
                manifest.Parameters.AddWithValue("$count", chunks.Count);
                await manifest.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
        }
        finally { _gate.Release(); }
    }

    public async Task PublishAsync(string fileId, string generation, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var chunks = await ReadGenerationAsync(fileId, generation, ct);
            if (await WasPublishedAsync(fileId, generation, ct))
                throw new InvalidOperationException("A published generation cannot be published again.");
            using (var manifest = _index.CreateCommand())
            {
                manifest.CommandText = "SELECT chunk_count FROM lexical_index_manifests WHERE file_id=$file AND generation=$generation";
                manifest.Parameters.AddWithValue("$file", fileId);
                manifest.Parameters.AddWithValue("$generation", generation);
                var count = await manifest.ExecuteScalarAsync(ct);
                if (count == null || Convert.ToInt64(count) != chunks.Count)
                    throw new InvalidOperationException("The staged lexical index is incomplete.");
            }
            using var verify = _index.CreateCommand();
            verify.CommandText = "SELECT chunk_id FROM lexical_generation_fts WHERE file_id=$file AND generation=$generation";
            verify.Parameters.AddWithValue("$file", fileId);
            verify.Parameters.AddWithValue("$generation", generation);
            var indexed = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = await verify.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct))
                    if (!indexed.Add(reader.GetString(0))) throw new InvalidOperationException("Duplicate indexed chunk.");
            if (!indexed.SetEquals(chunks.Select(c => c.Id)))
                throw new InvalidOperationException("The staged lexical index is incomplete.");
            var oldSource = await ReadActiveSourceAsync(fileId, ct);
            var newSource = await ReadSourceAsync(fileId, generation, ct);
            using var tx = _documents.BeginTransaction();
            using var cmd = _documents.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO lexical_published_generations(file_id,generation) VALUES($file,$generation);
                INSERT INTO lexical_active_generations(file_id,generation) VALUES($file,$generation)
                ON CONFLICT(file_id) DO UPDATE SET generation=excluded.generation;
                INSERT OR IGNORE INTO lexical_stale_vector_sources(source) VALUES($newSource);
                INSERT OR IGNORE INTO lexical_stale_vector_sources(source) SELECT $oldSource WHERE $oldSource IS NOT NULL;
                INSERT INTO lexical_vector_source_revisions(source,revision) VALUES($newSource,1)
                    ON CONFLICT(source) DO UPDATE SET revision=revision+1;
                INSERT INTO lexical_vector_source_revisions(source,revision)
                    SELECT $oldSource,1 WHERE $oldSource IS NOT NULL AND $oldSource <> $newSource COLLATE NOCASE
                    ON CONFLICT(source) DO UPDATE SET revision=revision+1";
            cmd.Parameters.AddWithValue("$file", fileId);
            cmd.Parameters.AddWithValue("$generation", generation);
            cmd.Parameters.AddWithValue("$newSource", string.IsNullOrWhiteSpace(newSource) ? "*" : newSource);
            cmd.Parameters.AddWithValue("$oldSource", oldSource == null ? DBNull.Value : string.IsNullOrWhiteSpace(oldSource) ? "*" : oldSource);
            await cmd.ExecuteNonQueryAsync(ct);
            tx.Commit();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ChunkRecord>> GetActiveChunksAsync(string fileId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _documents.CreateCommand();
            cmd.CommandText = "SELECT generation FROM lexical_active_generations WHERE file_id=$file";
            cmd.Parameters.AddWithValue("$file", fileId);
            var generation = (string?)await cmd.ExecuteScalarAsync(ct);
            return generation == null ? Array.Empty<ChunkRecord>() : await ReadGenerationAsync(fileId, generation, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SearchResult>> ExpandAuthorizedAsync(IReadOnlyCollection<string> chunkIds,
        LexicalSearchScope scope, int window, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        ArgumentNullException.ThrowIfNull(scope);
        if (chunkIds.Count > 100) throw new ArgumentOutOfRangeException(nameof(chunkIds));
        window = Math.Clamp(window, 0, 10);
        await _gate.WaitAsync(ct);
        try
        {
            var results = new List<SearchResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in chunkIds.Distinct(StringComparer.Ordinal))
            {
                using var cmd = _documents.CreateCommand();
                cmd.CommandText = @"SELECT g.file_id,g.generation,g.file_json FROM lexical_generation_chunks c
                    JOIN lexical_active_generations a ON a.file_id=c.file_id AND a.generation=c.generation
                    JOIN lexical_generations g ON g.file_id=a.file_id AND g.generation=a.generation
                    WHERE c.chunk_id=$id";
                cmd.Parameters.AddWithValue("$id", id);
                string? fileId = null, generation = null;
                FileRecord? file = null;
                using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await reader.ReadAsync(ct))
                    {
                        fileId = reader.GetString(0);
                        generation = reader.GetString(1);
                        file = JsonSerializer.Deserialize<FileRecord>(reader.GetString(2));
                    }
                }
                if (fileId == null || generation == null || file == null || !scope.Allows(file)) continue;
                var chunks = (await ReadGenerationAsync(fileId, generation, ct))
                    .OrderBy(c => c.ChunkIndex).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
                var center = chunks.FindIndex(c => c.Id == id);
                if (center < 0) continue;
                foreach (var chunk in chunks.Skip(Math.Max(0, center - window)).Take(Math.Min(chunks.Count, center + window + 1) - Math.Max(0, center - window)))
                {
                    if (!seen.Add(chunk.Id)) continue;
                    results.Add(new SearchResult { ChunkId = chunk.Id, FileId = fileId, FilePath = file.Path,
                        Source = file.Source, Title = file.Title, Content = chunk.Content, StartLine = chunk.StartLine,
                        EndLine = chunk.EndLine, HeadingPath = chunk.HeadingPath, Score = chunk.Id == id ? 1 : 0 });
                }
            }
            return results;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> IsActiveIndexCompleteAsync(string fileId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var active = _documents.CreateCommand();
            active.CommandText = "SELECT generation FROM lexical_active_generations WHERE file_id=$file";
            active.Parameters.AddWithValue("$file", fileId);
            var generation = (string?)await active.ExecuteScalarAsync(ct);
            if (generation == null) return false;
            var chunks = await ReadGenerationAsync(fileId, generation, ct);
            using var manifest = _index.CreateCommand();
            manifest.CommandText = "SELECT chunk_count FROM lexical_index_manifests WHERE file_id=$file AND generation=$generation";
            manifest.Parameters.AddWithValue("$file", fileId);
            manifest.Parameters.AddWithValue("$generation", generation);
            var count = await manifest.ExecuteScalarAsync(ct);
            if (count == null || Convert.ToInt64(count) != chunks.Count) return false;
            var expected = chunks.ToDictionary(c => c.Id,
                c => string.Join(' ', Segmenter.CutForSearch(c.Content)), StringComparer.Ordinal);
            using var cmd = _index.CreateCommand();
            cmd.CommandText = "SELECT chunk_id,content FROM lexical_generation_fts WHERE file_id=$file AND generation=$generation";
            cmd.Parameters.AddWithValue("$file", fileId);
            cmd.Parameters.AddWithValue("$generation", generation);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                if (!expected.Remove(id, out var content) || reader.GetString(1) != content) return false;
            }
            return expected.Count == 0;
        }
        finally { _gate.Release(); }
    }

    public async Task<FileRecord?> GetActiveFileByPathAsync(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _documents.CreateCommand();
            cmd.CommandText = @"SELECT g.file_json FROM lexical_generations g
                JOIN lexical_active_generations a ON a.file_id=g.file_id AND a.generation=g.generation";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            while (await reader.ReadAsync(ct))
            {
                var file = JsonSerializer.Deserialize<FileRecord>(reader.GetString(0)) ?? throw new InvalidOperationException("Invalid file.");
                if (string.Equals(file.Path, path, comparison)) return file;
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<FileRecord>> GetActiveFilesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await GetActiveFilesCoreAsync(ct);
        }
        finally { _gate.Release(); }
    }

    internal async Task<List<FileRecord>> GetActiveFilesCoreAsync(CancellationToken ct = default)
    {
        using var cmd = _documents.CreateCommand();
        cmd.CommandText = @"SELECT g.file_json FROM lexical_generations g
            JOIN lexical_active_generations a ON a.file_id=g.file_id AND a.generation=g.generation";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var files = new List<FileRecord>();
        while (await reader.ReadAsync(ct))
            files.Add(JsonSerializer.Deserialize<FileRecord>(reader.GetString(0)) ?? throw new InvalidOperationException("Invalid file."));
        return files;
    }

    /// <summary>Reads the authorized file and its chunks under one publication barrier.</summary>
    public async Task<(FileRecord? File, IReadOnlyList<ChunkRecord> Chunks)> ReadAuthorizedFileAsync(
        string path, LexicalSearchScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(scope);
        await _gate.WaitAsync(ct);
        try
        {
            string? fileId = null, generation = null;
            FileRecord? file = null;
            using (var cmd = _documents.CreateCommand())
            {
                cmd.CommandText = @"SELECT g.file_id,g.generation,g.file_json FROM lexical_generations g
                    JOIN lexical_active_generations a ON a.file_id=g.file_id AND a.generation=g.generation";
                using var reader = await cmd.ExecuteReaderAsync(ct);
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                while (await reader.ReadAsync(ct))
                {
                    var candidate = JsonSerializer.Deserialize<FileRecord>(reader.GetString(2));
                    if (candidate == null || !string.Equals(candidate.Path, path, comparison) || !scope.Allows(candidate)) continue;
                    fileId = reader.GetString(0);
                    generation = reader.GetString(1);
                    file = candidate;
                    break;
                }
            }
            if (fileId == null || generation == null || file == null) return (null, Array.Empty<ChunkRecord>());
            return (file, await ReadGenerationAsync(fileId, generation, ct));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Atomically hides the active generation; recovery removes its derived rows later.</summary>
    public async Task DeleteFileAsync(string fileId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        await _gate.WaitAsync(ct);
        try
        {
            await DeleteFileCoreAsync(fileId, ct);
        }
        finally { _gate.Release(); }
    }

    internal async Task DeleteFileCoreAsync(string fileId, CancellationToken ct = default)
    {
        var source = await ReadActiveSourceAsync(fileId, ct);
        if (source == null) return;
        using var tx = _documents.BeginTransaction();
        using var cmd = _documents.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT OR IGNORE INTO lexical_stale_vector_sources(source) VALUES($source);
            INSERT INTO lexical_vector_source_revisions(source,revision) VALUES($source,1)
                ON CONFLICT(source) DO UPDATE SET revision=revision+1;
            DELETE FROM lexical_active_generations WHERE file_id=$file";
        cmd.Parameters.AddWithValue("$file", fileId);
        cmd.Parameters.AddWithValue("$source", string.IsNullOrWhiteSpace(source) ? "*" : source);
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }

    /// <summary>Searches all active generations without file restrictions.</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int topK, CancellationToken ct = default)
    {
        return await SearchCoreAsync(query, null, null, topK, ct);
    }

    /// <summary>Searches only active generations of the supplied already-authorized files.</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query,
        IReadOnlyCollection<string> allowedFileIds, int topK, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allowedFileIds);
        return await SearchCoreAsync(query, allowedFileIds, null, topK, ct);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAuthorizedAsync(string query,
        LexicalSearchScope scope, int topK, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return await SearchCoreAsync(query, null, scope, topK, ct);
    }

    private async Task<IReadOnlyList<SearchResult>> SearchCoreAsync(string query,
        IReadOnlyCollection<string>? allowedFileIds, LexicalSearchScope? scope, int topK, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length > 4096) throw new ArgumentOutOfRangeException(nameof(query));
        if (topK is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(topK));
        ct.ThrowIfCancellationRequested();
        if (allowedFileIds != null && allowedFileIds.Count == 0) return Array.Empty<SearchResult>();

        var isLongQuery = query.Length > 15;
        var plan = isLongQuery ? ReferenceRAG.Core.Features.Search.Lexical.LongQueryRefiner.Default.PlanQuery(query) : null;

        var queriesToTry = new List<string>();
        if (plan != null)
        {
            if (!string.IsNullOrWhiteSpace(plan.Tier1StrictNearQuery)) queriesToTry.Add(plan.Tier1StrictNearQuery);
            if (!string.IsNullOrWhiteSpace(plan.Tier2WeightedQuery)) queriesToTry.Add(plan.Tier2WeightedQuery);
            if (!string.IsNullOrWhiteSpace(plan.Tier3FallbackQuery)) queriesToTry.Add(plan.Tier3FallbackQuery);
        }
        else
        {
            var expansionTerms = ReferenceRAG.Core.Features.Search.PhoneticQueryExpander.GetExpansionTerms(query);
            var terms = Segmenter.CutForSearch(query)
                .Concat(expansionTerms)
                .Select(t => t.Trim())
                .Where(t => t.Any(char.IsLetterOrDigit)).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToArray();
            if (terms.Length == 0) return Array.Empty<SearchResult>();
            queriesToTry.Add(string.Join(" OR ", terms.Select(t => $"\"{t.Replace("\"", "\"\"")}\"")));
        }
        if (queriesToTry.Count == 0) return Array.Empty<SearchResult>();

        await _gate.WaitAsync(ct);
        try
        {
            Execute(_documents, "CREATE TEMP TABLE IF NOT EXISTS lexical_allowed_files (id TEXT PRIMARY KEY) WITHOUT ROWID; DELETE FROM lexical_allowed_files;");
            using (var insert = _documents.CreateCommand())
            {
                insert.CommandText = "INSERT OR IGNORE INTO lexical_allowed_files(id) VALUES($id)";
                var id = insert.Parameters.Add("$id", SqliteType.Text);
                foreach (var fileId in allowedFileIds ?? Array.Empty<string>())
                {
                    ct.ThrowIfCancellationRequested();
                    id.Value = fileId;
                    await insert.ExecuteNonQueryAsync(ct);
                }
            }
            var files = new Dictionary<string, (string Generation, FileRecord File)>(StringComparer.Ordinal);
            using (var cmd = _documents.CreateCommand())
            {
                cmd.CommandText = @"SELECT g.file_id,g.generation,g.file_json FROM lexical_generations g
                    JOIN lexical_active_generations a ON a.file_id=g.file_id AND a.generation=g.generation" +
                    (allowedFileIds == null ? "" : " JOIN lexical_allowed_files allowed ON allowed.id=g.file_id");
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var file = JsonSerializer.Deserialize<FileRecord>(reader.GetString(2)) ?? throw new InvalidOperationException("Invalid file.");
                    if (scope == null || scope.Allows(file)) files[reader.GetString(0)] = (reader.GetString(1), file);
                }
            }
            if (files.Count == 0) return Array.Empty<SearchResult>();
            Execute(_index, "CREATE TEMP TABLE IF NOT EXISTS lexical_allowed_generations (file_id TEXT PRIMARY KEY, generation TEXT NOT NULL) WITHOUT ROWID; DELETE FROM lexical_allowed_generations;");
            using (var insert = _index.CreateCommand())
            {
                insert.CommandText = "INSERT INTO lexical_allowed_generations(file_id,generation) VALUES($file,$generation)";
                var file = insert.Parameters.Add("$file", SqliteType.Text);
                var generation = insert.Parameters.Add("$generation", SqliteType.Text);
                foreach (var (fileId, value) in files)
                {
                    file.Value = fileId;
                    generation.Value = value.Generation;
                    await insert.ExecuteNonQueryAsync(ct);
                }
            }
            var matches = new List<(string Id, string FileId, double Score)>();
            var seenChunkIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var match in queriesToTry)
            {
                if (matches.Count >= topK) break;
                try
                {
                    using var cmd = _index.CreateCommand();
                    cmd.CommandText = @"SELECT f.chunk_id,f.file_id,bm25(lexical_generation_fts) AS score
                        FROM lexical_generation_fts f JOIN lexical_allowed_generations a
                        ON a.file_id=f.file_id AND a.generation=f.generation
                        WHERE lexical_generation_fts MATCH $query ORDER BY score,f.chunk_id LIMIT $limit";
                    cmd.Parameters.AddWithValue("$query", match);
                    cmd.Parameters.AddWithValue("$limit", topK);
                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        var chunkId = reader.GetString(0);
                        if (seenChunkIds.Add(chunkId))
                        {
                            matches.Add((chunkId, reader.GetString(1), reader.GetDouble(2)));
                        }
                    }
                }
                catch (SqliteException)
                {
                    // Fallback to next tier
                }
            }
            var chunkById = new Dictionary<string, ChunkRecord>(StringComparer.Ordinal);
            foreach (var batch in matches.Select(m => m.Id).Distinct(StringComparer.Ordinal).Chunk(500))
            {
                using var cmd = _documents.CreateCommand();
                var names = new string[batch.Length];
                for (var i = 0; i < batch.Length; i++)
                {
                    names[i] = $"$id{i}";
                    cmd.Parameters.AddWithValue(names[i], batch[i]);
                }
                cmd.CommandText = $"SELECT chunk_json FROM lexical_generation_chunks WHERE chunk_id IN ({string.Join(',', names)})";
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var chunk = JsonSerializer.Deserialize<ChunkRecord>(reader.GetString(0)) ?? throw new InvalidOperationException("Invalid chunk.");
                    chunkById.Add(chunk.Id, chunk);
                }
            }
            var result = new List<SearchResult>(matches.Count);
            foreach (var (id, fileId, score) in matches)
            {
                if (!chunkById.TryGetValue(id, out var chunk) || chunk.FileId != fileId)
                    throw new InvalidOperationException("Lexical index does not match its active document generation.");
                var file = files[fileId].File;
                result.Add(new SearchResult { ChunkId = id, FileId = fileId, FilePath = file.Path,
                    Title = file.Title, Source = file.Source, Content = chunk.Content, StartLine = chunk.StartLine,
                    EndLine = chunk.EndLine, HeadingPath = chunk.HeadingPath, BM25Score = (float)Math.Abs(score), Score = (float)Math.Abs(score) });
            }

            // PRF (Pseudo-Relevance Feedback) 统计伪相关反馈重排
            if (plan != null && result.Count > 0 && query.Length > 20)
            {
                var prfTerms = ReferenceRAG.Core.Features.Search.Lexical.StatisticalPrfOperator.MineExpansionTerms(
                    result.Select(r => r.Content), plan.DistilledTerms, 3);
                if (prfTerms.Count > 0)
                {
                    for (int i = 0; i < result.Count; i++)
                    {
                        var item = result[i];
                        int hits = prfTerms.Count(t => item.Content.Contains(t, StringComparison.OrdinalIgnoreCase));
                        if (hits > 0)
                        {
                            item.Score += (float)(hits * 0.5);
                        }
                    }
                    result.Sort((a, b) => b.Score.CompareTo(a.Score));
                }
            }

            return result;
        }
        finally
        {
            try
            {
                Execute(_documents, "DROP TABLE IF EXISTS temp.lexical_allowed_files");
                Execute(_index, "DROP TABLE IF EXISTS temp.lexical_allowed_generations");
            }
            finally { _gate.Release(); }
        }
    }

    private async Task<List<ChunkRecord>> ReadGenerationAsync(string fileId, string generation, CancellationToken ct)
    {
        using var exists = _documents.CreateCommand();
        exists.CommandText = "SELECT 1 FROM lexical_generations WHERE file_id=$file AND generation=$generation";
        exists.Parameters.AddWithValue("$file", fileId);
        exists.Parameters.AddWithValue("$generation", generation);
        if (await exists.ExecuteScalarAsync(ct) == null) throw new InvalidOperationException("Document generation does not exist.");
        using var cmd = _documents.CreateCommand();
        cmd.CommandText = "SELECT chunk_json FROM lexical_generation_chunks WHERE file_id=$file AND generation=$generation ORDER BY chunk_id";
        cmd.Parameters.AddWithValue("$file", fileId);
        cmd.Parameters.AddWithValue("$generation", generation);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var chunks = new List<ChunkRecord>();
        while (await reader.ReadAsync(ct))
            chunks.Add(JsonSerializer.Deserialize<ChunkRecord>(reader.GetString(0)) ?? throw new InvalidOperationException("Invalid chunk."));
        return chunks;
    }

    private async Task<bool> WasPublishedAsync(string fileId, string generation, CancellationToken ct)
    {
        using var cmd = _documents.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM lexical_published_generations WHERE file_id=$file AND generation=$generation";
        cmd.Parameters.AddWithValue("$file", fileId);
        cmd.Parameters.AddWithValue("$generation", generation);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private async Task<string?> ReadActiveSourceAsync(string fileId, CancellationToken ct)
    {
        using var cmd = _documents.CreateCommand();
        cmd.CommandText = @"SELECT g.file_json FROM lexical_generations g JOIN lexical_active_generations a
            ON a.file_id=g.file_id AND a.generation=g.generation WHERE g.file_id=$file";
        cmd.Parameters.AddWithValue("$file", fileId);
        var json = (string?)await cmd.ExecuteScalarAsync(ct);
        if (json == null) return null;
        return JsonSerializer.Deserialize<FileRecord>(json)?.Source ?? "";
    }

    private async Task<string?> ReadSourceAsync(string fileId, string generation, CancellationToken ct)
    {
        using var cmd = _documents.CreateCommand();
        cmd.CommandText = "SELECT file_json FROM lexical_generations WHERE file_id=$file AND generation=$generation";
        cmd.Parameters.AddWithValue("$file", fileId);
        cmd.Parameters.AddWithValue("$generation", generation);
        var json = (string?)await cmd.ExecuteScalarAsync(ct);
        return json == null ? throw new InvalidOperationException("Document generation does not exist.") :
            JsonSerializer.Deserialize<FileRecord>(json)?.Source;
    }

    public async Task<IReadOnlySet<string>> GetStaleVectorSourcesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ReadStaleVectorSourcesAsync(ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Blocks readers and marks every affected source stale before derived indexes are rebuilt.</summary>
    public async Task<VectorRecoveryLease> BeginVectorRecoveryAsync(IEnumerable<string> sources, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var names = sources.Where(s => !string.IsNullOrWhiteSpace(s) && s != "*")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await _gate.WaitAsync(ct);
        try
        {
            var alreadyGlobal = (await ReadStaleVectorSourcesAsync(ct)).Contains("*");
            using var tx = _documents.BeginTransaction();
            using var cmd = _documents.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO lexical_stale_vector_sources(source) VALUES($source);
                INSERT INTO lexical_vector_source_revisions(source,revision) VALUES($source,1)
                ON CONFLICT(source) DO UPDATE SET revision=revision+1";
            var parameter = cmd.Parameters.Add("$source", SqliteType.Text);
            foreach (var name in names.Append("*"))
            {
                ct.ThrowIfCancellationRequested();
                parameter.Value = name;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
            return new VectorRecoveryLease(this, names, alreadyGlobal);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public sealed class VectorRecoveryLease : IDisposable
    {
        private LexicalIndexCoordinator? _owner;
        private readonly HashSet<string> _markedSources;
        private readonly bool _alreadyGlobal;
        private bool _completed;

        internal VectorRecoveryLease(LexicalIndexCoordinator owner, HashSet<string> markedSources, bool alreadyGlobal)
        {
            _owner = owner;
            _markedSources = markedSources;
            _alreadyGlobal = alreadyGlobal;
        }

        public bool IsCompleted => _completed;

        /// <summary>Atomically hides the active generation while holding this lease without re-acquiring the lock.</summary>
        public async Task DeleteFileAsync(string fileId, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
            var owner = _owner ?? throw new ObjectDisposedException(nameof(VectorRecoveryLease));
            await owner.DeleteFileCoreAsync(fileId, ct);
        }

        /// <summary>Reads active files while holding this lease without re-acquiring the lock.</summary>
        public async Task<IReadOnlyList<FileRecord>> GetActiveFilesAsync(CancellationToken ct = default)
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(VectorRecoveryLease));
            return await owner.GetActiveFilesCoreAsync(ct);
        }

        /// <summary>Complete a serialized maintenance operation while leaving its sources stale.</summary>
        public async Task CompleteMaintenanceAsync(CancellationToken ct = default)
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(VectorRecoveryLease));
            if (_completed) throw new InvalidOperationException("Recovery lease has already completed.");
            if (!_alreadyGlobal)
            {
                using var cmd = owner._documents.CreateCommand();
                cmd.CommandText = "DELETE FROM lexical_stale_vector_sources WHERE source='*'";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            _completed = true;
        }

        /// <summary>Call only after the full rebuild and coverage validation complete while this lease is held.</summary>
        public async Task CompleteVerifiedAsync(IEnumerable<string> verifiedSources, CancellationToken ct = default)
        {
            var owner = _owner ?? throw new ObjectDisposedException(nameof(VectorRecoveryLease));
            if (_completed) throw new InvalidOperationException("Recovery lease has already completed.");
            var verified = verifiedSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!verified.IsSubsetOf(_markedSources)) throw new ArgumentException("Unmarked source cannot be verified.", nameof(verifiedSources));
            using var tx = owner._documents.BeginTransaction();
            using var cmd = owner._documents.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM lexical_stale_vector_sources WHERE source=$source";
            var parameter = cmd.Parameters.Add("$source", SqliteType.Text);
            foreach (var name in verified.Append("*"))
            {
                ct.ThrowIfCancellationRequested();
                parameter.Value = name;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
            _completed = true;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?._gate.Release();
        }
    }

    /// <summary>Capture revisions before a verified vector rebuild. Revisions never reset.</summary>
    public async Task<IReadOnlyDictionary<string, long>> CaptureVectorCoverageAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _documents.CreateCommand();
            cmd.CommandText = @"SELECT s.source,r.revision FROM lexical_stale_vector_sources s
                JOIN lexical_vector_source_revisions r ON r.source=s.source";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            var revisions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync(ct)) revisions.Add(reader.GetString(0), reader.GetInt64(1));
            return revisions;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Clear only sources whose complete rebuild was verified against the captured revisions.</summary>
    public async Task<IReadOnlySet<string>> ClearVerifiedVectorCoverageAsync(
        IReadOnlyDictionary<string, long> snapshot, IReadOnlyCollection<string> verifiedSources,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(verifiedSources);
        var expected = new Dictionary<string, long>(snapshot, StringComparer.OrdinalIgnoreCase);
        await _gate.WaitAsync(ct);
        try
        {
            using var tx = _documents.BeginTransaction();
            using var cmd = _documents.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"DELETE FROM lexical_stale_vector_sources WHERE source=$source
                AND EXISTS (SELECT 1 FROM lexical_vector_source_revisions r
                    WHERE r.source=$source AND r.revision=$revision)";
            var sourceParameter = cmd.Parameters.Add("$source", SqliteType.Text);
            var revisionParameter = cmd.Parameters.Add("$revision", SqliteType.Integer);
            var cleared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in verifiedSources.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                if (source == "*" || !expected.TryGetValue(source, out var revision)) continue;
                sourceParameter.Value = source;
                revisionParameter.Value = revision;
                if (await cmd.ExecuteNonQueryAsync(ct) > 0) cleared.Add(source);
            }
            tx.Commit();
            return cleared;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Serializes non-recovery indexing and management operations against the coordinator gate.</summary>
    public async Task<IDisposable> AcquireWriteLeaseAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        return new GateLease(_gate);
    }

    /// <summary>Holds the publication gate until a vector query finishes.</summary>
    public async Task<IDisposable> AcquireVectorQueryLeaseAsync(IReadOnlyCollection<string> sources,
        bool allSources, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        await _gate.WaitAsync(ct);
        try
        {
            var stale = await ReadStaleVectorSourcesAsync(ct);
            if (stale.Contains("*") || (allSources && stale.Count > 0) || sources.Any(stale.Contains))
                throw new SearchCapabilityException("vector_index_incomplete", "向量索引覆盖不完整，请先重建受影响的向量索引。",
                    sources.Where(stale.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            return new GateLease(_gate);
        }
        catch { _gate.Release(); throw; }
    }

    private async Task<HashSet<string>> ReadStaleVectorSourcesAsync(CancellationToken ct)
    {
        using var cmd = _documents.CreateCommand();
        cmd.CommandText = "SELECT source FROM lexical_stale_vector_sources";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct)) sources.Add(reader.GetString(0));
        return sources;
    }

    private sealed class GateLease : IDisposable
    {
        private SemaphoreSlim? _gate;
        public GateLease(SemaphoreSlim gate) => _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    public async Task<bool> HasIndexedGenerationAsync(string fileId, string generation, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cmd = _index.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM lexical_index_manifests WHERE file_id=$file AND generation=$generation";
            cmd.Parameters.AddWithValue("$file", fileId);
            cmd.Parameters.AddWithValue("$generation", generation);
            return await cmd.ExecuteScalarAsync(ct) != null;
        }
        finally { _gate.Release(); }
    }

    public async Task RecoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var stale = new List<(string FileId, string Generation)>();
            using (var cmd = _documents.CreateCommand())
            {
                cmd.CommandText = @"SELECT g.file_id,g.generation FROM lexical_generations g
                    LEFT JOIN lexical_active_generations a ON a.file_id=g.file_id AND a.generation=g.generation
                    WHERE a.file_id IS NULL";
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) stale.Add((reader.GetString(0), reader.GetString(1)));
            }
            foreach (var (fileId, generation) in stale)
            {
                using (var cmd = _index.CreateCommand())
                {
                    cmd.CommandText = @"DELETE FROM lexical_generation_fts WHERE file_id=$file AND generation=$generation;
                        DELETE FROM lexical_index_manifests WHERE file_id=$file AND generation=$generation";
                    cmd.Parameters.AddWithValue("$file", fileId);
                    cmd.Parameters.AddWithValue("$generation", generation);
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                using var tx = _documents.BeginTransaction();
                using var delete = _documents.CreateCommand();
                delete.Transaction = tx;
                delete.CommandText = @"DELETE FROM lexical_generation_chunks WHERE file_id=$file AND generation=$generation;
                    DELETE FROM lexical_generations WHERE file_id=$file AND generation=$generation;";
                delete.Parameters.AddWithValue("$file", fileId);
                delete.Parameters.AddWithValue("$generation", generation);
                await delete.ExecuteNonQueryAsync(ct);
                tx.Commit();
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _documents.DisposeAsync();
        await _index.DisposeAsync();
        await _exclusiveLease.DisposeAsync();
        _gate.Dispose();
    }
}
