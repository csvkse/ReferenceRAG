using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using JiebaNet.Segmenter;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Storage;

/// <summary>
/// 单个知识库存储实现（管理对应 data/kbs/{kbId}/kb.db 文件）
/// </summary>
public sealed class SqliteKnowledgeBaseStore : IKnowledgeBaseStore, IGraphStore, IDisposable
{
    public string KnowledgeBaseId { get; }
    public IGraphStore GraphStore => this;
    private readonly string _dbPath;
    private readonly ILogger<SqliteKnowledgeBaseStore>? _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;
    private static readonly JiebaSegmenter LexicalSegmenter = new();

    public SqliteKnowledgeBaseStore(string knowledgeBaseId, string dbPath, ILogger<SqliteKnowledgeBaseStore>? logger = null)
    {
        KnowledgeBaseId = knowledgeBaseId ?? throw new ArgumentNullException(nameof(knowledgeBaseId));
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _logger = logger;
    }

    private SqliteConnection CreateConnection(bool readOnly = false)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        using (var pragmaCmd = conn.CreateCommand())
        {
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA mmap_size = 268435456;
                PRAGMA cache_size = -32000;
                PRAGMA temp_store = MEMORY;
            ";
            pragmaCmd.ExecuteNonQuery();
        }
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

                CREATE TABLE IF NOT EXISTS graph_nodes (
                    id        TEXT PRIMARY KEY,
                    title     TEXT NOT NULL DEFAULT '',
                    type      TEXT NOT NULL DEFAULT 'document',
                    chunk_ids TEXT NOT NULL DEFAULT '[]',
                    metadata  TEXT NOT NULL DEFAULT '{}'
                );
                CREATE TABLE IF NOT EXISTS graph_edges (
                    from_id     TEXT NOT NULL,
                    to_id       TEXT NOT NULL,
                    type        TEXT NOT NULL DEFAULT 'wikilink',
                    line_number INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (from_id, to_id, type)
                );
                CREATE INDEX IF NOT EXISTS idx_graph_edges_from ON graph_edges(from_id);
                CREATE INDEX IF NOT EXISTS idx_graph_edges_to   ON graph_edges(to_id);
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

