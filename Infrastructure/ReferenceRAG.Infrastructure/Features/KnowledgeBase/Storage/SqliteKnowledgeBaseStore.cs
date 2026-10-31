using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Storage;

/// <summary>
/// 单个知识库存储实现（管理对应 data/kbs/{kbId}/kb.db 文件）
/// </summary>
public sealed class SqliteKnowledgeBaseStore : IKnowledgeBaseStore, IDisposable
{
    public string KnowledgeBaseId { get; }
    private readonly string _dbPath;
    private readonly ILogger<SqliteKnowledgeBaseStore>? _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public SqliteKnowledgeBaseStore(string knowledgeBaseId, string dbPath, ILogger<SqliteKnowledgeBaseStore>? logger = null)
    {
        KnowledgeBaseId = knowledgeBaseId ?? throw new ArgumentNullException(nameof(knowledgeBaseId));
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _logger = logger;
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _writeGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;

                CREATE TABLE IF NOT EXISTS kb_manifest (
                    kb_id TEXT PRIMARY KEY,
                    index_epoch INTEGER NOT NULL DEFAULT 1,
                    read_revision INTEGER NOT NULL DEFAULT 1,
                    content_generation INTEGER NOT NULL DEFAULT 1,
                    vector_generation INTEGER NOT NULL DEFAULT 1,
                    active_spec_hash TEXT,
                    target_spec_hash TEXT,
                    cohort_set_revision INTEGER NOT NULL DEFAULT 1,
                    active_vector_count INTEGER NOT NULL DEFAULT 0,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS documents (
                    id TEXT PRIMARY KEY,
                    connector_id TEXT NOT NULL,
                    source_key TEXT NOT NULL,
                    locator TEXT,
                    source_revision TEXT,
                    content_hash TEXT NOT NULL,
                    document_version INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE(connector_id, source_key)
                );
                CREATE INDEX IF NOT EXISTS idx_docs_conn ON documents(connector_id);

                CREATE TABLE IF NOT EXISTS chunks (
                    id TEXT PRIMARY KEY,
                    document_id TEXT NOT NULL,
                    document_version INTEGER NOT NULL,
                    ordinal INTEGER NOT NULL,
                    text TEXT NOT NULL,
                    token_count INTEGER NOT NULL,
                    embedding_text TEXT,
                    FOREIGN KEY(document_id) REFERENCES documents(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_chunks_doc_id ON chunks(document_id);

                CREATE TABLE IF NOT EXISTS chunk_vectors (
                    chunk_id TEXT PRIMARY KEY,
                    document_id TEXT NOT NULL,
                    vector_blob BLOB NOT NULL,
                    dimension INTEGER NOT NULL,
                    producer_cohort TEXT,
                    FOREIGN KEY(document_id) REFERENCES documents(id) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS idx_chunk_vectors_doc ON chunk_vectors(document_id);

                CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks USING fts5(
                    chunk_id UNINDEXED,
                    document_id UNINDEXED,
                    text
                );
            ";
            await cmd.ExecuteNonQueryAsync(ct);

            // 检查初始 Manifest
            cmd.CommandText = "SELECT COUNT(*) FROM kb_manifest WHERE kb_id = @id;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
            var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            if (count == 0)
            {
                cmd.CommandText = @"
                    INSERT INTO kb_manifest (
                        kb_id, index_epoch, read_revision, content_generation,
                        vector_generation, cohort_set_revision, active_vector_count, updated_at
                    ) VALUES (
                        @id, 1, 1, 1, 1, 1, 0, @now
                    );
                ";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            _initialized = true;
            _logger?.LogInformation("[SqliteKnowledgeBaseStore] kb.db 初始化完成: {KbId} ({DbPath})", KnowledgeBaseId, _dbPath);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IndexManifest> GetManifestAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT kb_id, index_epoch, read_revision, content_generation,
                   vector_generation, active_spec_hash, target_spec_hash,
                   cohort_set_revision, active_vector_count, updated_at
            FROM kb_manifest
            WHERE kb_id = @id;
        ";
        cmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new IndexManifest(
                KnowledgeBaseId: reader.GetString(0),
                IndexEpoch: reader.GetInt64(1),
                ReadRevision: reader.GetInt64(2),
                ContentGeneration: reader.GetInt64(3),
                VectorGeneration: reader.GetInt64(4),
                ActiveEmbeddingSpecHash: reader.IsDBNull(5) ? null : reader.GetString(5),
                TargetEmbeddingSpecHash: reader.IsDBNull(6) ? null : reader.GetString(6),
                CorpusCohortSetRevision: reader.GetInt64(7),
                ActiveVectorCount: reader.GetInt32(8),
                UpdatedAt: DateTime.Parse(reader.GetString(9))
            );
        }

        return new IndexManifest(KnowledgeBaseId, 1, 1, 1, 1, null, null, 1, 0, DateTime.UtcNow);
    }

    public async Task<CommitResult> CommitDocumentAsync(IndexBatch batch, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            // 1. 校验写入栅栏 IndexEpoch
            var currentManifest = await GetManifestInternalAsync(conn, tx, ct);
            if (batch.ExpectedIndexEpoch != 0 && batch.ExpectedIndexEpoch != currentManifest.IndexEpoch)
            {
                return new CommitResult(
                    false, 0, currentManifest.ReadRevision,
                    $"写入栅栏过期: 期望代次 Epoch {batch.ExpectedIndexEpoch} != 当前代次 Epoch {currentManifest.IndexEpoch}");
            }

            // 2. 查询当前文档版本
            long nextDocVersion = 1;
            string docId;
            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.Transaction = tx;
                checkCmd.CommandText = "SELECT id, document_version FROM documents WHERE connector_id = @c_id AND source_key = @s_key;";
                checkCmd.Parameters.AddWithValue("@c_id", batch.ConnectorId);
                checkCmd.Parameters.AddWithValue("@s_key", batch.SourceKey);
                using var r = await checkCmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    docId = r.GetString(0);
                    var existingVer = r.GetInt64(1);
                    if (batch.ExpectedDocumentVersion != 0 && batch.ExpectedDocumentVersion != existingVer)
                    {
                        return new CommitResult(
                            false, existingVer, currentManifest.ReadRevision,
                            $"乐观并发冲突: 期望版本 {batch.ExpectedDocumentVersion} != 当前版本 {existingVer}");
                    }
                    nextDocVersion = existingVer + 1;
                }
                else
                {
                    docId = Guid.NewGuid().ToString("N");
                }
            }

            var now = DateTime.UtcNow.ToString("O");

            // 3. Upsert documents
            using (var upsertCmd = conn.CreateCommand())
            {
                upsertCmd.Transaction = tx;
                upsertCmd.CommandText = @"
                    INSERT INTO documents (
                        id, connector_id, source_key, locator, source_revision,
                        content_hash, document_version, created_at, updated_at
                    ) VALUES (
                        @id, @c_id, @s_key, @locator, @s_rev,
                        @hash, @ver, @now, @now
                    )
                    ON CONFLICT(connector_id, source_key) DO UPDATE SET
                        locator = excluded.locator,
                        source_revision = excluded.source_revision,
                        content_hash = excluded.content_hash,
                        document_version = excluded.document_version,
                        updated_at = excluded.updated_at;
                ";
                upsertCmd.Parameters.AddWithValue("@id", docId);
                upsertCmd.Parameters.AddWithValue("@c_id", batch.ConnectorId);
                upsertCmd.Parameters.AddWithValue("@s_key", batch.SourceKey);
                upsertCmd.Parameters.AddWithValue("@locator", (object?)batch.Locator ?? DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@s_rev", (object?)batch.SourceRevision ?? DBNull.Value);
                upsertCmd.Parameters.AddWithValue("@hash", batch.ContentHash);
                upsertCmd.Parameters.AddWithValue("@ver", nextDocVersion);
                upsertCmd.Parameters.AddWithValue("@now", now);
                await upsertCmd.ExecuteNonQueryAsync(ct);
            }

            // 4. 清除文档历史分块、FTS 与向量
            using (var cleanCmd = conn.CreateCommand())
            {
                cleanCmd.Transaction = tx;
                cleanCmd.CommandText = @"
                    DELETE FROM fts_chunks WHERE document_id = @doc_id;
                    DELETE FROM chunk_vectors WHERE document_id = @doc_id;
                    DELETE FROM chunks WHERE document_id = @doc_id;
                ";
                cleanCmd.Parameters.AddWithValue("@doc_id", docId);
                await cleanCmd.ExecuteNonQueryAsync(ct);
            }

            // 5. 插入新 Chunks、FTS 与可选向量
            for (int i = 0; i < batch.Chunks.Count; i++)
            {
                var chunk = batch.Chunks[i];
                using var insertChunkCmd = conn.CreateCommand();
                insertChunkCmd.Transaction = tx;
                insertChunkCmd.CommandText = @"
                    INSERT INTO chunks (
                        id, document_id, document_version, ordinal,
                        text, token_count, embedding_text
                    ) VALUES (
                        @id, @doc_id, @ver, @ordinal,
                        @text, @tokens, @emb_text
                    );
                    INSERT INTO fts_chunks (chunk_id, document_id, text)
                    VALUES (@id, @doc_id, @text);
                ";
                insertChunkCmd.Parameters.AddWithValue("@id", chunk.ChunkId);
                insertChunkCmd.Parameters.AddWithValue("@doc_id", docId);
                insertChunkCmd.Parameters.AddWithValue("@ver", nextDocVersion);
                insertChunkCmd.Parameters.AddWithValue("@ordinal", chunk.Ordinal);
                insertChunkCmd.Parameters.AddWithValue("@text", chunk.Text);
                insertChunkCmd.Parameters.AddWithValue("@tokens", chunk.TokenCount);
                insertChunkCmd.Parameters.AddWithValue("@emb_text", (object?)chunk.EmbeddingText ?? DBNull.Value);
                await insertChunkCmd.ExecuteNonQueryAsync(ct);

                if (batch.Vectors != null && i < batch.Vectors.Count && batch.Vectors[i] != null)
                {
                    var vec = batch.Vectors[i];
                    var blob = FloatsToBytes(vec);
                    using var insertVecCmd = conn.CreateCommand();
                    insertVecCmd.Transaction = tx;
                    insertVecCmd.CommandText = @"
                        INSERT INTO chunk_vectors (chunk_id, document_id, vector_blob, dimension, producer_cohort)
                        VALUES (@chunk_id, @doc_id, @blob, @dim, @cohort);
                    ";
                    insertVecCmd.Parameters.AddWithValue("@chunk_id", chunk.ChunkId);
                    insertVecCmd.Parameters.AddWithValue("@doc_id", docId);
                    insertVecCmd.Parameters.AddWithValue("@blob", blob);
                    insertVecCmd.Parameters.AddWithValue("@dim", vec.Length);
                    insertVecCmd.Parameters.AddWithValue("@cohort", (object?)batch.ProducerCohort ?? DBNull.Value);
                    await insertVecCmd.ExecuteNonQueryAsync(ct);
                }
            }

            // 6. 递增 ReadRevision 并更新 Manifest
            var nextReadRevision = currentManifest.ReadRevision + 1;
            using (var manifestCmd = conn.CreateCommand())
            {
                manifestCmd.Transaction = tx;
                manifestCmd.CommandText = @"
                    UPDATE kb_manifest
                    SET read_revision = @read_rev,
                        updated_at = @now
                    WHERE kb_id = @id;
                ";
                manifestCmd.Parameters.AddWithValue("@read_rev", nextReadRevision);
                manifestCmd.Parameters.AddWithValue("@now", now);
                manifestCmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
                await manifestCmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            return new CommitResult(true, nextDocVersion, nextReadRevision);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<RecallSnapshot> RecallAsync(RecallRequest request, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var manifest = await GetManifestAsync(ct);
        var bm25Items = new List<RecallItem>();
        var vectorItems = new List<RecallItem>();

        using var conn = CreateConnection();

        // 1. BM25 全文召回
        var sanitizedQuery = SanitizeFtsQuery(request.Query);
        if (!string.IsNullOrWhiteSpace(sanitizedQuery))
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT c.id, c.document_id, d.source_key, c.text,
                           bm25(fts_chunks) AS score, c.document_version, d.connector_id
                    FROM fts_chunks f
                    JOIN chunks c ON c.id = f.chunk_id
                    JOIN documents d ON d.id = c.document_id
                    WHERE fts_chunks MATCH @match
                    ORDER BY score ASC
                    LIMIT @topK;
                ";
                cmd.Parameters.AddWithValue("@match", sanitizedQuery);
                cmd.Parameters.AddWithValue("@topK", Math.Clamp(request.TopK, 1, 100));

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var chunkId = reader.GetString(0);
                    var docId = reader.GetString(1);
                    var sourceKey = reader.GetString(2);
                    var text = reader.GetString(3);
                    var bm25Raw = reader.GetDouble(4);
                    var docVer = reader.GetInt64(5);
                    var connId = reader.GetString(6);

                    var score = 1.0 / (1.0 + Math.Max(0, bm25Raw));

                    bm25Items.Add(new RecallItem(
                        ChunkId: chunkId,
                        DocumentId: docId,
                        SourceKey: sourceKey,
                        Text: text,
                        Score: score,
                        ScoreKind: ScoreKind.BM25,
                        DocumentVersion: docVer,
                        ConnectorId: connId
                    ));
                }
            }
            catch (SqliteException ex)
            {
                _logger?.LogWarning(ex, "[SqliteKnowledgeBaseStore] FTS 查询语法异常: {Query}", sanitizedQuery);
            }
        }

        // 2. 向量相似度召回 (若传入 QueryVector)
        if (request.QueryVector != null && request.QueryVector.Length > 0)
        {
            try
            {
                using var vecCmd = conn.CreateCommand();
                vecCmd.CommandText = @"
                    SELECT v.chunk_id, v.document_id, d.source_key, c.text,
                           v.vector_blob, c.document_version, d.connector_id
                    FROM chunk_vectors v
                    JOIN chunks c ON c.id = v.chunk_id
                    JOIN documents d ON d.id = c.document_id;
                ";
                using var reader = await vecCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var chunkId = reader.GetString(0);
                    var docId = reader.GetString(1);
                    var sourceKey = reader.GetString(2);
                    var text = reader.GetString(3);
                    var blob = (byte[])reader.GetValue(4);
                    var docVer = reader.GetInt64(5);
                    var connId = reader.GetString(6);

                    var docFloats = BytesToFloats(blob);
                    var sim = CosineSimilarity(request.QueryVector, docFloats);
                    if (sim > 0.0)
                    {
                        vectorItems.Add(new RecallItem(
                            ChunkId: chunkId,
                            DocumentId: docId,
                            SourceKey: sourceKey,
                            Text: text,
                            Score: sim,
                            ScoreKind: ScoreKind.Cosine,
                            DocumentVersion: docVer,
                            ConnectorId: connId
                        ));
                    }
                }

                vectorItems = vectorItems.OrderByDescending(v => v.Score).Take(request.TopK).ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[SqliteKnowledgeBaseStore] 向量相似度查询异常");
            }
        }

        // 3. 多路融合裁决 (Hybrid / Pure Vector / Pure BM25)
        IReadOnlyList<RecallItem> finalItems;
        if (vectorItems.Count > 0 && bm25Items.Count > 0)
        {
            // 单库内置 RRF 混合融合
            var fused = new Dictionary<string, (double Score, RecallItem Item)>();
            for (int r = 0; r < bm25Items.Count; r++)
            {
                var it = bm25Items[r];
                fused[it.ChunkId] = (1.0 / (60 + r + 1), it);
            }
            for (int r = 0; r < vectorItems.Count; r++)
            {
                var it = vectorItems[r];
                var score = 1.0 / (60 + r + 1);
                if (fused.TryGetValue(it.ChunkId, out var existing))
                {
                    fused[it.ChunkId] = (existing.Score + score, existing.Item);
                }
                else
                {
                    fused[it.ChunkId] = (score, it);
                }
            }

            finalItems = fused.Values
                .OrderByDescending(x => x.Score)
                .Take(request.TopK)
                .Select(x => x.Item with { Score = x.Score, ScoreKind = ScoreKind.RRF })
                .ToList();
        }
        else if (vectorItems.Count > 0)
        {
            finalItems = vectorItems;
        }
        else
        {
            finalItems = bm25Items;
        }

        return new RecallSnapshot(
            KnowledgeBaseId, manifest.ReadRevision, manifest.ContentGeneration, manifest.VectorGeneration, finalItems);
    }

    public static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    public static double CosineSimilarity(float[] v1, float[] v2)
    {
        if (v1.Length != v2.Length || v1.Length == 0) return 0.0;
        double dot = 0.0;
        double norm1 = 0.0;
        double norm2 = 0.0;
        for (int i = 0; i < v1.Length; i++)
        {
            dot += v1[i] * v2[i];
            norm1 += v1[i] * v1[i];
            norm2 += v2[i] * v2[i];
        }
        double denom = Math.Sqrt(norm1) * Math.Sqrt(norm2);
        return denom <= 0.0 ? 0.0 : dot / denom;
    }

    public async Task<bool> DeleteDocumentAsync(string connectorId, string sourceKey, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            string? docId = null;
            using (var findCmd = conn.CreateCommand())
            {
                findCmd.Transaction = tx;
                findCmd.CommandText = "SELECT id FROM documents WHERE connector_id = @c_id AND source_key = @s_key;";
                findCmd.Parameters.AddWithValue("@c_id", connectorId);
                findCmd.Parameters.AddWithValue("@s_key", sourceKey);
                docId = (string?)await findCmd.ExecuteScalarAsync(ct);
            }

            if (docId == null) return false;

            using (var delCmd = conn.CreateCommand())
            {
                delCmd.Transaction = tx;
                delCmd.CommandText = @"
                    DELETE FROM fts_chunks WHERE document_id = @doc_id;
                    DELETE FROM chunk_vectors WHERE document_id = @doc_id;
                    DELETE FROM chunks WHERE document_id = @doc_id;
                    DELETE FROM documents WHERE id = @doc_id;
                    UPDATE kb_manifest
                    SET read_revision = read_revision + 1,
                        updated_at = @now
                    WHERE kb_id = @kb_id;
                ";
                delCmd.Parameters.AddWithValue("@doc_id", docId);
                delCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                delCmd.Parameters.AddWithValue("@kb_id", KnowledgeBaseId);
                await delCmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<IndexManifest> GetManifestInternalAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT kb_id, index_epoch, read_revision, content_generation,
                   vector_generation, active_spec_hash, target_spec_hash,
                   cohort_set_revision, active_vector_count, updated_at
            FROM kb_manifest LIMIT 1;
        ";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new IndexManifest(
                KnowledgeBaseId: reader.GetString(0),
                IndexEpoch: reader.GetInt64(1),
                ReadRevision: reader.GetInt64(2),
                ContentGeneration: reader.GetInt64(3),
                VectorGeneration: reader.GetInt64(4),
                ActiveEmbeddingSpecHash: reader.IsDBNull(5) ? null : reader.GetString(5),
                TargetEmbeddingSpecHash: reader.IsDBNull(6) ? null : reader.GetString(6),
                CorpusCohortSetRevision: reader.GetInt64(7),
                ActiveVectorCount: reader.GetInt32(8),
                UpdatedAt: DateTime.Parse(reader.GetString(9))
            );
        }
        return new IndexManifest("default", 1, 1, 1, 1, null, null, 1, 0, DateTime.UtcNow);
    }