            // 确保 chunks 表包含 enabled 字段与索引
            try
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE chunks ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;";
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException) { }
            try
            {
                using var idxCmd = conn.CreateCommand();
                idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_chunks_enabled ON chunks(enabled);";
                await idxCmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException) { }

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

    public async Task<KnowledgeBaseLexicalStats> GetLexicalStatsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var conn = CreateConnection();

        int totalChunks = 0;
        double avgLength = 200.0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), COALESCE(AVG(LENGTH(content)), 200.0) FROM chunks WHERE enabled = 1;";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                totalChunks = reader.GetInt32(0);
                avgLength = reader.GetDouble(1);
            }
        }

        int totalDocs = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM documents;";
            var scalar = await cmd.ExecuteScalarAsync(ct);
            if (scalar != null && scalar != DBNull.Value)
            {
                totalDocs = Convert.ToInt32(scalar);
            }
        }

        int vocabSize = totalChunks == 0 ? 0 : Math.Min((int)(totalChunks * 38), 250000);

        return new KnowledgeBaseLexicalStats(
            TotalChunks: totalChunks,
            TotalDocuments: totalDocs,
            TotalVocabularySize: vocabSize,
            AverageDocumentLength: Math.Round(avgLength, 2)
        );
    }

    public async Task EnsureLexicalIndexAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            if (!await HasLexicalIndexAsync(conn, tx, ct))
            {
                using var create = conn.CreateCommand();
                create.Transaction = tx;
                create.CommandText = "CREATE VIRTUAL TABLE fts_chunks_lexical USING fts5(chunk_id UNINDEXED, document_id UNINDEXED, text)";
                await create.ExecuteNonQueryAsync(ct);
                await PopulateLexicalIndexAsync(conn, tx, ct);
            }
            tx.Commit();
        }
        finally { _writeGate.Release(); }
    }

    private static async Task<bool> HasLexicalIndexAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='fts_chunks_lexical'";
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    private static string TokenizeLexical(string text) => string.Join(' ', LexicalSegmenter.CutForSearch(text));

    private static string SanitizeLexicalQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;
        if (query.Length > 4096) throw new ArgumentOutOfRangeException(nameof(query));
        var expansionTerms = ReferenceRAG.Core.Features.Search.PhoneticQueryExpander.GetExpansionTerms(query);
        var tokens = LexicalSegmenter.CutForSearch(query)
            .Concat(expansionTerms)
            .Select(t => t.Trim())
            .Where(t => t.Any(char.IsLetterOrDigit)).Distinct(StringComparer.OrdinalIgnoreCase).Take(64);
        return string.Join(" OR ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }

    private static async Task PopulateLexicalIndexAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        long lastRowId = 0;
        while (true)
        {
            var rows = new List<(long RowId, string ChunkId, string DocumentId, string Text)>();
            using (var select = conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT rowid,id,document_id,text FROM chunks WHERE rowid>@last ORDER BY rowid LIMIT 500";
                select.Parameters.AddWithValue("@last", lastRowId);
                using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }
            if (rows.Count == 0) break;
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO fts_chunks_lexical(chunk_id,document_id,text) VALUES(@chunk,@doc,@text)";
            var chunk = insert.Parameters.Add("@chunk", SqliteType.Text);
            var document = insert.Parameters.Add("@doc", SqliteType.Text);
            var text = insert.Parameters.Add("@text", SqliteType.Text);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                chunk.Value = row.ChunkId;
                document.Value = row.DocumentId;
                text.Value = TokenizeLexical(row.Text);
                await insert.ExecuteNonQueryAsync(ct);
            }
            lastRowId = rows[^1].RowId;
        }
    }

    public async Task<CommitResult> CommitDocumentAsync(IndexBatch batch, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            var hasLexicalIndex = await HasLexicalIndexAsync(conn, tx, ct);

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
            if (hasLexicalIndex)
            {
                using var cleanLexical = conn.CreateCommand();
                cleanLexical.Transaction = tx;
                cleanLexical.CommandText = "DELETE FROM fts_chunks_lexical WHERE document_id=@id";
                cleanLexical.Parameters.AddWithValue("@id", docId);
                await cleanLexical.ExecuteNonQueryAsync(ct);
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

                if (hasLexicalIndex)
                {
                    using var lexical = conn.CreateCommand();
                    lexical.Transaction = tx;
                    lexical.CommandText = "INSERT INTO fts_chunks_lexical(chunk_id,document_id,text) VALUES(@id,@doc,@text)";
                    lexical.Parameters.AddWithValue("@id", chunk.ChunkId);
                    lexical.Parameters.AddWithValue("@doc", docId);
                    lexical.Parameters.AddWithValue("@text", TokenizeLexical(chunk.Text));
                    await lexical.ExecuteNonQueryAsync(ct);
                }

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

    public async Task<bool> HasAuthorizedDocumentsAsync(Func<string, string, string?, bool> authorizeDocument, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorizeDocument);
        await InitializeAsync(ct);
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT connector_id,source_key,locator FROM documents";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (authorizeDocument(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))) return true;
        }
        return false;
    }

    public async Task<RecallSnapshot> RecallAsync(RecallRequest request, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        if (request.UseLexicalTokenizer) await EnsureLexicalIndexAsync(ct);
        var bm25Items = new List<RecallItem>();
        var vectorItems = new List<RecallItem>();

        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction(deferred: true);
        long readRevision, contentGeneration, vectorGeneration;
        using (var manifestCmd = conn.CreateCommand())
        {
            manifestCmd.Transaction = tx;
            manifestCmd.CommandText = "SELECT read_revision,content_generation,vector_generation FROM kb_manifest WHERE kb_id=@id";
            manifestCmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
            using var manifestReader = await manifestCmd.ExecuteReaderAsync(ct);
            if (!await manifestReader.ReadAsync(ct)) throw new InvalidOperationException("知识库索引清单不存在。");
            readRevision = manifestReader.GetInt64(0);
            contentGeneration = manifestReader.GetInt64(1);
            vectorGeneration = manifestReader.GetInt64(2);
        }
        if (request.MetadataFilters is { Count: > 0 })
            throw new NotSupportedException("知识库检索尚不支持元数据过滤。");
        var scopeClause = "";
        if (request.AuthorizeDocument != null)
        {
            using (var create = conn.CreateCommand())
            {
                create.Transaction = tx;
                create.CommandText = "CREATE TEMP TABLE authorized_kb_documents(id TEXT PRIMARY KEY) WITHOUT ROWID";
                await create.ExecuteNonQueryAsync(ct);
            }
            var authorized = new List<string>();
            using (var select = conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = "SELECT id,connector_id,source_key,locator FROM documents";
                using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    ct.ThrowIfCancellationRequested();
                    if (request.AuthorizeDocument(reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)))
                        authorized.Add(reader.GetString(0));
                }
            }
            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO authorized_kb_documents(id) VALUES(@id)";
                var id = insert.Parameters.Add("@id", SqliteType.Text);
                foreach (var documentId in authorized)
                {
                    ct.ThrowIfCancellationRequested();
                    id.Value = documentId;
                    await insert.ExecuteNonQueryAsync(ct);
                }
            }
            scopeClause = " AND EXISTS (SELECT 1 FROM authorized_kb_documents a WHERE a.id=d.id)";
        }
        bool hasVectorCoverage = true;
        bool isDegraded = false;
        string? degradeReason = null;
        if (request.QueryVector is { Length: > 0 })
        {
            using var coverage = conn.CreateCommand();
            coverage.Transaction = tx;
            coverage.CommandText = @"SELECT 1 FROM chunks c
                JOIN documents d ON d.id=c.document_id LEFT JOIN chunk_vectors v ON v.chunk_id=c.id
                WHERE c.enabled = 1 AND v.chunk_id IS NULL AND (@connector IS NULL OR d.connector_id=@connector)" + scopeClause + " LIMIT 1";
            coverage.Parameters.AddWithValue("@connector", (object?)request.ConnectorId ?? DBNull.Value);
            if (await coverage.ExecuteScalarAsync(ct) != null)
            {
                hasVectorCoverage = false;
                isDegraded = true;
                degradeReason = "知识库向量索引未完全覆盖，自动降级为全量纯词法检索";
                _logger?.LogInformation("[SqliteKnowledgeBaseStore] 知识库 {KbId} 向量未完全覆盖，自动平滑降级为纯词法检索", KnowledgeBaseId);
            }
        }

        // 1. BM25 全文召回 (支持长句分层阶梯与近音短语邻近)
        var isLongQuery = request.Query != null && request.Query.Length > 15;
        var table = request.UseLexicalTokenizer ? "fts_chunks_lexical" : "fts_chunks";
        var plan = isLongQuery && request.UseLexicalTokenizer
            ? ReferenceRAG.Core.Features.Search.Lexical.LongQueryRefiner.Default.PlanQuery(request.Query)
            : null;

        var queriesToTry = new List<string>();
        if (plan != null)
        {
            if (!string.IsNullOrWhiteSpace(plan.Tier1StrictNearQuery)) queriesToTry.Add(plan.Tier1StrictNearQuery);
            if (!string.IsNullOrWhiteSpace(plan.Tier2WeightedQuery)) queriesToTry.Add(plan.Tier2WeightedQuery);
            if (!string.IsNullOrWhiteSpace(plan.Tier3FallbackQuery)) queriesToTry.Add(plan.Tier3FallbackQuery);
        }
        else
        {
            var fallback = request.UseLexicalTokenizer ? SanitizeLexicalQuery(request.Query) : SanitizeFtsQuery(request.Query);
            if (!string.IsNullOrWhiteSpace(fallback)) queriesToTry.Add(fallback);
        }

        var seenChunkIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ftsQuery in queriesToTry)
        {
            if (bm25Items.Count >= request.TopK) break;

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $@"
                    SELECT c.id, c.document_id, d.source_key, c.text,
                           bm25({table}) AS score, c.document_version, d.connector_id, d.locator
                    FROM {table} f
                    JOIN chunks c ON c.id = f.chunk_id
                    JOIN documents d ON d.id = c.document_id
                    WHERE {table} MATCH @match AND c.enabled = 1 AND (@connector IS NULL OR d.connector_id=@connector) {scopeClause}
                    ORDER BY score ASC
                    LIMIT @topK;
                ";
                cmd.Parameters.AddWithValue("@match", ftsQuery);
                cmd.Parameters.AddWithValue("@connector", (object?)request.ConnectorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@topK", Math.Clamp(request.TopK, 1, 100));

                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var chunkId = reader.GetString(0);
                    if (!seenChunkIds.Add(chunkId)) continue;

                    var docId = reader.GetString(1);
                    var sourceKey = reader.GetString(2);
                    var text = reader.GetString(3);
                    var bm25Raw = reader.GetDouble(4);
                    var docVer = reader.GetInt64(5);
                    var connId = reader.GetString(6);

                    var score = request.UseLexicalTokenizer ? Math.Abs(bm25Raw) : 1.0 / (1.0 + Math.Max(0, bm25Raw));

                    bm25Items.Add(new RecallItem(
                        ChunkId: chunkId,
                        DocumentId: docId,
                        SourceKey: sourceKey,
                        Text: text,
                        Score: score,
                        ScoreKind: ScoreKind.BM25,
                        DocumentVersion: docVer,
                        ConnectorId: connId,
                        Locator: reader.IsDBNull(7) ? null : reader.GetString(7)
                    ));
                }
            }
            catch (SqliteException ex)
            {
                _logger?.LogDebug(ex, "[SqliteKnowledgeBaseStore] FTS 阶梯查询回退: {Query}", ftsQuery);
            }
        }

        // PRF 统计伪相关反馈重排
        if (plan != null && bm25Items.Count > 0 && request.Query != null && request.Query.Length > 20)
        {
            var prfTerms = ReferenceRAG.Core.Features.Search.Lexical.StatisticalPrfOperator.MineExpansionTerms(
                bm25Items.Select(i => i.Text), plan.DistilledTerms, 3);
            if (prfTerms.Count > 0)
            {
                for (int i = 0; i < bm25Items.Count; i++)
                {
                    var item = bm25Items[i];
                    int hits = prfTerms.Count(t => item.Text.Contains(t, StringComparison.OrdinalIgnoreCase));
                    if (hits > 0)
                    {
                        bm25Items[i] = item with { Score = item.Score + hits * 0.5 };
                    }
                }
                bm25Items = bm25Items.OrderByDescending(x => x.Score).ToList();
            }
        }

        // 2. 向量相似度召回 (若传入 QueryVector 且向量覆盖完整)
        if (hasVectorCoverage && request.QueryVector != null && request.QueryVector.Length > 0)
        {
            try
            {
                using var vecCmd = conn.CreateCommand();
                vecCmd.Transaction = tx;
                vecCmd.CommandText = @"
                    SELECT v.chunk_id, v.document_id, d.source_key, c.text,
                           v.vector_blob, c.document_version, d.connector_id, d.locator
                    FROM chunk_vectors v
                    JOIN chunks c ON c.id = v.chunk_id
                    JOIN documents d ON d.id = c.document_id
                    WHERE c.enabled = 1 AND (@connector IS NULL OR d.connector_id=@connector)" + scopeClause;
                vecCmd.Parameters.AddWithValue("@connector", (object?)request.ConnectorId ?? DBNull.Value);
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

                    var docSpan = MemoryMarshal.Cast<byte, float>(blob.AsSpan());
                    var sim = CosineSimilarity(request.QueryVector.AsSpan(), docSpan);
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
                            ConnectorId: connId,
                            Locator: reader.IsDBNull(7) ? null : reader.GetString(7)
                        ));
                    }
                }

                vectorItems = vectorItems.OrderByDescending(v => v.Score).Take(request.TopK).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "[SqliteKnowledgeBaseStore] 向量相似度查询异常");
                throw;
            }
        }

        // 3. 多路融合裁决 (Hybrid / Pure Vector / Pure BM25)
        IReadOnlyList<RecallItem> finalItems;
        if (vectorItems.Count > 0 && bm25Items.Count > 0)
        {
            // 单库内置自适应 RRF 混合融合
            var (wBm25, wVector) = ReferenceRAG.Core.Features.Search.Fusion.AdaptiveSearchFusionHelper.CalculateWeights(request.Query);
            var fused = new Dictionary<string, (double Score, RecallItem Item)>();
            for (int r = 0; r < bm25Items.Count; r++)
            {
                var it = bm25Items[r];
                fused[it.ChunkId] = (wBm25 / (60 + r + 1), it);
            }
            for (int r = 0; r < vectorItems.Count; r++)
            {
                var it = vectorItems[r];
                var score = wVector / (60 + r + 1);
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
            KnowledgeBaseId, readRevision, contentGeneration, vectorGeneration, finalItems,
            Degraded: isDegraded, DegradationReason: degradeReason);
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
        if (v1 == null || v2 == null || v1.Length != v2.Length || v1.Length == 0) return 0.0;
        return TensorPrimitives.CosineSimilarity(v1, v2);
    }

    public static double CosineSimilarity(ReadOnlySpan<float> v1, ReadOnlySpan<float> v2)
    {
        if (v1.Length != v2.Length || v1.Length == 0) return 0.0;
        return TensorPrimitives.CosineSimilarity(v1, v2);
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

            if (await HasLexicalIndexAsync(conn, tx, ct))
            {
                using var lexicalDelete = conn.CreateCommand();
                lexicalDelete.Transaction = tx;
                lexicalDelete.CommandText = "DELETE FROM fts_chunks_lexical WHERE document_id=@doc_id";
                lexicalDelete.Parameters.AddWithValue("@doc_id", docId);
                await lexicalDelete.ExecuteNonQueryAsync(ct);
            }

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

            var hasLexical = await HasLexicalIndexAsync(conn, tx, ct);
            if (hasLexical)
            {
                using var clearLexical = conn.CreateCommand();
                clearLexical.Transaction = tx;
                clearLexical.CommandText = "DELETE FROM fts_chunks_lexical";
                await clearLexical.ExecuteNonQueryAsync(ct);
                await PopulateLexicalIndexAsync(conn, tx, ct);
            }

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
            ORDER BY c.document_id, c.ordinal, c.id
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

    // ==================== IGraphStore 物理单库图谱实现 ====================

    private static GraphNode ReadNode(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Title = r.GetString(1),
        Type = r.GetString(2),
        ChunkIds = JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? new(),
        Metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(4)) ?? new()
    };

    private static string EscapeLikePattern(string pattern)
        => pattern.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    private static GraphNode? GetNodeInternal(string id, SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title, type, chunk_ids, metadata FROM graph_nodes WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadNode(reader) : null;
    }

    private static List<GraphEdge> GetEdgesInternal(string id, string[]? types, SqliteConnection conn)
    {
        var edges = new List<GraphEdge>();
        using var cmd = conn.CreateCommand();
        if (types == null || types.Length == 0)
        {
            cmd.CommandText = "SELECT from_id, to_id, type, line_number FROM graph_edges WHERE from_id = @id OR to_id = @id";
        }
        else
        {
            var inClause = string.Join(",", types.Select((_, i) => $"@t{i}"));
            cmd.CommandText = $"SELECT from_id, to_id, type, line_number FROM graph_edges WHERE (from_id = @id OR to_id = @id) AND type IN ({inClause})";
            for (int i = 0; i < types.Length; i++)
                cmd.Parameters.AddWithValue($"@t{i}", types[i]);
        }
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            edges.Add(new GraphEdge
            {
                FromId = reader.GetString(0),
                ToId = reader.GetString(1),
                Type = reader.GetString(2),
                LineNumber = reader.GetInt32(3)
            });
        }
        return edges;
    }

    public async Task UpsertNodeAsync(GraphNode node, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO graph_nodes (id, title, type, chunk_ids, metadata)
                VALUES (@id, @title, @type, @chunkIds, @metadata)
                ON CONFLICT(id) DO UPDATE SET
                    title     = excluded.title,
                    type      = excluded.type,
                    chunk_ids = excluded.chunk_ids,
                    metadata  = excluded.metadata
                """;
            cmd.Parameters.AddWithValue("@id", node.Id);
            cmd.Parameters.AddWithValue("@title", node.Title);
            cmd.Parameters.AddWithValue("@type", node.Type);
            cmd.Parameters.AddWithValue("@chunkIds", JsonSerializer.Serialize(node.ChunkIds));
            cmd.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(node.Metadata));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeGate.Release(); }
    }

    public async Task UpsertEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO graph_edges (from_id, to_id, type, line_number)
                VALUES (@from, @to, @type, @line)
                ON CONFLICT(from_id, to_id, type) DO UPDATE SET line_number = excluded.line_number
                """;
            var pFrom = cmd.Parameters.Add("@from", SqliteType.Text);
            var pTo = cmd.Parameters.Add("@to", SqliteType.Text);
            var pType = cmd.Parameters.Add("@type", SqliteType.Text);
            var pLine = cmd.Parameters.Add("@line", SqliteType.Integer);

            foreach (var e in edges)
            {
                pFrom.Value = e.FromId;
                pTo.Value = e.ToId;
                pType.Value = e.Type;
                pLine.Value = e.LineNumber;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
        }
        finally { _writeGate.Release(); }
    }

    public async Task DeleteNodeAsync(string nodeId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM graph_nodes WHERE id = @id;
                DELETE FROM graph_edges WHERE from_id = @id OR to_id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", nodeId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeGate.Release(); }
    }

    public async Task DeleteOutgoingEdgesAsync(string nodeId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM graph_edges WHERE from_id = @id";
            cmd.Parameters.AddWithValue("@id", nodeId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeGate.Release(); }
    }

    public async Task DeleteHeadingNodesAsync(string fileNodeId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var prefix = EscapeLikePattern(fileNodeId) + "#%";
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM graph_edges WHERE from_id LIKE @p ESCAPE '\' OR to_id LIKE @p ESCAPE '\';
                DELETE FROM graph_nodes WHERE id LIKE @p ESCAPE '\';
            ";
            cmd.Parameters.AddWithValue("@p", prefix);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeGate.Release(); }
    }

    public async Task UpsertFileGraphAsync(
        string fileNodeId,
        GraphNode fileNode,
        IEnumerable<GraphNode> extraNodes,
        IEnumerable<GraphEdge> edges,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var nodeList = extraNodes.ToList();
        var edgeList = edges.ToList();
        var prefix = EscapeLikePattern(fileNodeId) + "#%";

        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            void Exec(string sql, Action<SqliteCommand>? bind = null)
            {
                using var c = conn.CreateCommand();
                c.CommandText = sql;
                c.Transaction = tx;
                bind?.Invoke(c);
                c.ExecuteNonQuery();
            }

            Exec("DELETE FROM graph_edges WHERE from_id = @id",
                c => c.Parameters.AddWithValue("@id", fileNodeId));
            Exec("DELETE FROM graph_edges WHERE from_id LIKE @p ESCAPE '\\' OR to_id LIKE @p ESCAPE '\\'",
                c => c.Parameters.AddWithValue("@p", prefix));
            Exec("DELETE FROM graph_nodes WHERE id LIKE @p ESCAPE '\\'",
                c => c.Parameters.AddWithValue("@p", prefix));

            const string upsertNodeSql = """
                INSERT INTO graph_nodes (id, title, type, chunk_ids, metadata)
                VALUES (@id, @title, @type, @chunkIds, @metadata)
                ON CONFLICT(id) DO UPDATE SET
                    title     = excluded.title,
                    type      = excluded.type,
                    chunk_ids = excluded.chunk_ids,
                    metadata  = excluded.metadata
                """;

            void UpsertNode(GraphNode node)
            {
                using var c = conn.CreateCommand();
                c.CommandText = upsertNodeSql;
                c.Transaction = tx;
                c.Parameters.AddWithValue("@id", node.Id);
                c.Parameters.AddWithValue("@title", node.Title);
                c.Parameters.AddWithValue("@type", node.Type);
                c.Parameters.AddWithValue("@chunkIds", JsonSerializer.Serialize(node.ChunkIds));
                c.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(node.Metadata));
                c.ExecuteNonQuery();
            }

            UpsertNode(fileNode);
            foreach (var n in nodeList) UpsertNode(n);

            if (edgeList.Count > 0)
            {
                const string upsertEdgeSql = """
                    INSERT INTO graph_edges (from_id, to_id, type, line_number)
                    VALUES (@from, @to, @type, @line)
                    ON CONFLICT(from_id, to_id, type) DO UPDATE SET line_number = excluded.line_number
                    """;
                using var ec = conn.CreateCommand();
                ec.CommandText = upsertEdgeSql;
                ec.Transaction = tx;
                var pFrom = ec.Parameters.Add("@from", SqliteType.Text);
                var pTo = ec.Parameters.Add("@to", SqliteType.Text);
                var pType = ec.Parameters.Add("@type", SqliteType.Text);
                var pLine = ec.Parameters.Add("@line", SqliteType.Integer);
                foreach (var e in edgeList)
                {
                    pFrom.Value = e.FromId;
                    pTo.Value = e.ToId;
                    pType.Value = e.Type;
                    pLine.Value = e.LineNumber;
                    ec.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
        finally { _writeGate.Release(); }
    }

    public async Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, title, type, chunk_ids, metadata FROM graph_nodes WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", nodeId);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadNode(reader) : null;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<GraphTraversalResult> GetNeighborsAsync(
        string nodeId, int depth = 1, string[]? edgeTypes = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            depth = Math.Clamp(depth, 1, 3);
            var result = new GraphTraversalResult { RootId = nodeId, Depth = depth };
            var visited = new HashSet<string>();
            var edgeSet = new HashSet<(string from, string to, string type)>();
            var queue = new Queue<(string id, int remainingDepth)>();
            queue.Enqueue((nodeId, depth));

            while (queue.Count > 0)
            {
                var (current, remaining) = queue.Dequeue();
                if (!visited.Add(current)) continue;

                var node = GetNodeInternal(current, conn);
                if (node != null) result.Nodes.Add(node);

                if (remaining <= 0) continue;

                var edges = GetEdgesInternal(current, edgeTypes, conn);
                foreach (var e in edges)
                {
                    if (edgeSet.Add((e.FromId, e.ToId, e.Type)))
                        result.Edges.Add(e);
                    var neighbor = e.FromId == current ? e.ToId : e.FromId;
                    if (!visited.Contains(neighbor))
                        queue.Enqueue((neighbor, remaining - 1));
                }
            }
            return result;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<List<GraphNode>> SearchNodesAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            var nodes = new List<GraphNode>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, title, type, chunk_ids, metadata FROM graph_nodes
                WHERE title LIKE @q OR id LIKE @q
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@q", $"%{query}%");
            cmd.Parameters.AddWithValue("@limit", limit);
            using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) nodes.Add(ReadNode(r));
            return nodes;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<GraphStats> GetStatsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                  (SELECT COUNT(*) FROM graph_nodes) AS node_count,
                  (SELECT COUNT(*) FROM graph_nodes WHERE type = 'document') AS doc_count,
                  (SELECT COUNT(*) FROM graph_nodes WHERE type = 'tag') AS tag_count,
                  (SELECT COUNT(*) FROM graph_nodes WHERE type = 'heading') AS heading_count,
                  (SELECT COUNT(*) FROM graph_nodes WHERE type = 'external') AS external_count,
                  (SELECT COUNT(*) FROM graph_edges) AS edge_count
                """;
            using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                return new GraphStats
                {
                    NodeCount = r.GetInt32(0),
                    DocCount = r.GetInt32(1),
                    TagCount = r.GetInt32(2),
                    HeadingCount = r.GetInt32(3),
                    ExternalCount = r.GetInt32(4),
                    EdgeCount = r.GetInt32(5)
                };
            }
            return new GraphStats();
        }
        finally { _writeGate.Release(); }
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM graph_edges; DELETE FROM graph_nodes;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<int> CleanupOrphanNodesAsync(IEnumerable<string> validFileNodeIds, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var validSet = validFileNodeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            var orphanIds = new List<string>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM graph_nodes WHERE type = 'document'";
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetString(0);
                    if (!validSet.Contains(id)) orphanIds.Add(id);
                }
            }

            if (orphanIds.Count == 0) return 0;

            using var tx = conn.BeginTransaction();
            foreach (var id in orphanIds)
            {
                using var c1 = conn.CreateCommand();
                c1.Transaction = tx;
                c1.CommandText = "DELETE FROM graph_nodes WHERE id = @id";
                c1.Parameters.AddWithValue("@id", id);
                await c1.ExecuteNonQueryAsync(ct);

                using var c2 = conn.CreateCommand();
                c2.Transaction = tx;
                c2.CommandText = "DELETE FROM graph_edges WHERE from_id = @id OR to_id = @id";
                c2.Parameters.AddWithValue("@id", id);
                await c2.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
            return orphanIds.Count;
        }
        finally { _writeGate.Release(); }
    }

    // ==================== 图谱边删除实现 ====================
    public async Task<bool> DeleteEdgeAsync(string fromId, string toId, string type, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM graph_edges WHERE from_id = @from AND to_id = @to AND type = @type;";
            cmd.Parameters.AddWithValue("@from", fromId);
            cmd.Parameters.AddWithValue("@to", toId);
            cmd.Parameters.AddWithValue("@type", type);
            var affected = await cmd.ExecuteNonQueryAsync(ct);
            return affected > 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ==================== 文档增删改查实现 ====================
    public async Task<(IReadOnlyList<KnowledgeBaseDocumentRecord> Documents, int TotalCount)> GetDocumentsAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? connectorId = null,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (page - 1) * pageSize;

        using var conn = CreateConnection();
        var whereClauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(connectorId))
            whereClauses.Add("d.connector_id = @connectorId");
        if (!string.IsNullOrWhiteSpace(search))
            whereClauses.Add("(d.source_key LIKE @search OR d.locator LIKE @search)");

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

        int totalCount = 0;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM documents d {whereSql};";
            if (!string.IsNullOrWhiteSpace(connectorId)) countCmd.Parameters.AddWithValue("@connectorId", connectorId);
            if (!string.IsNullOrWhiteSpace(search)) countCmd.Parameters.AddWithValue("@search", $"%{search}%");
            totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
        }

        var list = new List<KnowledgeBaseDocumentRecord>();
        using (var queryCmd = conn.CreateCommand())
        {
            queryCmd.CommandText = $@"
                SELECT d.id, d.connector_id, d.source_key, d.locator, d.source_revision,
                       d.content_hash, d.document_version,
                       (SELECT COUNT(*) FROM chunks c WHERE c.document_id = d.id) AS chunk_count,
                       d.created_at, d.updated_at
                FROM documents d
                {whereSql}
                ORDER BY d.updated_at DESC
                LIMIT @limit OFFSET @offset;
            ";
            if (!string.IsNullOrWhiteSpace(connectorId)) queryCmd.Parameters.AddWithValue("@connectorId", connectorId);
            if (!string.IsNullOrWhiteSpace(search)) queryCmd.Parameters.AddWithValue("@search", $"%{search}%");
            queryCmd.Parameters.AddWithValue("@limit", pageSize);
            queryCmd.Parameters.AddWithValue("@offset", offset);

            using var reader = await queryCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new KnowledgeBaseDocumentRecord(
                    Id: reader.GetString(0),
                    ConnectorId: reader.GetString(1),
                    SourceKey: reader.GetString(2),
                    Locator: reader.IsDBNull(3) ? null : reader.GetString(3),
                    SourceRevision: reader.IsDBNull(4) ? null : reader.GetString(4),
                    ContentHash: reader.GetString(5),
                    DocumentVersion: reader.GetInt64(6),
                    ChunkCount: reader.GetInt32(7),
                    CreatedAt: DateTime.Parse(reader.GetString(8)),
                    UpdatedAt: DateTime.Parse(reader.GetString(9))
                ));
            }
        }
        return (list, totalCount);
    }

    public async Task<KnowledgeBaseDocumentRecord?> GetDocumentByIdAsync(string documentId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT d.id, d.connector_id, d.source_key, d.locator, d.source_revision,
                   d.content_hash, d.document_version,
                   (SELECT COUNT(*) FROM chunks c WHERE c.document_id = d.id) AS chunk_count,
                   d.created_at, d.updated_at
            FROM documents d
            WHERE d.id = @id;
        ";
        cmd.Parameters.AddWithValue("@id", documentId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new KnowledgeBaseDocumentRecord(
                Id: reader.GetString(0),
                ConnectorId: reader.GetString(1),
                SourceKey: reader.GetString(2),
                Locator: reader.IsDBNull(3) ? null : reader.GetString(3),
                SourceRevision: reader.IsDBNull(4) ? null : reader.GetString(4),
                ContentHash: reader.GetString(5),
                DocumentVersion: reader.GetInt64(6),
                ChunkCount: reader.GetInt32(7),
                CreatedAt: DateTime.Parse(reader.GetString(8)),
                UpdatedAt: DateTime.Parse(reader.GetString(9))
            );
        }
        return null;
    }

    public async Task<bool> DeleteDocumentByIdAsync(string documentId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            var hasLexical = await HasLexicalIndexAsync(conn, tx, ct);

            using var cleanCmd = conn.CreateCommand();
            cleanCmd.Transaction = tx;
            cleanCmd.CommandText = @"
                DELETE FROM fts_chunks WHERE document_id = @doc_id;
                DELETE FROM chunk_vectors WHERE document_id = @doc_id;
                DELETE FROM chunks WHERE document_id = @doc_id;
                DELETE FROM documents WHERE id = @doc_id;
            ";
            cleanCmd.Parameters.AddWithValue("@doc_id", documentId);
            var affected = await cleanCmd.ExecuteNonQueryAsync(ct);

            if (hasLexical)
            {
                using var cleanLex = conn.CreateCommand();
                cleanLex.Transaction = tx;
                cleanLex.CommandText = "DELETE FROM fts_chunks_lexical WHERE document_id = @doc_id;";
                cleanLex.Parameters.AddWithValue("@doc_id", documentId);
                await cleanLex.ExecuteNonQueryAsync(ct);
            }

            using var bumpCmd = conn.CreateCommand();
            bumpCmd.Transaction = tx;
            bumpCmd.CommandText = @"
                UPDATE kb_manifest
                SET read_revision = read_revision + 1,
                    content_generation = content_generation + 1,
                    vector_generation = vector_generation + 1,
                    updated_at = @now
                WHERE kb_id = @id;
            ";
            bumpCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            bumpCmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
            await bumpCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            return affected > 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ==================== 切片增删改查实现 (HITL) ====================
    public async Task<(IReadOnlyList<KnowledgeBaseChunkRecord> Chunks, int TotalCount)> GetChunksAsync(
        string? documentId = null,
        int page = 1,
        int pageSize = 50,
        string? search = null,
        bool? enabledOnly = null,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var offset = (page - 1) * pageSize;

        using var conn = CreateConnection();
        var whereClauses = new List<string>();
        if (!string.IsNullOrWhiteSpace(documentId))
            whereClauses.Add("c.document_id = @documentId");
        if (!string.IsNullOrWhiteSpace(search))
            whereClauses.Add("c.text LIKE @search");
        if (enabledOnly == true)
            whereClauses.Add("c.enabled = 1");

        var whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

        int totalCount = 0;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM chunks c {whereSql};";
            if (!string.IsNullOrWhiteSpace(documentId)) countCmd.Parameters.AddWithValue("@documentId", documentId);
            if (!string.IsNullOrWhiteSpace(search)) countCmd.Parameters.AddWithValue("@search", $"%{search}%");
            totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
        }

        var list = new List<KnowledgeBaseChunkRecord>();
        using (var queryCmd = conn.CreateCommand())
        {
            queryCmd.CommandText = $@"
                SELECT c.id, c.document_id, c.document_version, c.ordinal, c.text,
                       c.token_count, c.embedding_text,
                       (CASE WHEN v.chunk_id IS NOT NULL THEN 1 ELSE 0 END) AS has_vector,
                       c.enabled, d.source_key
                FROM chunks c
                LEFT JOIN chunk_vectors v ON v.chunk_id = c.id
                LEFT JOIN documents d ON d.id = c.document_id
                {whereSql}
                ORDER BY c.document_id ASC, c.ordinal ASC
                LIMIT @limit OFFSET @offset;
            ";
            if (!string.IsNullOrWhiteSpace(documentId)) queryCmd.Parameters.AddWithValue("@documentId", documentId);
            if (!string.IsNullOrWhiteSpace(search)) queryCmd.Parameters.AddWithValue("@search", $"%{search}%");
            queryCmd.Parameters.AddWithValue("@limit", pageSize);
            queryCmd.Parameters.AddWithValue("@offset", offset);

            using var reader = await queryCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new KnowledgeBaseChunkRecord(
                    Id: reader.GetString(0),
                    DocumentId: reader.GetString(1),
                    DocumentVersion: reader.GetInt64(2),
                    Ordinal: reader.GetInt32(3),
                    Text: reader.GetString(4),
                    TokenCount: reader.GetInt32(5),
                    EmbeddingText: reader.IsDBNull(6) ? null : reader.GetString(6),
                    HasVector: reader.GetInt32(7) == 1,
                    Enabled: reader.GetInt32(8) == 1,
                    DocumentSourceKey: reader.IsDBNull(9) ? null : reader.GetString(9)
                ));
            }
        }
        return (list, totalCount);
    }

    public async Task<KnowledgeBaseChunkRecord?> GetChunkByIdAsync(string chunkId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var conn = CreateConnection();
        using var queryCmd = conn.CreateCommand();
        queryCmd.CommandText = @"
            SELECT c.id, c.document_id, c.document_version, c.ordinal, c.text,
                   c.token_count, c.embedding_text,
                   (CASE WHEN v.chunk_id IS NOT NULL THEN 1 ELSE 0 END) AS has_vector,
                   c.enabled, d.source_key
            FROM chunks c
            LEFT JOIN chunk_vectors v ON v.chunk_id = c.id
            LEFT JOIN documents d ON d.id = c.document_id
            WHERE c.id = @id;
        ";
        queryCmd.Parameters.AddWithValue("@id", chunkId);
        using var reader = await queryCmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new KnowledgeBaseChunkRecord(
                Id: reader.GetString(0),
                DocumentId: reader.GetString(1),
                DocumentVersion: reader.GetInt64(2),
                Ordinal: reader.GetInt32(3),
                Text: reader.GetString(4),
                TokenCount: reader.GetInt32(5),
                EmbeddingText: reader.IsDBNull(6) ? null : reader.GetString(6),
                HasVector: reader.GetInt32(7) == 1,
                Enabled: reader.GetInt32(8) == 1,
                DocumentSourceKey: reader.IsDBNull(9) ? null : reader.GetString(9)
            );
        }
        return null;
    }

    public async Task<KnowledgeBaseChunkRecord> CreateChunkAsync(
        string? documentId,
        string text,
        string? embeddingText = null,
        float[]? vector = null,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            var hasLexical = await HasLexicalIndexAsync(conn, tx, ct);
            var now = DateTime.UtcNow.ToString("O");

            // 若 documentId 为空，确保 __manual__ 虚拟文档存在
            if (string.IsNullOrWhiteSpace(documentId))
            {
                documentId = "__manual__";
                using var ensureDoc = conn.CreateCommand();
                ensureDoc.Transaction = tx;
                ensureDoc.CommandText = @"
                    INSERT INTO documents (id, connector_id, source_key, locator, content_hash, document_version, created_at, updated_at)
                    VALUES ('__manual__', '__manual__', '手动添加分段', 'manual://chunks', 'manual_seed', 1, @now, @now)
                    ON CONFLICT(connector_id, source_key) DO UPDATE SET updated_at = @now;
                ";
                ensureDoc.Parameters.AddWithValue("@now", now);
                await ensureDoc.ExecuteNonQueryAsync(ct);
            }

            // 获取下一个 ordinal
            int nextOrdinal = 1;
            using (var ordCmd = conn.CreateCommand())
            {
                ordCmd.Transaction = tx;
                ordCmd.CommandText = "SELECT COALESCE(MAX(ordinal), 0) + 1 FROM chunks WHERE document_id = @doc_id;";
                ordCmd.Parameters.AddWithValue("@doc_id", documentId);
                nextOrdinal = Convert.ToInt32(await ordCmd.ExecuteScalarAsync(ct));
            }

            var chunkId = Guid.NewGuid().ToString("N");
            var tokenCount = text.Length;

            using (var insertChunk = conn.CreateCommand())
            {
                insertChunk.Transaction = tx;
                insertChunk.CommandText = @"
                    INSERT INTO chunks (id, document_id, document_version, ordinal, text, token_count, embedding_text, enabled)
                    VALUES (@id, @doc_id, 1, @ordinal, @text, @tokens, @emb_text, 1);
                    INSERT INTO fts_chunks (chunk_id, document_id, text)
                    VALUES (@id, @doc_id, @text);
                ";
                insertChunk.Parameters.AddWithValue("@id", chunkId);
                insertChunk.Parameters.AddWithValue("@doc_id", documentId);
                insertChunk.Parameters.AddWithValue("@ordinal", nextOrdinal);
                insertChunk.Parameters.AddWithValue("@text", text);
                insertChunk.Parameters.AddWithValue("@tokens", tokenCount);
                insertChunk.Parameters.AddWithValue("@emb_text", (object?)embeddingText ?? DBNull.Value);
                await insertChunk.ExecuteNonQueryAsync(ct);
            }

            if (hasLexical)
            {
                using var insertLex = conn.CreateCommand();
                insertLex.Transaction = tx;
                insertLex.CommandText = "INSERT INTO fts_chunks_lexical(chunk_id, document_id, text) VALUES(@id, @doc, @text);";
                insertLex.Parameters.AddWithValue("@id", chunkId);
                insertLex.Parameters.AddWithValue("@doc", documentId);
                insertLex.Parameters.AddWithValue("@text", TokenizeLexical(text));
                await insertLex.ExecuteNonQueryAsync(ct);
            }

            bool hasVector = false;
            if (vector != null && vector.Length > 0)
            {
                using var insertVec = conn.CreateCommand();
                insertVec.Transaction = tx;
                insertVec.CommandText = @"
                    INSERT INTO chunk_vectors (chunk_id, document_id, vector_blob, dimension, producer_cohort)
                    VALUES (@chunk_id, @doc_id, @blob, @dim, 'manual');
                ";
                insertVec.Parameters.AddWithValue("@chunk_id", chunkId);
                insertVec.Parameters.AddWithValue("@doc_id", documentId);
                insertVec.Parameters.AddWithValue("@blob", FloatsToBytes(vector));
                insertVec.Parameters.AddWithValue("@dim", vector.Length);
                await insertVec.ExecuteNonQueryAsync(ct);
                hasVector = true;
            }

            // 更新 manifest
            using (var bumpCmd = conn.CreateCommand())
            {
                bumpCmd.Transaction = tx;
                bumpCmd.CommandText = @"
                    UPDATE kb_manifest
                    SET read_revision = read_revision + 1,
                        content_generation = content_generation + 1,
                        vector_generation = vector_generation + 1,
                        updated_at = @now
                    WHERE kb_id = @id;
                ";
                bumpCmd.Parameters.AddWithValue("@now", now);
                bumpCmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
                await bumpCmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();

            return new KnowledgeBaseChunkRecord(
                Id: chunkId,
                DocumentId: documentId,
                DocumentVersion: 1,
                Ordinal: nextOrdinal,
                Text: text,
                TokenCount: tokenCount,
                EmbeddingText: embeddingText,
                HasVector: hasVector,
                Enabled: true,
                DocumentSourceKey: documentId == "__manual__" ? "手动添加分段" : null
            );
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<KnowledgeBaseChunkRecord?> UpdateChunkAtomicAsync(
        string chunkId,
        string newText,
        string? embeddingText = null,
        float[]? newVector = null,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            var hasLexical = await HasLexicalIndexAsync(conn, tx, ct);
            var now = DateTime.UtcNow.ToString("O");

            // 查询原切片记录
            string docId;
            long docVer;
            int ordinal;
            bool enabled;
            using (var qCmd = conn.CreateCommand())
            {
                qCmd.Transaction = tx;
                qCmd.CommandText = "SELECT document_id, document_version, ordinal, enabled FROM chunks WHERE id = @id;";
                qCmd.Parameters.AddWithValue("@id", chunkId);
                using var r = await qCmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) return null;
                docId = r.GetString(0);
                docVer = r.GetInt64(1);
                ordinal = r.GetInt32(2);
                enabled = r.GetInt32(3) == 1;
            }

            var tokenCount = newText.Length;

            // 1. 更新 chunks
            using (var uCmd = conn.CreateCommand())
            {
                uCmd.Transaction = tx;
                uCmd.CommandText = @"
                    UPDATE chunks
                    SET text = @text,
                        token_count = @tokens,
                        embedding_text = @emb_text
                    WHERE id = @id;
                ";
                uCmd.Parameters.AddWithValue("@text", newText);
                uCmd.Parameters.AddWithValue("@tokens", tokenCount);
                uCmd.Parameters.AddWithValue("@emb_text", (object?)embeddingText ?? DBNull.Value);
                uCmd.Parameters.AddWithValue("@id", chunkId);
                await uCmd.ExecuteNonQueryAsync(ct);
            }

            // 2. 更新 FTS
            using (var cleanFts = conn.CreateCommand())
            {
                cleanFts.Transaction = tx;
                cleanFts.CommandText = @"
                    DELETE FROM fts_chunks WHERE chunk_id = @id;
                    INSERT INTO fts_chunks (chunk_id, document_id, text) VALUES (@id, @doc_id, @text);
                ";
                cleanFts.Parameters.AddWithValue("@id", chunkId);
                cleanFts.Parameters.AddWithValue("@doc_id", docId);
                cleanFts.Parameters.AddWithValue("@text", newText);
                await cleanFts.ExecuteNonQueryAsync(ct);
            }

            if (hasLexical)
            {
                using var cleanLex = conn.CreateCommand();
                cleanLex.Transaction = tx;
                cleanLex.CommandText = @"
                    DELETE FROM fts_chunks_lexical WHERE chunk_id = @id;
                    INSERT INTO fts_chunks_lexical (chunk_id, document_id, text) VALUES (@id, @doc_id, @text);
                ";
                cleanLex.Parameters.AddWithValue("@id", chunkId);
                cleanLex.Parameters.AddWithValue("@doc_id", docId);
                cleanLex.Parameters.AddWithValue("@text", TokenizeLexical(newText));
                await cleanLex.ExecuteNonQueryAsync(ct);
            }

            // 3. 更新向量
            bool hasVector = false;
            if (newVector != null && newVector.Length > 0)
            {
                using var upsertVec = conn.CreateCommand();
                upsertVec.Transaction = tx;
                upsertVec.CommandText = @"
                    INSERT INTO chunk_vectors (chunk_id, document_id, vector_blob, dimension, producer_cohort)
                    VALUES (@chunk_id, @doc_id, @blob, @dim, 'hitl_edit')
                    ON CONFLICT(chunk_id) DO UPDATE SET
                        vector_blob = excluded.vector_blob,
                        dimension = excluded.dimension,
                        producer_cohort = excluded.producer_cohort;
                ";
                upsertVec.Parameters.AddWithValue("@chunk_id", chunkId);
                upsertVec.Parameters.AddWithValue("@doc_id", docId);
                upsertVec.Parameters.AddWithValue("@blob", FloatsToBytes(newVector));
                upsertVec.Parameters.AddWithValue("@dim", newVector.Length);
                await upsertVec.ExecuteNonQueryAsync(ct);
                hasVector = true;
            }
            else
            {
                // 检查是否已有向量
                using var checkVec = conn.CreateCommand();
                checkVec.Transaction = tx;
                checkVec.CommandText = "SELECT COUNT(*) FROM chunk_vectors WHERE chunk_id = @id;";
                checkVec.Parameters.AddWithValue("@id", chunkId);
                hasVector = Convert.ToInt64(await checkVec.ExecuteScalarAsync(ct)) > 0;
            }

            // 4. 更新 manifest 与 documents
            using (var bumpCmd = conn.CreateCommand())
            {
                bumpCmd.Transaction = tx;
                bumpCmd.CommandText = @"
                    UPDATE kb_manifest
                    SET read_revision = read_revision + 1,
                        content_generation = content_generation + 1,
                        vector_generation = vector_generation + 1,
                        updated_at = @now
                    WHERE kb_id = @id;
                    UPDATE documents SET updated_at = @now WHERE id = @doc_id;
                ";
                bumpCmd.Parameters.AddWithValue("@now", now);
                bumpCmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
                bumpCmd.Parameters.AddWithValue("@doc_id", docId);
                await bumpCmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();

            return new KnowledgeBaseChunkRecord(
                Id: chunkId,
                DocumentId: docId,
                DocumentVersion: docVer,
                Ordinal: ordinal,
                Text: newText,
                TokenCount: tokenCount,
                EmbeddingText: embeddingText,
                HasVector: hasVector,
                Enabled: enabled,
                DocumentSourceKey: null
            );
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> ToggleChunkAsync(string chunkId, bool enabled, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE chunks SET enabled = @enabled WHERE id = @id;
                UPDATE kb_manifest SET read_revision = read_revision + 1, updated_at = @now WHERE kb_id = @kbId;
            ";
            cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", chunkId);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@kbId", KnowledgeBaseId);
            var affected = await cmd.ExecuteNonQueryAsync(ct);
            return affected > 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> DeleteChunkByIdAsync(string chunkId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            var hasLexical = await HasLexicalIndexAsync(conn, tx, ct);

            using var cleanCmd = conn.CreateCommand();
            cleanCmd.Transaction = tx;
            cleanCmd.CommandText = @"
                DELETE FROM chunk_vectors WHERE chunk_id = @id;
                DELETE FROM fts_chunks WHERE chunk_id = @id;
                DELETE FROM chunks WHERE id = @id;
            ";
            cleanCmd.Parameters.AddWithValue("@id", chunkId);
            var affected = await cleanCmd.ExecuteNonQueryAsync(ct);

            if (hasLexical)
            {
                using var cleanLex = conn.CreateCommand();
                cleanLex.Transaction = tx;
                cleanLex.CommandText = "DELETE FROM fts_chunks_lexical WHERE chunk_id = @id;";
                cleanLex.Parameters.AddWithValue("@id", chunkId);
                await cleanLex.ExecuteNonQueryAsync(ct);
            }

            using var bumpCmd = conn.CreateCommand();
            bumpCmd.Transaction = tx;
            bumpCmd.CommandText = @"
                UPDATE kb_manifest
                SET read_revision = read_revision + 1,
                    content_generation = content_generation + 1,
                    vector_generation = vector_generation + 1,
                    updated_at = @now
                WHERE kb_id = @kb_id;
            ";
            bumpCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            bumpCmd.Parameters.AddWithValue("@kb_id", KnowledgeBaseId);
            await bumpCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            return affected > 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