    private static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        var words = query.Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return string.Empty;
        return string.Join(" OR ", words.Select(w => $"\"{w.Replace("\"", "\"\"")}\""));
    }

    public async Task<long> RebuildFtsIndexAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            using (var clearCmd = conn.CreateCommand())
            {
                clearCmd.Transaction = tx;
                clearCmd.CommandText = "DELETE FROM fts_chunks;";
                await clearCmd.ExecuteNonQueryAsync(ct);
            }

            using (var populateCmd = conn.CreateCommand())
            {
                populateCmd.Transaction = tx;
                populateCmd.CommandText = @"
                    INSERT INTO fts_chunks(chunk_id, document_id, text)
                    SELECT id, document_id, text FROM chunks;
                    
                    UPDATE kb_manifest
                    SET read_revision = read_revision + 1,
                        updated_at = @now
                    WHERE kb_id = @kb_id;
                    
                    SELECT read_revision FROM kb_manifest WHERE kb_id = @kb_id;
                ";
                populateCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                populateCmd.Parameters.AddWithValue("@kb_id", KnowledgeBaseId);
                var newRevision = (long)(await populateCmd.ExecuteScalarAsync(ct) ?? 0L);
                tx.Commit();
                return newRevision;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<ChunkData>> GetChunksMissingVectorsAsync(int limit = 100, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.id, c.ordinal, c.text, c.token_count
            FROM chunks c
            LEFT JOIN chunk_vectors v ON c.id = v.chunk_id
            WHERE v.chunk_id IS NULL
            ORDER BY c.created_at ASC
            LIMIT @limit;
        ";
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<ChunkData>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ChunkData(
                ChunkId: reader.GetString(0),
                Ordinal: reader.GetInt32(1),
                Text: reader.GetString(2),
                TokenCount: reader.GetInt32(3)
            ));
        }
        return list;
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _writeGate.Dispose();
        _disposed = true;
    }
}
