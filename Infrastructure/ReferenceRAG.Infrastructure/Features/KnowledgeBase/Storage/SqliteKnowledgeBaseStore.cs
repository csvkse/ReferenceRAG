using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Infrastructure.Features.Search.Lexical;
using ReferenceRAG.Core.Features.Search.Lexical;
using ReferenceRAG.Infrastructure.Features.Search.Fusion;
using JiebaNet.Segmenter;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Tracing;

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

    // ── 词法查询统计缓存 ────────────────────────────────────────────────────
    // corpusSize = COUNT(*) chunks（57k 行约 18ms），df 走「带上限的 COUNT」逐词统计（11 词约 20ms）。
    // 两者都只在 read_revision 变化（内容代次推进）时失效，热路径零成本。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Revision, int Total)> CorpusSizeCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> TermDfCache = new();
    private const int TermDfCacheCapacity = 50_000;

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
                    previous_spec_hash TEXT,
                    cohort_set_revision INTEGER NOT NULL DEFAULT 1,
                    active_vector_count INTEGER NOT NULL DEFAULT 0,
                    lexical_generation INTEGER NOT NULL DEFAULT 0,
                    lexical_indexed_rowid INTEGER NOT NULL DEFAULT 0,
                    tokenizer_version TEXT,
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
                    start_line INTEGER NOT NULL DEFAULT 0,
                    end_line INTEGER NOT NULL DEFAULT 0,
                    heading_path TEXT,
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

                -- 词法倒排索引：与 fts_chunks 对称建表，避免首次查询时在读路径上现场建表与全量分词
                CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks_lexical USING fts5(
                    chunk_id UNINDEXED,
                    document_id UNINDEXED,
                    title,
                    heading,
                    text
                );

                -- 更激进的自动段合并（默认 4）：本表按 200 块/批写入，默认值来不及合并，
                -- 段数上千后，查询一个不存在的词要在每个 segment 各查一次 term 索引（实测 230ms）。
                -- 注意语法：FTS5 的配置命令必须写成 (表名, rank) VALUES('键', 值)，
                -- 写成 VALUES('automerge=8') 会报 SQL logic error。
                INSERT INTO fts_chunks_lexical(fts_chunks_lexical, rank) VALUES('automerge', 8);
                INSERT INTO fts_chunks_lexical(fts_chunks_lexical, rank) VALUES('crisismerge', 32);

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

            // 升级与兼容性探测：为历史已有数据库平滑补全 start_line, end_line, heading_path 字段
            try {
                using var alterLine1 = conn.CreateCommand();
                alterLine1.CommandText = "ALTER TABLE chunks ADD COLUMN start_line INTEGER NOT NULL DEFAULT 0;";
                await alterLine1.ExecuteNonQueryAsync(ct);
            } catch (SqliteException) { }

            try {
                using var alterLine2 = conn.CreateCommand();
                alterLine2.CommandText = "ALTER TABLE chunks ADD COLUMN end_line INTEGER NOT NULL DEFAULT 0;";
                await alterLine2.ExecuteNonQueryAsync(ct);
            } catch (SqliteException) { }

            try {
                using var alterHeading = conn.CreateCommand();
                alterHeading.CommandText = "ALTER TABLE chunks ADD COLUMN heading_path TEXT;";
                await alterHeading.ExecuteNonQueryAsync(ct);
            } catch (SqliteException) { }

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

            // 词法索引代次与断点续传检查点（历史库平滑补列）
            try
            {
                using var alterLexGen = conn.CreateCommand();
                alterLexGen.CommandText = "ALTER TABLE kb_manifest ADD COLUMN lexical_generation INTEGER NOT NULL DEFAULT 0;";
                await alterLexGen.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException) { }
            try
            {
                using var alterLexRow = conn.CreateCommand();
                alterLexRow.CommandText = "ALTER TABLE kb_manifest ADD COLUMN lexical_indexed_rowid INTEGER NOT NULL DEFAULT 0;";
                await alterLexRow.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException) { }
            try
            {
                using var alterTokVer = conn.CreateCommand();
                alterTokVer.CommandText = "ALTER TABLE kb_manifest ADD COLUMN tokenizer_version TEXT;";
                await alterTokVer.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException) { }
            try
            {
                using var alterPrevSpec = conn.CreateCommand();
                alterPrevSpec.CommandText = "ALTER TABLE kb_manifest ADD COLUMN previous_spec_hash TEXT;";
                await alterPrevSpec.ExecuteNonQueryAsync(ct);
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
                   cohort_set_revision, active_vector_count, updated_at, previous_spec_hash
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
                UpdatedAt: DateTime.Parse(reader.GetString(9)),
                PreviousEmbeddingSpecHash: reader.IsDBNull(10) ? null : reader.GetString(10)
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
            cmd.CommandText = "SELECT COUNT(*), COALESCE(AVG(LENGTH(text)), 200.0) FROM chunks WHERE enabled = 1;";
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

    /// <summary>
    /// 确保词法索引就绪。⚠️ 仅供后台任务 / 索引期调用；查询读路径必须使用 <see cref="GetLexicalIndexStatusAsync"/>，
    /// 严禁在读路径上执行 DDL 或全量分词（历史事故：首次词法查询被阻塞 16 秒）。
    /// </summary>
    public async Task EnsureLexicalIndexAsync(CancellationToken ct = default)
    {
        await BackfillLexicalIndexAsync(500, null, ct);
    }

    /// <summary>
    /// O(1) 裁决词法索引状态：只读连接、无 DDL、无写栅栏，可安全运行在查询请求线程。
    /// 快路径仅读 kb_manifest 一行；仅在"代次不一致/表缺失"时才做一次 COUNT 以给出补齐进度。
    /// </summary>
    public async Task<LexicalIndexStatusInfo> GetLexicalIndexStatusAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var span = TimingScope.Begin("kb.lexical-index.status", SpanCategory.SubPhase, "词法索引状态裁决");
        using var conn = CreateConnection(readOnly: true);

        long contentGen = 0, lexGen = 0, rowId = 0;
        string? tokenizerVersion = null;
        using (var m = conn.CreateCommand())
        {
            m.CommandText = "SELECT content_generation, lexical_generation, lexical_indexed_rowid, tokenizer_version FROM kb_manifest WHERE kb_id=@id";
            m.Parameters.AddWithValue("@id", KnowledgeBaseId);
            using var r = await m.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                contentGen = r.GetInt64(0);
                lexGen = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                rowId = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                tokenizerVersion = r.IsDBNull(3) ? null : r.GetString(3);
            }
        }

        bool tableExists;
        using (var t = conn.CreateCommand())
        {
            t.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='fts_chunks_lexical'";
            tableExists = await t.ExecuteScalarAsync(ct) != null;
        }

        // 快路径：表存在 + 代次对齐 + 有检查点 → 就绪，无需 COUNT
        if (tableExists && lexGen > 0 && lexGen == contentGen && rowId > 0)
        {
            span.SetDetail("Ready (fast-path)");
            return new LexicalIndexStatusInfo(LexicalIndexStatus.Ready, -1, -1, contentGen, lexGen, tokenizerVersion, rowId);
        }

        int chunks;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*) FROM chunks";
            chunks = Convert.ToInt32(await c.ExecuteScalarAsync(ct) ?? 0);
        }

        if (!tableExists)
        {
            span.SetDetail(chunks == 0 ? "Empty (无表/无分块)" : "Absent (表缺失)");
            return new LexicalIndexStatusInfo(
                chunks == 0 ? LexicalIndexStatus.Empty : LexicalIndexStatus.Absent,
                chunks, 0, contentGen, lexGen, tokenizerVersion, rowId);
        }

        int indexed;
        using (var i = conn.CreateCommand())
        {
            i.CommandText = "SELECT COUNT(*) FROM fts_chunks_lexical";
            indexed = Convert.ToInt32(await i.ExecuteScalarAsync(ct) ?? 0);
        }

        var status = chunks == 0
            ? LexicalIndexStatus.Empty
            : indexed == 0
                ? LexicalIndexStatus.Absent
                : indexed < chunks
                    ? LexicalIndexStatus.Stale
                    : (lexGen == contentGen ? LexicalIndexStatus.Ready : LexicalIndexStatus.Stale);

        span.SetDetail($"{status} (chunks={chunks}, indexed={indexed}, gen={lexGen}/{contentGen})");
        span.SetOutput(indexed);
        return new LexicalIndexStatusInfo(status, chunks, indexed, contentGen, lexGen, tokenizerVersion, rowId);
    }

    /// <summary>
    /// 后台分批补齐词法倒排索引：每批独立事务 + 检查点持久化（可断点续传）+ 并行分词。
    /// 每批仅短暂持有写栅栏，不会长时间阻塞写入；查询读路径不调用本方法。
    /// </summary>
    public async Task<LexicalIndexBackfillResult> BackfillLexicalIndexAsync(
        int batchSize = 500,
        IProgress<LexicalIndexProgress>? progress = null,
        CancellationToken ct = default,
        bool force = false)
    {
        await InitializeAsync(ct);
        var sw = Stopwatch.StartNew();
        using var span = TimingScope.Begin("kb.lexical-index.backfill", SpanCategory.Io, "词法索引补齐");
        span.MarkColdStart();

        await EnsureLexicalTableAsync(ct);

        int total;
        long checkpoint;
        using (var conn = CreateConnection(readOnly: true))
        {
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM chunks";
                total = Convert.ToInt32(await c.ExecuteScalarAsync(ct) ?? 0);
            }
            using (var m = conn.CreateCommand())
            {
                m.CommandText = "SELECT lexical_indexed_rowid FROM kb_manifest WHERE kb_id=@id";
                m.Parameters.AddWithValue("@id", KnowledgeBaseId);
                checkpoint = Convert.ToInt64(await m.ExecuteScalarAsync(ct) ?? 0L);
            }
        }

        // 运维强制重建：忽略检查点与快路径，先清空再全量重建（用于清理历史重复行 / 强制重分词）
        if (force)
        {
            await ClearLexicalIndexAsync(ct);
            checkpoint = 0;
            span.SetDetail("force=true，已清空词法表并从零重建");
        }

        int processed = 0, batchNo = 0;

        // ── 升级兼容快路径 ────────────────────────────────────────────────
        // 历史库可能早已存在完整词法索引，只是 manifest 的 lexical_generation 列是本次新增的（初值 0）。
        // 这种情况下若直接按 checkpoint=0 重建，会对 5w+ 块重复分词，并因 FTS5 无唯一约束产生重复行。
        // 因此先做一次轻量核对：fts 行数与 distinct chunk_id 数都覆盖 chunks 时，仅对齐代次即可。
        if (!force && total > 0 && checkpoint <= 0)
        {
            int indexedRows, distinctIds;
            using (var conn = CreateConnection(readOnly: true))
            using (var i = conn.CreateCommand())
            {
                i.CommandText = "SELECT COUNT(*), COUNT(DISTINCT chunk_id) FROM fts_chunks_lexical";
                using var r = await i.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    indexedRows = r.GetInt32(0);
                    distinctIds = r.GetInt32(1);
                }
                else
                {
                    indexedRows = distinctIds = 0;
                }
            }

            if (distinctIds >= total)
            {
                await MarkLexicalGenerationAsync(ct);

                // 升级/并发补齐留下的重复行必须在这里顺手清掉：df 虚高会让 bm25 打分失真，
                // 倒排表翻倍会让查询耗时翻倍。这一步是幂等的，重复执行无副作用。
                var repaired = 0;
                if (indexedRows > distinctIds)
                {
                    repaired = await RepairLexicalIndexAsync(ct);
                }

                span.SetDetail($"快路径：索引已完整（{indexedRows} 行 / {distinctIds} 块），仅对齐代次" +
                               (repaired > 0 ? $"，自愈清理 {repaired} 行" : string.Empty));
                _logger?.LogInformation(
                    "[SqliteKnowledgeBaseStore] 词法索引已完整，跳过重建仅对齐代次: {KbId}，{Indexed}/{Total} 块，清理重复 {Repaired} 行，耗时 {Elapsed}ms",
                    KnowledgeBaseId, distinctIds, total, repaired, sw.ElapsedMilliseconds);
                return new LexicalIndexBackfillResult(
                    KnowledgeBaseId, 0, total, sw.ElapsedMilliseconds, true, false,
                    $"索引已完整（{distinctIds}/{total}），仅对齐代次");
            }

            // 索引不完整：从零重建前必须清空历史残留，避免重复行污染检索结果
            await ClearLexicalIndexAsync(ct);
        }

        try
        {
            int casConflicts = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var rows = await ReadChunkBatchAsync(checkpoint, Math.Clamp(batchSize, 50, 2000), ct);
                if (rows.Count == 0) break;

                var tokenized = TokenizeBatch(rows);
                var written = await WriteLexicalBatchAsync(rows, tokenized, checkpoint, ct);
                if (!written)
                {
                    // 另一个写者已推进检查点：本批作废，从最新检查点续跑（绝不重复插入）。
                    var latest = await ReadLexicalCheckpointAsync(ct);
                    casConflicts++;
                    _logger?.LogInformation(
                        "[SqliteKnowledgeBaseStore] 词法补齐检查点冲突（{KbId}）：本批已回滚，检查点 {From} → {To}",
                        KnowledgeBaseId, checkpoint, latest);
                    if (latest > checkpoint)
                    {
                        checkpoint = latest;
                        continue;
                    }
                    if (casConflicts >= 5)
                    {
                        span.SetDetail($"检查点竞争 {casConflicts} 次，让出本次补齐");
                        break;
                    }
                    continue;
                }

                checkpoint = rows[^1].RowId;
                processed += rows.Count;
                batchNo++;
                progress?.Report(new LexicalIndexProgress(KnowledgeBaseId, processed, total, batchNo, "分词与写入"));
            }

            // 完成后对齐代次（内容未变更时即等价于"索引与内容一致"）
            await MarkLexicalGenerationAsync(ct);

            // 自愈：历史并发补齐可能留下重复行/孤儿行，补齐收尾时一次性核对并清理。
            var (rows_, distinct) = await GetLexicalRowCountsAsync(ct);
            if (rows_ != distinct || rows_ != total)
            {
                var removed = await RepairLexicalIndexAsync(ct);
                span.SetDetail($"补齐 {processed}/{total} 块，{batchNo} 批；自愈清理 {removed} 行（{rows_}→{rows_ - removed}）");
            }
            else
            {
                span.SetDetail($"补齐 {processed}/{total} 块，{batchNo} 批");
            }

            // 段合并：数百次 commit 会留下数百个 segment，"词不存在"的查询要在每个 segment
            // 的 term 索引里各查一次（实测 230ms vs 有命中 4ms）。补齐收尾一次性合并。
            await OptimizeLexicalIndexAsync(ct);

            span.SetOutput(processed);
            _logger?.LogInformation(
                "[SqliteKnowledgeBaseStore] 词法索引补齐完成: {KbId}，{Processed}/{Total} 块，{Batches} 批，耗时 {Elapsed}ms",
                KnowledgeBaseId, processed, total, batchNo, sw.ElapsedMilliseconds);

            return new LexicalIndexBackfillResult(KnowledgeBaseId, processed, total, sw.ElapsedMilliseconds, true, false);
        }
        catch (OperationCanceledException)
        {
            span.SetDetail($"已取消（检查点 {checkpoint}，已处理 {processed}/{total}）");
            throw;
        }
        catch (Exception ex)
        {
            span.Fail(ex);
            _logger?.LogError(ex, "[SqliteKnowledgeBaseStore] 词法索引补齐失败: {KbId}", KnowledgeBaseId);
            return new LexicalIndexBackfillResult(KnowledgeBaseId, processed, total, sw.ElapsedMilliseconds, false, false, ex.Message);
        }
    }

    /// <summary>建表（DDL 仅在写栅栏内短时执行，幂等）。</summary>
    private async Task EnsureLexicalTableAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS fts_chunks_lexical USING fts5(
                    chunk_id UNINDEXED, document_id UNINDEXED, title, heading, text
                );
                INSERT INTO fts_chunks_lexical(fts_chunks_lexical, rank) VALUES('automerge', 8);
                INSERT INTO fts_chunks_lexical(fts_chunks_lexical, rank) VALUES('crisismerge', 32);";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeGate.Release(); }
    }

    private async Task<List<(long RowId, string ChunkId, string DocumentId, string SourceKey, string Text)>> ReadChunkBatchAsync(
        long afterRowId, int limit, CancellationToken ct)
    {
        var rows = new List<(long, string, string, string, string)>(limit);
        using var conn = CreateConnection(readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.rowid, c.id, c.document_id, d.source_key, c.text
            FROM chunks c JOIN documents d ON d.id = c.document_id
            WHERE c.rowid > @last ORDER BY c.rowid LIMIT @lim";
        cmd.Parameters.AddWithValue("@last", afterRowId);
        cmd.Parameters.AddWithValue("@lim", limit);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }
        return rows;
    }

    /// <summary>并行分词（jieba 只读字典，线程安全），显著降低全量构建的 CPU 墙钟时间。</summary>
    private static (string Title, string Heading, string Text)[] TokenizeBatch(
        List<(long RowId, string ChunkId, string DocumentId, string SourceKey, string Text)> rows)
    {
        var result = new (string, string, string)[rows.Count];
        var dop = Math.Clamp(Environment.ProcessorCount, 1, 4);
        if (dop <= 1)
        {
            for (int i = 0; i < rows.Count; i++)
                result[i] = TokenizeRow(rows[i].SourceKey, rows[i].Text);
            return result;
        }

        Parallel.For(0, rows.Count, new ParallelOptions { MaxDegreeOfParallelism = dop }, i =>
        {
            result[i] = TokenizeRow(rows[i].SourceKey, rows[i].Text);
        });
        return result;
    }

    private static (string Title, string Heading, string Text) TokenizeRow(string sourceKey, string text)
        => (TokenizeLexical(ExtractTitle(sourceKey, text)), TokenizeLexical(ExtractHeading(text)), TokenizeLexical(text));

    /// <summary>清空词法索引（仅用于从零重建前的清理，避免 FTS5 无唯一约束导致的重复行）。</summary>
    private async Task ClearLexicalIndexAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM fts_chunks_lexical";
                await del.ExecuteNonQueryAsync(ct);
            }
            using (var reset = conn.CreateCommand())
            {
                reset.Transaction = tx;
                reset.CommandText = "UPDATE kb_manifest SET lexical_indexed_rowid = 0, lexical_generation = 0 WHERE kb_id = @id";
                reset.Parameters.AddWithValue("@id", KnowledgeBaseId);
                await reset.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// 写入一批词法行。返回 <c>false</c> 表示检查点已被另一个写者推进（CAS 失败），本批已回滚。
    ///
    /// 为什么需要 CAS：<c>_writeGate</c> 是**实例级**信号量，而 <c>SqliteKnowledgeBaseStore</c>
    /// 按租约创建、可同时存在多个实例（后台 Worker + 手动触发 + 对账作业）。
    /// 两个实例从同一检查点出发、各自插入同一批 500 行，就会得到 rowid 相差 500 的重复行
    /// （生产库实测 107,966 行 / 57,733 块 = 1.87 倍膨胀，df 甚至超过语料规模）。
    /// FTS5 没有唯一约束，重复行会让倒排表翻倍，是首查 18s 的第二个乘数。
    /// </summary>
    private async Task<bool> WriteLexicalBatchAsync(
        List<(long RowId, string ChunkId, string DocumentId, string SourceKey, string Text)> rows,
        (string Title, string Heading, string Text)[] tokenized,
        long expectedCheckpoint,
        CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();

            // 先做检查点 CAS：只有"我读到的检查点仍是当前值"才允许写入，跨实例互斥且零新增表。
            int claimed;
            using (var cp = conn.CreateCommand())
            {
                cp.Transaction = tx;
                cp.CommandText = @"UPDATE kb_manifest
                    SET lexical_indexed_rowid=@row, tokenizer_version=@ver, updated_at=@now
                    WHERE kb_id=@id AND lexical_indexed_rowid=@expected";
                cp.Parameters.AddWithValue("@row", rows[^1].RowId);
                cp.Parameters.AddWithValue("@ver", LexicalTokenizerVersion);
                cp.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                cp.Parameters.AddWithValue("@id", KnowledgeBaseId);
                cp.Parameters.AddWithValue("@expected", expectedCheckpoint);
                claimed = await cp.ExecuteNonQueryAsync(ct);
            }

            if (claimed == 0)
            {
                tx.Rollback();
                return false;
            }

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO fts_chunks_lexical(chunk_id, document_id, title, heading, text) VALUES(@c,@d,@t,@h,@x)";
                var pc = insert.Parameters.Add("@c", SqliteType.Text);
                var pd = insert.Parameters.Add("@d", SqliteType.Text);
                var pt = insert.Parameters.Add("@t", SqliteType.Text);
                var ph = insert.Parameters.Add("@h", SqliteType.Text);
                var px = insert.Parameters.Add("@x", SqliteType.Text);
                for (int i = 0; i < rows.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    pc.Value = rows[i].ChunkId;
                    pd.Value = rows[i].DocumentId;
                    pt.Value = tokenized[i].Title;
                    ph.Value = tokenized[i].Heading;
                    px.Value = tokenized[i].Text;
                    await insert.ExecuteNonQueryAsync(ct);
                }
            }
            tx.Commit();
            return true;
        }
        finally { _writeGate.Release(); }
    }

    private async Task<long> ReadLexicalCheckpointAsync(CancellationToken ct)
    {
        using var conn = CreateConnection(readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT lexical_indexed_rowid FROM kb_manifest WHERE kb_id=@id";
        cmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    private async Task MarkLexicalGenerationAsync(CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE kb_manifest
                    SET lexical_generation = content_generation,
                        lexical_indexed_rowid = COALESCE((SELECT MAX(rowid) FROM chunks), 0),
                        tokenizer_version = @ver,
                        updated_at = @now
                    WHERE kb_id = @id";
                cmd.Parameters.AddWithValue("@ver", LexicalTokenizerVersion);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>分词器版本号：写入 manifest，版本漂移时触发后台重建，防止索引/查询词项不一致。</summary>
    private const string LexicalTokenizerVersion = "jieba-0.42.2/search-v1";

    private async Task<(int Rows, int Distinct)> GetLexicalRowCountsAsync(CancellationToken ct)
    {
        using var conn = CreateConnection(readOnly: true);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COUNT(DISTINCT chunk_id) FROM fts_chunks_lexical";
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            return (r.GetInt32(0), r.GetInt32(1));
        }
        return (0, 0);
    }

    /// <summary>
    /// 清理词法索引中的重复行与孤儿行（FTS5 无唯一约束，历史并发补齐/断点续传会留下重复行）。
    /// 仅在后台补齐收尾与运维端点调用，**绝不在读路径执行**。
    /// 重复行不是"脏数据"这么简单：倒排表长度翻倍会让 df 虚高、bm25 打分失真、查询耗时翻倍。
    /// </summary>
    public async Task<int> RepairLexicalIndexAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var span = TimingScope.Begin("kb.lexical-index.repair", SpanCategory.Io, "词法索引去重");
        var sw = Stopwatch.StartNew();
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            int removed = 0;

            // 每个 chunk_id 只保留最小 rowid（首次写入的那一行）
            using (var keep = conn.CreateCommand())
            {
                keep.Transaction = tx;
                keep.CommandText = @"
                    DROP TABLE IF EXISTS temp.kb_lex_keep;
                    CREATE TEMP TABLE kb_lex_keep(rowid INTEGER PRIMARY KEY);
                    INSERT INTO kb_lex_keep(rowid) SELECT MIN(rowid) FROM fts_chunks_lexical GROUP BY chunk_id;";
                await keep.ExecuteNonQueryAsync(ct);
            }

            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM fts_chunks_lexical WHERE rowid NOT IN (SELECT rowid FROM temp.kb_lex_keep)";
                removed += await del.ExecuteNonQueryAsync(ct);
            }

            // 孤儿行：chunks 已被删除但词法表还留着（文档删除/重分块的历史残留）
            using (var orphan = conn.CreateCommand())
            {
                orphan.Transaction = tx;
                orphan.CommandText = "DELETE FROM fts_chunks_lexical WHERE chunk_id NOT IN (SELECT id FROM chunks)";
                removed += await orphan.ExecuteNonQueryAsync(ct);
            }

            using (var drop = conn.CreateCommand())
            {
                drop.Transaction = tx;
                drop.CommandText = "DROP TABLE IF EXISTS temp.kb_lex_keep";
                await drop.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();

            if (removed > 0)
            {
                _logger?.LogInformation(
                    "[SqliteKnowledgeBaseStore] 词法索引去重完成: {KbId}，清理 {Removed} 行，耗时 {Elapsed}ms",
                    KnowledgeBaseId, removed, sw.ElapsedMilliseconds);
            }
            span.SetOutput(removed);
            span.SetDetail($"清理 {removed} 行重复/孤儿");
            return removed;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>
    /// 合并 FTS5 段（<c>optimize</c>）。
    /// <para>
    /// <b>为什么必须做：</b>FTS5 每次写入事务产生一个 segment。57k 块按 200 块/批写入 =
    /// 数百个 segment，每个 segment 都有自己的 term 索引。查询一个**不存在**的词时，
    /// 要在每个 segment 的 term B-tree 里各查一次——实测 881MB 库上
    /// 「zzzzzzqqq」239ms、「量子」223ms，而「纠缠」（有命中）只要 4ms。
    /// </para>
    /// <para>
    /// 这不是查询退化，是**索引碎片化**。合并成单个 segment 后 term 查找变成一次 B-tree 查找。
    /// 只在后台补齐收尾与运维端点调用，读路径绝不触发（optimize 会重写整个倒排表）。
    /// </para>
    /// </summary>
    public async Task<bool> OptimizeLexicalIndexAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var span = TimingScope.Begin("kb.lexical-index.optimize", SpanCategory.Io, "FTS5 段合并");
        var sw = Stopwatch.StartNew();
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO fts_chunks_lexical(fts_chunks_lexical) VALUES('optimize')";
            await cmd.ExecuteNonQueryAsync(ct);
            span.SetDetail($"段合并完成 {sw.ElapsedMilliseconds}ms");
            _logger?.LogInformation(
                "[SqliteKnowledgeBaseStore] 词法索引段合并完成: {KbId}，耗时 {Elapsed}ms",
                KnowledgeBaseId, sw.ElapsedMilliseconds);
            return true;
        }
        catch (SqliteException ex)
        {
            span.Fail(ex);
            _logger?.LogWarning(ex, "[SqliteKnowledgeBaseStore] 词法索引段合并失败: {KbId}", KnowledgeBaseId);
            return false;
        }
        finally { _writeGate.Release(); }
    }

    private static async Task<bool> HasLexicalIndexAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='fts_chunks_lexical'";
        var sql = (string?)await cmd.ExecuteScalarAsync(ct);
        if (sql == null) return false;
        // Clean-Break: 必须包含 title 与 heading 列，若为旧版 3 列结构则视为缺失（由后台补齐重建）
        if (!sql.Contains("title", StringComparison.OrdinalIgnoreCase) || !sql.Contains("heading", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static string ExtractTitle(string sourceKey, string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var lines = text.Split('\n', 5);
            foreach (var l in lines)
            {
                var trimmed = l.Trim();
                if (trimmed.StartsWith("# ") && trimmed.Length > 2)
                    return trimmed.Substring(2).Trim();
            }
        }
        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            try
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(sourceKey);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            return sourceKey;
        }
        return string.Empty;
    }

    private static string ExtractHeading(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Split('\n');
        foreach (var l in lines)
        {
            var trimmed = l.Trim();
            if ((trimmed.StartsWith("## ") || trimmed.StartsWith("### ")) && trimmed.Length > 3)
                return trimmed.TrimStart('#', ' ');
        }
        return string.Empty;
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

    /// <summary>按原句顺序抽取词元（含音近/同义词扩展），供查询规划器做 df 剪枝与相邻 NEAR。</summary>
    private static IReadOnlyList<string> ExtractLexicalTerms(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();
        if (query.Length > 4096) throw new ArgumentOutOfRangeException(nameof(query));
        var expansionTerms = ReferenceRAG.Core.Features.Search.PhoneticQueryExpander.GetExpansionTerms(query);
        return LexicalSegmenter.CutForSearch(query)
            .Concat(expansionTerms)
            .Select(t => t.Trim())
            .Where(t => t.Any(char.IsLetterOrDigit))
            .Take(64)
            .ToList();
    }

    private string StatsCacheKey(long revision) => $"{_dbPath}\u0001{revision}";

    private async Task<int> GetCorpusSizeAsync(SqliteConnection conn, SqliteTransaction tx, long readRevision, CancellationToken ct)
    {
        var key = StatsCacheKey(readRevision);
        if (CorpusSizeCache.TryGetValue(key, out var cached) && cached.Revision == readRevision)
        {
            return cached.Total;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM chunks";
        var total = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
        if (CorpusSizeCache.Count > 1024) CorpusSizeCache.Clear();
        CorpusSizeCache[key] = (readRevision, total);
        return total;
    }

    /// <summary>
    /// 读取词项的文档频率（df）。
    /// <para>
    /// <b>为什么不用 <c>fts5vocab</c>：</b>该虚表不支持按 term 索引查找，任何 <c>WHERE term IN (...)</c>
    /// 都是对「词 × 文档」全表的线性扫描。57k 块 / ~1.7M 条 (term,doc) 记录实测 1,060ms，
    /// 全部落在首查的请求线程上——修好 MATCH 之后它反而成了新的首查瓶颈。
    /// </para>
    /// <para>
    /// <b>现在改用带上限的 COUNT：</b><c>SELECT COUNT(*) FROM (SELECT 1 ... MATCH @t LIMIT @cap)</c>。
    /// 命中上限即停止扫描，因此单个词元的代价被 <c>O(min(df, cap))</c> 约束：稀有词读全部 postings（本来就少），
    /// 高频虚词读到阈值就停。上限取「常见词判定阈值 + 1」，所以上限值本身已足以完成"该剪/不该剪"的裁决；
    /// 且封顶值只会**高估** df，使候选预算更保守（偏向多剪），不会破坏正确性。
    /// </para>
    /// 不可用时返回空统计，规划器会自动退回"全词 OR"（行为与历史版本一致，不抛错）。
    /// </summary>
    private async Task<Dictionary<string, int>> LoadTermDocumentFrequenciesAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        IReadOnlyList<string> terms,
        long readRevision,
        int corpusSize,
        CancellationToken ct)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var keyPrefix = StatsCacheKey(readRevision);
        var missing = new List<string>();
        foreach (var term in terms)
        {
            if (TermDfCache.TryGetValue($"{keyPrefix}\u0001{term}", out var cachedDf))
            {
                result[term] = cachedDf;
            }
            else
            {
                missing.Add(term);
            }
        }
        if (missing.Count == 0) return result;

        // 上限 = 常见词阈值 + 1：达到该值即可判定"这是虚词/高频词"，继续精确统计没有意义。
        var cap = Math.Max(
            64,
            (int)Math.Ceiling(corpusSize * LexicalQueryPlannerOptions.Default.CommonTermCutoff) + 1);

        try
        {
            foreach (var term in missing.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using var q = conn.CreateCommand();
                q.Transaction = tx;
                q.CommandText =
                    "SELECT COUNT(*) FROM (SELECT 1 FROM fts_chunks_lexical " +
                    "WHERE fts_chunks_lexical MATCH @match LIMIT @cap)";
                q.Parameters.AddWithValue("@match", LexicalQueryPlanner.QuotePhrase(term));
                q.Parameters.AddWithValue("@cap", cap);
                var df = Convert.ToInt32(await q.ExecuteScalarAsync(ct) ?? 0);
                result[term] = df;
                if (TermDfCache.Count > TermDfCacheCapacity) TermDfCache.Clear();
                TermDfCache[$"{keyPrefix}\u0001{term}"] = df;
            }
        }
        catch (SqliteException ex)
        {
            _logger?.LogDebug(ex, "[SqliteKnowledgeBaseStore] 词频统计不可用，词法查询退回无统计模式");
        }
        return result;
    }

    /// <summary>
    /// 生成词法 MATCH 表达式（统计驱动）。这是 18s 首查的直接修复点：
    /// 旧实现对 ≤15 字查询直接拼 <c>"他" OR "的" OR "她" ...</c>，命中率 &gt;100% 语料，
    /// bm25 必须对全库打分，冷缓存下退化为 16.7s。
    /// </summary>
    private async Task<LexicalQueryPlan> BuildLexicalQueryPlanAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string query,
        long readRevision,
        CancellationToken ct)
    {
        using var span = TimingScope.Begin("kb.lexical.plan", SpanCategory.SubPhase, "词法查询规划");
        var terms = ExtractLexicalTerms(query);
        var corpusSize = await GetCorpusSizeAsync(conn, tx, readRevision, ct);
        var df = await LoadTermDocumentFrequenciesAsync(conn, tx, terms, readRevision, corpusSize, ct);
        var plan = LexicalQueryPlanner.Plan(query, terms, df, corpusSize);
        span.SetInput(terms.Count);
        span.SetOutput(plan.AnchorTerms.Count);
        span.SetDetail(plan.Describe());
        return plan;
    }

    private static async Task PopulateLexicalIndexAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken ct)
    {
        long lastRowId = 0;
        while (true)
        {
            var rows = new List<(long RowId, string ChunkId, string DocumentId, string SourceKey, string Text)>();
            using (var select = conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText = @"
                    SELECT c.rowid, c.id, c.document_id, d.source_key, c.text 
                    FROM chunks c 
                    JOIN documents d ON d.id = c.document_id
                    WHERE c.rowid > @last 
                    ORDER BY c.rowid LIMIT 500";
                select.Parameters.AddWithValue("@last", lastRowId);
                using var reader = await select.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
                }
            }
            if (rows.Count == 0) break;
            var tokenized = TokenizeBatch(rows);
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO fts_chunks_lexical(chunk_id, document_id, title, heading, text) VALUES(@chunk, @doc, @title, @heading, @text)";
            var chunk = insert.Parameters.Add("@chunk", SqliteType.Text);
            var document = insert.Parameters.Add("@doc", SqliteType.Text);
            var titleParam = insert.Parameters.Add("@title", SqliteType.Text);
            var headingParam = insert.Parameters.Add("@heading", SqliteType.Text);
            var text = insert.Parameters.Add("@text", SqliteType.Text);
            for (int i = 0; i < rows.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                chunk.Value = rows[i].ChunkId;
                document.Value = rows[i].DocumentId;
                titleParam.Value = tokenized[i].Title;
                headingParam.Value = tokenized[i].Heading;
                text.Value = tokenized[i].Text;
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
            await InvalidateShadowRowsAsync(conn, tx, chunkId: null, documentId: docId, connectorId: null, ct);
            // 无条件维护词法倒排索引：InitializeAsync 已保证表存在；
            // 历史上这里用 if (hasLexicalIndex) 条件跳过，导致"索引永远缺一段，直到首次查询现场构建 16 秒"。
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
                        text, token_count, embedding_text,
                        start_line, end_line, heading_path
                    ) VALUES (
                        @id, @doc_id, @ver, @ordinal,
                        @text, @tokens, @emb_text,
                        @start_line, @end_line, @heading_path
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
                insertChunkCmd.Parameters.AddWithValue("@start_line", chunk.StartLine);
                insertChunkCmd.Parameters.AddWithValue("@end_line", chunk.EndLine);
                insertChunkCmd.Parameters.AddWithValue("@heading_path", (object?)chunk.HeadingPath ?? DBNull.Value);
                await insertChunkCmd.ExecuteNonQueryAsync(ct);

                {
                    var title = ExtractTitle(batch.SourceKey, chunk.Text);
                    var heading = ExtractHeading(chunk.Text);
                    using var lexical = conn.CreateCommand();
                    lexical.Transaction = tx;
                    lexical.CommandText = "INSERT INTO fts_chunks_lexical(chunk_id, document_id, title, heading, text) VALUES(@id, @doc, @title, @heading, @text);";
                    lexical.Parameters.AddWithValue("@id", chunk.ChunkId);
                    lexical.Parameters.AddWithValue("@doc", docId);
                    lexical.Parameters.AddWithValue("@title", TokenizeLexical(title));
                    lexical.Parameters.AddWithValue("@heading", TokenizeLexical(heading));
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

            // 同步维护词法索引代次：本次写入已双写 fts_chunks_lexical，故词法索引与内容一致。
            using (var lexGen = conn.CreateCommand())
            {
                lexGen.Transaction = tx;
                lexGen.CommandText = @"UPDATE kb_manifest
                    SET lexical_generation = content_generation,
                        lexical_indexed_rowid = COALESCE((SELECT MAX(rowid) FROM chunks), 0),
                        tokenizer_version = @ver
                    WHERE kb_id = @id";
                lexGen.Parameters.AddWithValue("@ver", LexicalTokenizerVersion);
                lexGen.Parameters.AddWithValue("@id", KnowledgeBaseId);
                await lexGen.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();
            InMemoryTensorProjectionPool.GetOrCreate(KnowledgeBaseId).Invalidate();
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

        private async Task EnsureTensorProjectionPoolLoadedAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        long vectorGeneration,
        CancellationToken ct)
    {
        var pool = InMemoryTensorProjectionPool.GetOrCreate(KnowledgeBaseId);
        if (pool.CurrentGeneration == vectorGeneration && pool.IsLoaded)
        {
            return;
        }

        var items = new List<(string ChunkId, string DocumentId, string SourceKey, string Text, float[] Vector, long DocVersion, string ConnectorId, string? Locator, int StartLine, int EndLine, string? HeadingPath)>();
        int dimension = 0;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT v.chunk_id, v.document_id, d.source_key, c.text,
                   v.vector_blob, c.document_version, d.connector_id, d.locator, v.dimension,
                   c.start_line, c.end_line, c.heading_path
            FROM chunk_vectors v
            JOIN chunks c ON c.id = v.chunk_id
            JOIN documents d ON d.id = c.document_id
            WHERE c.enabled = 1";

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var chunkId = reader.GetString(0);
            var docId = reader.GetString(1);
            var sourceKey = reader.GetString(2);
            var text = reader.GetString(3);
            var blob = (byte[])reader.GetValue(4);
            var docVer = reader.GetInt64(5);
            var connId = reader.GetString(6);
            var locator = reader.IsDBNull(7) ? null : reader.GetString(7);
            var dim = reader.GetInt32(8);
            if (dimension == 0) dimension = dim;
            var startLine = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
            var endLine = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
            var headingPath = reader.IsDBNull(11) ? null : reader.GetString(11);

            var floatSpan = MemoryMarshal.Cast<byte, float>(blob.AsSpan());
            var vectorArray = floatSpan.ToArray();
            items.Add((chunkId, docId, sourceKey, text, vectorArray, docVer, connId, locator, startLine, endLine, headingPath));
        }

        pool.SwapSnapshot(vectorGeneration, dimension, items);
    }

    private static (string Clause, List<SqliteParameter> Parameters) BuildFolderFilter(IReadOnlyList<string>? folders)
    {
        if (folders == null || folders.Count == 0)
            return (string.Empty, new List<SqliteParameter>());

        var validFolders = folders
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (validFolders.Count == 0)
            return (" AND 1=0 ", new List<SqliteParameter>());

        var clauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        for (int i = 0; i < validFolders.Count; i++)
        {
            var folder = validFolders[i];
            var normSlash = folder.Replace('\\', '/').TrimEnd('/');
            var normBackslash = folder.Replace('/', '\\').TrimEnd('\\');

            var pName = $"@fld_{i}";
            var pPreSlash = $"@fld_ps_{i}";
            var pBackslash = $"@fld_bs_{i}";
            var pPreBackslash = $"@fld_pbs_{i}";

            clauses.Add($@"(
                d.source_key = {pName} OR d.source_key LIKE {pPreSlash}
                OR d.locator = {pName} OR d.locator LIKE {pPreSlash}
                OR d.locator = {pBackslash} OR d.locator LIKE {pPreBackslash}
                OR d.connector_id = {pName}
            )");

            parameters.Add(new SqliteParameter(pName, normSlash));
            parameters.Add(new SqliteParameter(pPreSlash, normSlash + "/%"));
            parameters.Add(new SqliteParameter(pBackslash, normBackslash));
            parameters.Add(new SqliteParameter(pPreBackslash, normBackslash + "\\%"));
        }

        return (" AND (" + string.Join(" OR ", clauses) + ") ", parameters);
    }

    /// <summary>
    /// 词法索引未就绪时的降级召回：对查询词项做 LIKE 扫描（毫秒级），保证补齐期间仍可用、可解释、可取消。
    /// </summary>
    private async Task<List<RecallItem>> ExecuteLikeFallbackAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        RecallRequest request,
        string scopeClause,
        string folderClause,
        IReadOnlyList<SqliteParameter> folderParams,
        HashSet<string> seenChunkIds,
        CancellationToken ct)
    {
        var results = new List<RecallItem>();
        if (string.IsNullOrWhiteSpace(request.Query)) return results;

        var tokens = LexicalSegmenter.CutForSearch(request.Query)
            .Select(t => t.Trim())
            .Where(t => t.Length >= 2 && t.Any(char.IsLetterOrDigit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
        if (tokens.Count == 0)
        {
            var raw = request.Query.Trim();
            if (raw.Length == 0) return results;
            tokens.Add(raw);
        }

        var likeClause = string.Join(" OR ", tokens.Select((_, i) => $"c.text LIKE @kw{i}"));
        var hitsExpr = string.Join(" + ", tokens.Select((_, i) => $"(c.text LIKE @kw{i})"));

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"
            SELECT c.id, c.document_id, d.source_key, c.text, c.document_version, d.connector_id, d.locator,
                   c.start_line, c.end_line, c.heading_path, ({hitsExpr}) AS hits
            FROM chunks c JOIN documents d ON d.id = c.document_id
            WHERE c.enabled = 1 AND (@connector IS NULL OR d.connector_id=@connector) {scopeClause} {folderClause}
              AND ({likeClause})
            ORDER BY hits DESC, c.rowid ASC
            LIMIT @lim";
        cmd.Parameters.AddWithValue("@connector", (object?)request.ConnectorId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lim", Math.Clamp(request.TopK * 3, 10, 200));
        for (int i = 0; i < tokens.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@kw{i}", $"%{tokens[i]}%");
        }
        foreach (var p in folderParams)
        {
            cmd.Parameters.AddWithValue(p.ParameterName, p.Value);
        }

        try
        {
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var chunkId = reader.GetString(0);
                if (!seenChunkIds.Add(chunkId)) continue;
                var hits = reader.IsDBNull(10) ? 1 : reader.GetInt32(10);
                results.Add(new RecallItem(
                    ChunkId: chunkId,
                    DocumentId: reader.GetString(1),
                    SourceKey: reader.GetString(2),
                    Text: reader.GetString(3),
                    Score: 0.30 + 0.10 * Math.Min(hits, 4),   // 归一化到 [0.4, 0.7]，避免压过真正的 BM25 召回
                    ScoreKind: ScoreKind.BM25,
                    DocumentVersion: reader.GetInt64(4),
                    ConnectorId: reader.GetString(5),
                    Locator: reader.IsDBNull(6) ? null : reader.GetString(6),
                    StartLine: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                    EndLine: reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    HeadingPath: reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }
        catch (SqliteException ex)
        {
            _logger?.LogDebug(ex, "[SqliteKnowledgeBaseStore] 词法降级扫描失败: {Query}", request.Query);
        }
        return results;
    }

    private async Task<List<RecallItem>> ExecuteLexicalQueryInternalAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string table,
        string ftsQuery,
        RecallRequest request,
        string scopeClause,
        string folderClause,
        IReadOnlyList<SqliteParameter> folderParams,
        HashSet<string> seenChunkIds,
        CancellationToken ct,
        LexicalQueryPlan? plan = null,
        bool isFloorTier = false)
    {
        using var span = TimingScope.Begin("kb.fts.match", SpanCategory.Io, "FTS5 匹配");
        span.AddAttribute("kb", KnowledgeBaseId);
        span.AddAttribute("table", table);
        var resultList = new List<RecallItem>();
        try
        {
            var scoreExpr = table == "fts_chunks_lexical"
                ? "bm25(fts_chunks_lexical, 0.0, 0.0, 5.0, 2.5, 1.0)"
                : $"bm25({table})";
            var topK = Math.Clamp(request.TopK, 1, 100);

            // ── 候选优先（two-phase）────────────────────────────────────────
            // 旧 SQL 是「FTS 全命中 → JOIN chunks(214MB) → 临时 B 树排序 → LIMIT 10」，
            // JOIN 与 bm25 都发生在 LIMIT 之前，候选集多大就要读多少正文页。
            // 现在先在 FTS 内部只取「前 N 个候选的 chunk_id + 分数」，再回填正文：
            // 冷缓存下被读的页数从 O(命中数) 降到 O(候选数)，这是 18s → 亚秒的关键一步。
            // 目录/授权过滤放在外层，若过滤后不足 topK，则指数放大候选窗口重试（保证结果正确性）。
            //
            // 候选窗口受查询计划的**绝对预算**约束（P0-1）：比例预算会随语料线性放大，
            // 绝对上限（5000）把最坏情况钉死。窗口上限同时受 2000 的 IO 约束。
            var budget = plan is { CandidateBudget: > 0 } ? plan.CandidateBudget : 2000;
            // 下限 80 是两阶段召回的最小窗口：若把窗口压到 topK 附近，
            // 一旦外层 connector / 目录授权过滤掉几个候选，就再也补不回来（窗口重试的上限也被压死）。
            var maxCandidates = Math.Max(Math.Max(topK, 80), Math.Min(2000, budget));
            var candidateWindow = Math.Min(maxCandidates, Math.Clamp(topK * 8, 80, 2000));

            // 退化查询（估算命中 > 预算）：bm25 排序必须对全部命中行打分，LIMIT 完全无效。
            // 有界预取 SQL（rowid IN 子查询）实测反而慢 110 倍（FTS5 虚表不支持下推），
            // 所以这里换成「流式取 chunk_id → 批量回表 → 覆盖率重排」，见 CollectByBoundedPrefetchAsync。
            var boundedPrefetch = plan is { BudgetExceeded: true } && table == "fts_chunks_lexical";
            span.AddAttribute("bounded", boundedPrefetch ? "prefetch" : "bm25");

            var collected = new List<RecallItem>(topK);
            var attempts = 0;
            var rawRows = 0;

            for (int attempt = 0; ; attempt++)
            {
                attempts = attempt + 1;
                collected.Clear();
                var localSeen = new HashSet<string>(StringComparer.Ordinal);

                if (boundedPrefetch)
                {
                    collected.AddRange(await CollectByBoundedPrefetchAsync(
                        conn, tx, table, ftsQuery, request, scopeClause, folderClause,
                        folderParams, seenChunkIds, localSeen, candidateWindow, ct));
                }
                else
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = BuildLexicalRecallSql(scoreExpr, table, scopeClause, folderClause);
                    cmd.Parameters.AddWithValue("@match", ftsQuery);
                    cmd.Parameters.AddWithValue("@connector", (object?)request.ConnectorId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@topK", topK);
                    cmd.Parameters.AddWithValue("@candidates", candidateWindow);
                    foreach (var p in folderParams)
                    {
                        cmd.Parameters.AddWithValue(p.ParameterName, p.Value);
                    }

                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    span.SetDetail(ftsQuery);
                    while (await reader.ReadAsync(ct))
                    {
                        rawRows++;
                        var chunkId = reader.GetString(0);
                        // 跨查询去重（seenChunkIds）在窗口重试全部成功后才提交，避免半成品污染后续查询
                        if (seenChunkIds.Contains(chunkId) || !localSeen.Add(chunkId)) continue;

                        var bm25Raw = reader.GetDouble(10);
                        var score = request.UseLexicalTokenizer ? Math.Abs(bm25Raw) : 1.0 / (1.0 + Math.Max(0, bm25Raw));

                        collected.Add(ReadRecallItem(reader, score, ScoreKind.BM25));
                    }
                }

                if (collected.Count >= topK || candidateWindow >= maxCandidates || attempt >= 2) break;
                candidateWindow = Math.Min(maxCandidates, candidateWindow * 4);
            }

            // ── 覆盖率重排（P0-3）──────────────────────────────────────────
            // 为什么需要：bm25 对"查询词命中数"完全不敏感。命中 9/10 个词的长文档
            // 可能因为长度归一化而排在一个只命中 2/10 个词的短文档之后。
            // 只在词数足够（锚点 >= 3）时启用——短查询的覆盖率没有区分度，只会打乱 bm25 顺序。
            ApplyCoverageRerank(collected, plan);

            // 地板层结果必须能被上层识别：它与精确命中在界面上长得一模一样，
            // 不标记就等于把"宽松兜底"伪装成"精确匹配"（上一轮遗留的 P1）。
            if (isFloorTier && collected.Count > 0)
            {
                for (int i = 0; i < collected.Count; i++)
                {
                    collected[i] = collected[i] with { IsFloor = true };
                }
            }

            foreach (var item in collected) seenChunkIds.Add(item.ChunkId);
            resultList.AddRange(collected);
            span.AddAttribute("candidates", candidateWindow.ToString());
            span.AddAttribute("attempts", attempts.ToString());
            span.AddAttribute("raw", rawRows.ToString());
            if (isFloorTier) span.AddAttribute("floor", "1");
        }
        catch (SqliteException ex)
        {
            _logger?.LogDebug(ex, "[SqliteKnowledgeBaseStore] FTS 查询回退: {Query}", ftsQuery);
        }
        span.SetOutput(resultList.Count);
        return resultList;
    }

    /// <summary>
    /// 构造词法召回的两阶段 SQL（FTS 先取候选 → 再回表取正文）。
    /// <para>
    /// <b>列序契约</b>：0~9 是公共列，第 10 列才是 score。两条召回路径（bm25 / 有界预取）必须
    /// 保持一致，否则 <c>ConnectorId</c>、<c>StartLine</c> 会静默错位——授权过滤读到
    /// <c>document_version</c>，行号读到 <c>locator</c>。
    /// </para>
    /// <para>
    /// <b>⚠️ f 与 chunks 之间必须是 CROSS JOIN（顺序约束），不能写普通 JOIN。</b>
    /// SQLite 3.53 的规划器会把 chunks 选成外层驱动表（<c>SEARCH c USING INDEX
    /// idx_chunks_enabled</c>），于是每条语句都要把整个 chunks 表扫一遍再逐行回探 FTS 协程：
    /// 5.7 万块语料实测 0 命中的查询也要 <b>105ms</b>，且与 LIMIT 无关（LIMIT 80/320/1280 都一样）。
    /// 同一条 SQL 在 SQLite 3.40 上只要 0.03ms，因为它选了 <c>MATERIALIZE f → SCAN f → SEARCH c</c>。
    /// 这是规划器版本差异造成的**静默性能反转**：两阶段召回的收益被外层全表扫描吃掉，
    /// 而且不看执行计划根本发现不了（结果完全正确，只是慢）。
    /// CROSS JOIN 把 f 钉在外层，才让「先取候选、再回表」真正生效。
    /// </para>
    /// </summary>
    internal static string BuildLexicalRecallSql(
        string scoreExpr, string table, string scopeClause, string folderClause) => $@"
                        SELECT c.id, c.document_id, d.source_key, c.text,
                               c.document_version, d.connector_id, d.locator,
                               c.start_line, c.end_line, c.heading_path,
                               f.score
                        FROM (
                            SELECT chunk_id, {scoreExpr} AS score
                            FROM {table}
                            WHERE {table} MATCH @match
                            ORDER BY score ASC
                            LIMIT @candidates
                        ) f
                        CROSS JOIN chunks c ON c.id = f.chunk_id
                        JOIN documents d ON d.id = c.document_id
                        WHERE c.enabled = 1 AND (@connector IS NULL OR d.connector_id=@connector) {scopeClause} {folderClause}
                        ORDER BY f.score ASC
                        LIMIT @topK;
                    ";

    /// <summary>
    /// 退化查询的有界预取：FTS5 的 <c>bm25()</c> 必须对**全部命中行**打分再排序，
    /// <c>LIMIT</c> 只减少回表、不减少打分（200k 语料实测 LIMIT 10 = 369ms，LIMIT 100000 = 413ms）。
    /// 而「<c>rowid IN (SELECT ... LIMIT n)</c>」这种写法无法下推到 FTS5 虚表，
    /// 实测反而从 9.53ms 恶化到 1054.91ms（110 倍）。
    /// <para>
    /// 因此退化查询唯一可行的降本方式是**放弃 bm25 排序**：
    /// 流式取前 N 个 chunk_id（不排序，2000 个 1.02ms）→ <c>json_each</c> 批量回表 → 覆盖率重排。
    /// 代价是顺序不再由 bm25 决定；但对"锚点全是虚词/命中超预算"的退化查询，
    /// bm25 顺序本来就没有区分度，覆盖率反而是更好的排序依据。
    /// </para>
    /// </summary>
    private async Task<List<RecallItem>> CollectByBoundedPrefetchAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string table,
        string ftsQuery,
        RecallRequest request,
        string scopeClause,
        string folderClause,
        IReadOnlyList<SqliteParameter> folderParams,
        HashSet<string> seenChunkIds,
        HashSet<string> localSeen,
        int window,
        CancellationToken ct)
    {
        var items = new List<RecallItem>(Math.Min(window, 256));
        var ids = new List<string>(Math.Min(window, 256));

        using (var probe = conn.CreateCommand())
        {
            probe.Transaction = tx;
            probe.CommandText = $"SELECT chunk_id FROM {table} WHERE {table} MATCH @match LIMIT @lim";
            probe.Parameters.AddWithValue("@match", ftsQuery);
            probe.Parameters.AddWithValue("@lim", window);
            using var reader = await probe.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0)) continue;
                var id = reader.GetString(0);
                if (seenChunkIds.Contains(id) || !localSeen.Add(id)) continue;
                ids.Add(id);
            }
        }

        if (ids.Count == 0) return items;

        // ⚠️ 必须用参数化 IN，不能用 `id IN (SELECT value FROM json_each(@ids))`：
        // 后者 SQLite 无法把它下推成 chunks.id 的主键查找，退化为全表扫描 57k 行，
        // 实测 80 个候选回表要 134ms（比 bm25 全量打分还慢）。
        var idNames = new string[ids.Count];
        for (int i = 0; i < ids.Count; i++) idNames[i] = $"@id{i}";

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"
            SELECT c.id, c.document_id, d.source_key, c.text,
                   c.document_version, d.connector_id, d.locator,
                   c.start_line, c.end_line, c.heading_path
            FROM chunks c
            JOIN documents d ON d.id = c.document_id
            WHERE c.enabled = 1
              AND c.id IN ({string.Join(",", idNames)})
              AND (@connector IS NULL OR d.connector_id=@connector) {scopeClause} {folderClause}";
        for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue(idNames[i], ids[i]);
        cmd.Parameters.AddWithValue("@connector", (object?)request.ConnectorId ?? DBNull.Value);
        foreach (var p in folderParams)
        {
            cmd.Parameters.AddWithValue(p.ParameterName, p.Value);
        }

        using var rows = await cmd.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            // 预取路径没有 bm25 分数：Score 先置 0，随后由覆盖率重排覆盖为 coverage。
            items.Add(ReadRecallItem(rows, 0.0, ScoreKind.BM25));
        }
        return items;
    }

    /// <summary>
    /// 从「chunk JOIN documents」结果行构造 RecallItem（两条召回路径共用）。
    /// <para>
    /// <b>列序契约</b>：0=chunk_id, 1=document_id, 2=source_key, 3=text, 4=document_version,
    /// 5=connector_id, 6=locator, 7=start_line, 8=end_line, 9=heading_path。
    /// bm25 路径把 score 放在第 10 列；有界预取路径没有 score，由覆盖率补齐。
    /// 任何一条 SQL 改列序都必须同步这里，否则 ConnectorId / StartLine 会静默错位。
    /// </para>
    /// </summary>
    private static RecallItem ReadRecallItem(SqliteDataReader reader, double score, ScoreKind kind)
        => new(
            ChunkId: reader.GetString(0),
            DocumentId: reader.GetString(1),
            SourceKey: reader.GetString(2),
            Text: reader.GetString(3),
            Score: score,
            ScoreKind: kind,
            DocumentVersion: reader.GetInt64(4),
            ConnectorId: reader.GetString(5),
            Locator: reader.IsDBNull(6) ? null : reader.GetString(6),
            StartLine: reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
            EndLine: reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            HeadingPath: reader.IsDBNull(9) ? null : reader.GetString(9));

    /// <summary>
    /// 覆盖率重排：<c>(coverage desc, score desc)</c>。
    /// coverage = 命中锚点数 / 锚点总数。锚点数 &lt; 3 时不计算（没有区分度）。
    /// </summary>
    private void ApplyCoverageRerank(List<RecallItem> items, LexicalQueryPlan? plan)
    {
        if (items.Count == 0 || plan == null) return;
        var basis = plan.CoverageBasis;
        if (basis.Count < 3) return;

        var distinct = basis.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < items.Count; i++)
        {
            var text = items[i].Text;
            int hits = 0;
            foreach (var term in distinct)
            {
                if (text.Contains(term, StringComparison.OrdinalIgnoreCase)) hits++;
            }
            var coverage = (double)hits / distinct.Count;
            // 预取路径的 Score 是占位 0，用覆盖率补齐，保证融合/排序有可用的信号。
            var score = items[i].Score > 0 ? items[i].Score : coverage;
            items[i] = items[i] with { Coverage = coverage, Score = score };
        }

        items.Sort((a, b) =>
        {
            var byCoverage = (b.Coverage ?? 0).CompareTo(a.Coverage ?? 0);
            return byCoverage != 0 ? byCoverage : b.Score.CompareTo(a.Score);
        });
    }

    public async Task<RecallSnapshot> RecallAsync(RecallRequest request, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        // ⚠️ 读路径禁止构建索引：这里只做 O(1) 状态裁决。
        // 历史实现调用 EnsureLexicalIndexAsync()，导致首次词法查询在请求线程内全量分词建索引（实测 16 秒）。
        var lexicalStatus = await GetLexicalIndexStatusAsync(ct);
        bool lexicalReady = lexicalStatus.Status is LexicalIndexStatus.Ready or LexicalIndexStatus.Empty;
        if (!lexicalReady)
        {
            // 只投递补齐意图，交给后台 Worker；请求线程绝不构建索引。
            LexicalIndexBackfillSignal.Request(KnowledgeBaseId);
        }

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

        var effectiveFolders = request.Folders;
        if ((effectiveFolders == null || effectiveFolders.Count == 0) && request.MetadataFilters != null)
        {
            if (request.MetadataFilters.TryGetValue("folders", out var flds) && !string.IsNullOrWhiteSpace(flds))
                effectiveFolders = flds.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            else if (request.MetadataFilters.TryGetValue("folder", out var fld) && !string.IsNullOrWhiteSpace(fld))
                effectiveFolders = [fld];
            else if (request.MetadataFilters.TryGetValue("path", out var pth) && !string.IsNullOrWhiteSpace(pth))
                effectiveFolders = [pth];
        }

        var (folderClause, folderParams) = BuildFolderFilter(effectiveFolders);

        var scopeClause = "";
        Func<string, string, string?, bool>? effectiveSimdAuth = null;
        if (effectiveFolders != null && effectiveFolders.Count > 0)
        {
            var validFolders = effectiveFolders.Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Replace('\\', '/').TrimEnd('/')).ToList();
            if (validFolders.Count == 0)
            {
                effectiveSimdAuth = (_, _, _) => false;
            }
            else
            {
                effectiveSimdAuth = (connId, srcKey, loc) =>
                {
                    var sk = srcKey?.Replace('\\', '/');
                    var lp = loc?.Replace('\\', '/');
                    foreach (var fld in validFolders)
                    {
                        if (string.Equals(connId, fld, StringComparison.OrdinalIgnoreCase)) return true;
                        if (!string.IsNullOrEmpty(sk) && (string.Equals(sk, fld, StringComparison.OrdinalIgnoreCase) || sk.StartsWith(fld + "/", StringComparison.OrdinalIgnoreCase))) return true;
                        if (!string.IsNullOrEmpty(lp) && (string.Equals(lp, fld, StringComparison.OrdinalIgnoreCase) || lp.StartsWith(fld + "/", StringComparison.OrdinalIgnoreCase))) return true;
                    }
                    return false;
                };
            }
        }

        if (request.AuthorizeDocument != null)
        {
            var baseAuth = request.AuthorizeDocument;
            var prevFolderAuth = effectiveSimdAuth;
            effectiveSimdAuth = prevFolderAuth == null
                ? baseAuth
                : (c, s, l) => baseAuth(c, s, l) && prevFolderAuth(c, s, l);

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
        bool isDegraded = !lexicalReady;
        string? degradeReason = lexicalReady
            ? null
            : $"词法索引补齐中 ({lexicalStatus.ProgressPercent}%)，已降级为关键词扫描召回";
        if (request.QueryVector is { Length: > 0 })
        {
            using var coverage = conn.CreateCommand();
            coverage.Transaction = tx;
            coverage.CommandText = @"SELECT 1 FROM chunks c
                JOIN documents d ON d.id=c.document_id LEFT JOIN chunk_vectors v ON v.chunk_id=c.id
                WHERE c.enabled = 1 AND v.chunk_id IS NULL AND (@connector IS NULL OR d.connector_id=@connector)" + scopeClause + folderClause + " LIMIT 1";
            coverage.Parameters.AddWithValue("@connector", (object?)request.ConnectorId ?? DBNull.Value);
            foreach (var p in folderParams)
            {
                coverage.Parameters.AddWithValue(p.ParameterName, p.Value);
            }
            if (await coverage.ExecuteScalarAsync(ct) != null)
            {
                if (!request.AllowDegrade)
                {
                    throw new InvalidOperationException("知识库存在未向量化分块，禁止向量检索。");
                }
                hasVectorCoverage = false;
                isDegraded = true;
                degradeReason = "知识库向量索引未完全覆盖，自动降级为全量纯词法检索";
                _logger?.LogInformation("[SqliteKnowledgeBaseStore] 知识库 {KbId} 向量未完全覆盖，自动平滑降级为纯词法检索", KnowledgeBaseId);
            }
        }

        // 1. BM25 全文召回 (统计驱动的词法规划 + BM25F 多列打分)
        var isLongQuery = request.Query != null && request.Query.Length > 15;
        var useLexicalTable = request.UseLexicalTokenizer || isDegraded || !hasVectorCoverage || request.QueryVector is not { Length: > 0 };
        var table = useLexicalTable ? "fts_chunks_lexical" : "fts_chunks";

        // 长句仍复用 LongQueryRefiner 的蒸馏词与 PRF 能力，但**表达式不再直接取它的 Tier1/2/3 字符串**：
        // 那三层里包含「的/她/他/手」这类 df 高达 70%~180% 的虚词，OR 之后 MATCH 命中全库，
        // FTS5 必须对全部命中行算 bm25 再排序（LIMIT 失效），冷缓存下单条查询 16.7s。
        var longQueryPlan = isLongQuery && useLexicalTable
            ? ReferenceRAG.Core.Features.Search.Lexical.LongQueryRefiner.Default.PlanQuery(request.Query)
            : null;

        var queriesToTry = new List<string>();
        LexicalQueryPlan? lexicalQueryPlan = null;
        if (useLexicalTable)
        {
            lexicalQueryPlan = await BuildLexicalQueryPlanAsync(conn, tx, request.Query ?? string.Empty, readRevision, ct);
            queriesToTry.AddRange(lexicalQueryPlan.Queries);
        }
        else
        {
            var fallback = SanitizeFtsQuery(request.Query);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                // SAG-Wiki 专有名词短语与双引号锁定增强（非词法表路径保持原行为）
                var enhanced = NamePhraseExtractor.BuildEnhancedFtsQuery(request.Query, fallback);
                if (!string.Equals(enhanced, fallback, StringComparison.Ordinal))
                {
                    queriesToTry.Add(enhanced);
                }
                queriesToTry.Add(fallback);
            }
        }

        var seenChunkIds = new HashSet<string>(StringComparer.Ordinal);
        var usedRecallFloorHit = false;
        if (useLexicalTable && !lexicalReady)
        {
            // 降级通道：词法倒排索引尚未补齐时，用关键词 LIKE 扫描兜底（毫秒级，绝不阻塞在分词建索引上）
            using var fb = TimingScope.Begin("kb.lexical.like-fallback", SpanCategory.Io, "关键词扫描兜底");
            var fallbackItems = await ExecuteLikeFallbackAsync(conn, tx, request, scopeClause, folderClause, folderParams, seenChunkIds, ct);
            bm25Items.AddRange(fallbackItems);
            fb.SetOutput(fallbackItems.Count);
        }
        else
        {
            for (int qi = 0; qi < queriesToTry.Count; qi++)
            {
                if (bm25Items.Count >= request.TopK) break;
                // 地板层永远是阶梯的最后一级：前面所有层都不足 topK 时才会执行到这里。
                var isFloorTier = lexicalQueryPlan is { UsedRecallFloor: true } && qi == queriesToTry.Count - 1;
                var retrieved = await ExecuteLexicalQueryInternalAsync(
                    conn, tx, table, queriesToTry[qi], request, scopeClause, folderClause,
                    folderParams, seenChunkIds, ct, lexicalQueryPlan, isFloorTier);
                if (isFloorTier && retrieved.Count > 0) usedRecallFloorHit = true;
                bm25Items.AddRange(retrieved);
            }
        }

        // 1.1 级联早停状态机裁决 (Cascade Early-Pruning State Machine)
        if (request.Query != null && bm25Items.Count > 0)
        {
            var (shouldEarlyExit, earlyExitReason) = CascadeEarlyPruningStateMachine.CheckEarlyExit(request.Query, bm25Items);
            if (shouldEarlyExit)
            {
                _logger?.LogInformation("[SqliteKnowledgeBaseStore] 触发词法级联早停: {Reason}，短路跳过向量检索与融合", earlyExitReason);
                return new RecallSnapshot(
                    KnowledgeBaseId, readRevision, contentGeneration, vectorGeneration,
                    bm25Items.Take(request.TopK).ToList(),
                    Degraded: isDegraded, DegradationReason: degradeReason, EarlyPruned: true,
                    UsedRecallFloor: usedRecallFloorHit);
            }
        }

        // 1.2 上下文共现多跳扩展与公平交错 (Context-IDF Expansion & Interleave)
        if (request.Query != null && bm25Items.Count > 0 && bm25Items.Count < request.TopK)
        {
            var expansions = ContextIdfTermSelector.SelectExpansionTerms(request.Query, bm25Items, limit: 2);
            if (expansions.Count > 0)
            {
                var hopLists = new List<IReadOnlyList<RecallItem>> { bm25Items };
                foreach (var exp in expansions)
                {
                    var hopQuery = exp.IsNamePhrase ? $"\"{exp.Term.Replace("\"", "\"\"")}\"" : TokenizeLexical(exp.Term);
                    if (string.IsNullOrWhiteSpace(hopQuery)) continue;
                    var hopRetrieved = await ExecuteLexicalQueryInternalAsync(conn, tx, table, hopQuery, request, scopeClause, folderClause, folderParams, seenChunkIds, ct);
                    if (hopRetrieved.Count > 0)
                    {
                        hopLists.Add(hopRetrieved);
                    }
                }
                bm25Items = InterleaveAlgorithm.Interleave(hopLists, request.TopK * 2).ToList();
            }
        }

        // PRF 统计伪相关反馈重排
        if (longQueryPlan != null && bm25Items.Count > 0 && request.Query != null && request.Query.Length > 20)
        {
            var prfTerms = ReferenceRAG.Core.Features.Search.Lexical.StatisticalPrfOperator.MineExpansionTerms(
                bm25Items.Select(i => i.Text), longQueryPlan.DistilledTerms, 3);
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

        // 2. 向量相似度召回 (优先尝试连续内存硬件 SIMD 投影池加速)
        if (hasVectorCoverage && request.QueryVector != null && request.QueryVector.Length > 0)
        {
            try
            {
                await EnsureTensorProjectionPoolLoadedAsync(conn, tx, vectorGeneration, ct);
                var pool = InMemoryTensorProjectionPool.GetOrCreate(KnowledgeBaseId);
                if (pool.TryScanSimd(request.QueryVector.AsSpan(), request.ConnectorId, authorizeDocument: effectiveSimdAuth, request.TopK, out var simdItems) && simdItems.Count > 0)
                {
                    vectorItems = simdItems;
                }
                else
                {
                    using var vecCmd = conn.CreateCommand();
                    vecCmd.Transaction = tx;
                    vecCmd.CommandText = @"
                        SELECT v.chunk_id, v.document_id, d.source_key, c.text,
                               v.vector_blob, c.document_version, d.connector_id, d.locator,
                               c.start_line, c.end_line, c.heading_path
                        FROM chunk_vectors v
                        JOIN chunks c ON c.id = v.chunk_id
                        JOIN documents d ON d.id = c.document_id
                        WHERE c.enabled = 1 AND (@connector IS NULL OR d.connector_id=@connector)" + scopeClause + folderClause;
                    vecCmd.Parameters.AddWithValue("@connector", (object?)request.ConnectorId ?? DBNull.Value);
                    foreach (var p in folderParams)
                    {
                        vecCmd.Parameters.AddWithValue(p.ParameterName, p.Value);
                    }
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
                        var startLine = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
                        var endLine = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
                        var headingPath = reader.IsDBNull(10) ? null : reader.GetString(10);

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
                                Locator: reader.IsDBNull(7) ? null : reader.GetString(7),
                                StartLine: startLine,
                                EndLine: endLine,
                                HeadingPath: headingPath
                            ));
                        }
                    }

                    vectorItems = vectorItems.OrderByDescending(v => v.Score).Take(request.TopK).ToList();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "[SqliteKnowledgeBaseStore] 向量相似度查询异常");
                throw;
            }
        }

        // 3. 多路融合裁决 (自适应动态意图权重与 Z-Score 正态标准化融合)
        IReadOnlyList<RecallItem> finalItems;
        if (vectorItems.Count > 0 && bm25Items.Count > 0)
        {
            finalItems = AdaptiveQueryAwareFusion.FuseWithZScore(bm25Items, vectorItems, request.Query, request.TopK);
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
            Degraded: isDegraded, DegradationReason: degradeReason,
            UsedRecallFloor: usedRecallFloorHit);

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

            await InvalidateShadowRowsAsync(conn, tx, chunkId: null, documentId: docId, connectorId: null, ct);

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
                   cohort_set_revision, active_vector_count, updated_at, previous_spec_hash
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
                UpdatedAt: DateTime.Parse(reader.GetString(9)),
                PreviousEmbeddingSpecHash: reader.IsDBNull(10) ? null : reader.GetString(10)
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
                        lexical_generation = content_generation,
                        lexical_indexed_rowid = COALESCE((SELECT MAX(rowid) FROM chunks), 0),
                        tokenizer_version = @ver,
                        updated_at = @now
                    WHERE kb_id = @kb_id;
                    
                    SELECT read_revision FROM kb_manifest WHERE kb_id = @kb_id;
                ";
                populateCmd.Parameters.AddWithValue("@ver", LexicalTokenizerVersion);
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

            await InvalidateShadowRowsAsync(conn, tx, chunkId: null, documentId: documentId, connectorId: null, ct);

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

    public async Task<int> PurgeConnectorDataAsync(string connectorId, CancellationToken ct = default)
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
                using var cleanLex = conn.CreateCommand();
                cleanLex.Transaction = tx;
                cleanLex.CommandText = "DELETE FROM fts_chunks_lexical WHERE document_id IN (SELECT id FROM documents WHERE connector_id = @c_id);";
                cleanLex.Parameters.AddWithValue("@c_id", connectorId);
                await cleanLex.ExecuteNonQueryAsync(ct);
            }

            using var cleanCmd = conn.CreateCommand();
            cleanCmd.Transaction = tx;
            cleanCmd.CommandText = @"
                DELETE FROM fts_chunks WHERE document_id IN (SELECT id FROM documents WHERE connector_id = @c_id);
                DELETE FROM chunk_vectors WHERE document_id IN (SELECT id FROM documents WHERE connector_id = @c_id);
                DELETE FROM chunks WHERE document_id IN (SELECT id FROM documents WHERE connector_id = @c_id);
                DELETE FROM documents WHERE connector_id = @c_id;
            ";
            cleanCmd.Parameters.AddWithValue("@c_id", connectorId);
            await InvalidateShadowRowsAsync(conn, tx, chunkId: null, documentId: null, connectorId: connectorId, ct);
            var affected = await cleanCmd.ExecuteNonQueryAsync(ct);

            // 递增知识库版本代次与写入代次栅栏
            using var bumpCmd = conn.CreateCommand();
            bumpCmd.Transaction = tx;
            bumpCmd.CommandText = @"
                UPDATE kb_manifest
                SET read_revision = read_revision + 1,
                    index_epoch = index_epoch + 1,
                    content_generation = content_generation + 1,
                    vector_generation = vector_generation + 1,
                    updated_at = @now
                WHERE kb_id = @id;
            ";
            bumpCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            bumpCmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
            await bumpCmd.ExecuteNonQueryAsync(ct);

            tx.Commit();
            return affected;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<(int DocumentCount, int ChunkCount)> GetConnectorStatsAsync(string connectorId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                (SELECT COUNT(1) FROM documents WHERE connector_id = @c_id) AS doc_count,
                (SELECT COUNT(1) FROM chunks WHERE document_id IN (SELECT id FROM documents WHERE connector_id = @c_id)) AS chunk_count;
        ";
        cmd.Parameters.AddWithValue("@c_id", connectorId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var docCount = reader.GetInt32(0);
            var chunkCount = reader.GetInt32(1);
            return (docCount, chunkCount);
        }
        return (0, 0);
    }

    /// <summary>
    /// 获取当前知识库所有连接器与其下文档的路径树信息
    /// </summary>
    public async Task<IReadOnlyList<SourcePathInfo>> GetPathsAsync(
        IReadOnlyList<ConnectorDefinition> connectors,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var result = new List<SourcePathInfo>();
        using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var processedConnIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var connector in connectors.Where(c => c.Enabled))
        {
            processedConnIds.Add(connector.Id);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT source_key, locator FROM documents WHERE connector_id = @connId";
            cmd.Parameters.AddWithValue("@connId", connector.Id);

            var foldersSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var sourceKey = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (!string.IsNullOrWhiteSpace(sourceKey))
                {
                    var parts = sourceKey.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var current = "";
                    for (int p = 0; p < parts.Length - 1; p++)
                    {
                        current = string.IsNullOrEmpty(current) ? parts[p] : current + "/" + parts[p];
                        foldersSet.Add(current);
                    }
                }
            }

            var displayName = Path.GetFileName(connector.TargetPath.TrimEnd('/', '\\'));
            if (string.IsNullOrWhiteSpace(displayName)) displayName = connector.TargetPath;

            result.Add(new SourcePathInfo
            {
                Name = displayName,
                RootPath = connector.TargetPath,
                Folders = foldersSet.OrderBy(x => x).ToList()
            });
        }

        // 补充 documents 表中存在但未在 connectors 清单中的连接器数据（若有历史遗留）
        using (var extraCmd = conn.CreateCommand())
        {
            extraCmd.CommandText = "SELECT DISTINCT connector_id FROM documents";
            using var extraReader = await extraCmd.ExecuteReaderAsync(ct);
            var otherConnIds = new List<string>();
            while (await extraReader.ReadAsync(ct))
            {
                var cId = extraReader.GetString(0);
                if (!processedConnIds.Contains(cId))
                {
                    otherConnIds.Add(cId);
                }
            }

            foreach (var otherId in otherConnIds)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT source_key, locator FROM documents WHERE connector_id = @connId";
                cmd.Parameters.AddWithValue("@connId", otherId);
                var foldersSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string? sampleLocator = null;
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var sourceKey = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    if (sampleLocator == null && !reader.IsDBNull(1)) sampleLocator = reader.GetString(1);
                    if (!string.IsNullOrWhiteSpace(sourceKey))
                    {
                        var parts = sourceKey.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                        var current = "";
                        for (int p = 0; p < parts.Length - 1; p++)
                        {
                            current = string.IsNullOrEmpty(current) ? parts[p] : current + "/" + parts[p];
                            foldersSet.Add(current);
                        }
                    }
                }

                result.Add(new SourcePathInfo
                {
                    Name = otherId,
                    RootPath = sampleLocator != null ? (Path.GetDirectoryName(sampleLocator) ?? otherId) : otherId,
                    Folders = foldersSet.OrderBy(x => x).ToList()
                });
            }
        }

        return result;
    }

    public async Task<KnowledgeBaseDocumentRecord> CreateDocumentAsync(
        string connectorId,
        string sourceKey,
        string? title,
        string content,
        ChunkingSpec? chunkingSpec = null,
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
            var docId = Guid.NewGuid().ToString("N");
            var docKey = string.IsNullOrWhiteSpace(sourceKey) ? (string.IsNullOrWhiteSpace(title) ? $"doc_{docId[..8]}.md" : title) : sourceKey;

            // 1. 插入文档记录
            using (var insertDoc = conn.CreateCommand())
            {
                insertDoc.Transaction = tx;
                insertDoc.CommandText = @"
                    INSERT INTO documents (
                        id, connector_id, source_key, locator, content_hash,
                        document_version, created_at, updated_at
                    ) VALUES (
                        @id, @c_id, @s_key, @locator, @hash,
                        1, @now, @now
                    );
                ";
                insertDoc.Parameters.AddWithValue("@id", docId);
                insertDoc.Parameters.AddWithValue("@c_id", connectorId);
                insertDoc.Parameters.AddWithValue("@s_key", docKey);
                insertDoc.Parameters.AddWithValue("@locator", $"manual://{connectorId}/{docId}");
                insertDoc.Parameters.AddWithValue("@hash", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));
                insertDoc.Parameters.AddWithValue("@now", now);
                await insertDoc.ExecuteNonQueryAsync(ct);
            }

            // 2. 自适应分块解析与分块生成，提取行号与标题路径
            var rawLines = content.Replace("\r\n", "\n").Split('\n');
            string? currentHeading = null;
            var chunkList = new List<(string Text, int StartLine, int EndLine, string? HeadingPath)>();

            var currentChunkBuilder = new System.Text.StringBuilder();
            int chunkStartLine = 1;

            // 根据知识库 ChunkingSpec 自适应参数进行切分
            int maxChars = chunkingSpec != null ? Math.Max(128, chunkingSpec.MaxTokens * 2) : 600;
            int minChars = chunkingSpec != null ? Math.Min(30, chunkingSpec.MinTokens) : 0;
            bool preserveCode = chunkingSpec?.PreserveCodeBlocks ?? true;
            bool insideCodeFence = false;

            for (int lineIdx = 0; lineIdx < rawLines.Length; lineIdx++)
            {
                var line = rawLines[lineIdx];
                int lineNum = lineIdx + 1;

                if (preserveCode && line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    insideCodeFence = !insideCodeFence;
                }

                if (!insideCodeFence && line.TrimStart().StartsWith('#'))
                {
                    currentHeading = line.TrimStart('#', ' ').Trim();
                }

                if (currentChunkBuilder.Length == 0)
                {
                    chunkStartLine = lineNum;
                }

                currentChunkBuilder.AppendLine(line);

                bool isSplitBoundary = !insideCodeFence && (
                    (string.IsNullOrWhiteSpace(line) && currentChunkBuilder.Length >= minChars) ||
                    currentChunkBuilder.Length >= maxChars
                );

                if (isSplitBoundary)
                {
                    var chunkText = currentChunkBuilder.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(chunkText))
                    {
                        chunkList.Add((chunkText, chunkStartLine, lineNum, currentHeading));
                    }
                    currentChunkBuilder.Clear();
                }
            }

            if (currentChunkBuilder.Length > 0)
            {
                var chunkText = currentChunkBuilder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(chunkText))
                {
                    chunkList.Add((chunkText, chunkStartLine, rawLines.Length, currentHeading));
                }
            }

            if (chunkList.Count == 0 && !string.IsNullOrWhiteSpace(content))
            {
                chunkList.Add((content.Trim(), 1, rawLines.Length, title));
            }

            int ordinal = 1;
            foreach (var (chunkText, sLine, eLine, hPath) in chunkList)
            {
                var chunkId = Guid.NewGuid().ToString("N");
                using var insertChunk = conn.CreateCommand();
                insertChunk.Transaction = tx;
                insertChunk.CommandText = @"
                    INSERT INTO chunks (id, document_id, document_version, ordinal, text, token_count, enabled, start_line, end_line, heading_path)
                    VALUES (@id, @doc_id, 1, @ord, @text, @tokens, 1, @s_line, @e_line, @h_path);
                    INSERT INTO fts_chunks (chunk_id, document_id, text)
                    VALUES (@id, @doc_id, @text);
                ";
                insertChunk.Parameters.AddWithValue("@id", chunkId);
                insertChunk.Parameters.AddWithValue("@doc_id", docId);
                insertChunk.Parameters.AddWithValue("@ord", ordinal++);
                insertChunk.Parameters.AddWithValue("@text", chunkText);
                insertChunk.Parameters.AddWithValue("@tokens", chunkText.Length);
                insertChunk.Parameters.AddWithValue("@s_line", sLine);
                insertChunk.Parameters.AddWithValue("@e_line", eLine);
                insertChunk.Parameters.AddWithValue("@h_path", (object?)hPath ?? DBNull.Value);
                await insertChunk.ExecuteNonQueryAsync(ct);

                if (hasLexical)
                {
                    var docTitle = ExtractTitle(title, chunkText);
                    var heading = hPath ?? ExtractHeading(chunkText);
                    using var insertLex = conn.CreateCommand();
                    insertLex.Transaction = tx;
                    insertLex.CommandText = "INSERT INTO fts_chunks_lexical(chunk_id, document_id, title, heading, text) VALUES(@id, @doc, @title, @heading, @text);";
                    insertLex.Parameters.AddWithValue("@id", chunkId);
                    insertLex.Parameters.AddWithValue("@doc", docId);
                    insertLex.Parameters.AddWithValue("@title", TokenizeLexical(docTitle));
                    insertLex.Parameters.AddWithValue("@heading", TokenizeLexical(heading));
                    insertLex.Parameters.AddWithValue("@text", TokenizeLexical(chunkText));
                    await insertLex.ExecuteNonQueryAsync(ct);
                }
            }

            // 3. 递增版本
            using (var bumpCmd = conn.CreateCommand())
            {
                bumpCmd.Transaction = tx;
                bumpCmd.CommandText = @"
                    UPDATE kb_manifest
                    SET read_revision = read_revision + 1,
                        content_generation = content_generation + 1,
                        updated_at = @now
                    WHERE kb_id = @id;
                ";
                bumpCmd.Parameters.AddWithValue("@now", now);
                bumpCmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
                await bumpCmd.ExecuteNonQueryAsync(ct);
            }

            tx.Commit();

            return new KnowledgeBaseDocumentRecord(
                Id: docId,
                ConnectorId: connectorId,
                SourceKey: docKey,
                Locator: $"manual://{connectorId}/{docId}",
                SourceRevision: null,
                ContentHash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))),
                DocumentVersion: 1,
                ChunkCount: ordinal - 1,
                CreatedAt: DateTime.UtcNow,
                UpdatedAt: DateTime.UtcNow
            );
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
                       c.enabled, d.source_key,
                       c.start_line, c.end_line, c.heading_path
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
                    DocumentSourceKey: reader.IsDBNull(9) ? null : reader.GetString(9),
                    StartLine: reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                    EndLine: reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                    HeadingPath: reader.IsDBNull(12) ? null : reader.GetString(12)
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
                   c.enabled, d.source_key,
                   c.start_line, c.end_line, c.heading_path
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
                DocumentSourceKey: reader.IsDBNull(9) ? null : reader.GetString(9),
                StartLine: reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                EndLine: reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                HeadingPath: reader.IsDBNull(12) ? null : reader.GetString(12)
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
                var title = ExtractTitle(string.Empty, text);
                var heading = ExtractHeading(text);
                using var insertLex = conn.CreateCommand();
                insertLex.Transaction = tx;
                insertLex.CommandText = "INSERT INTO fts_chunks_lexical(chunk_id, document_id, title, heading, text) VALUES(@id, @doc, @title, @heading, @text);";
                insertLex.Parameters.AddWithValue("@id", chunkId);
                insertLex.Parameters.AddWithValue("@doc", documentId);
                insertLex.Parameters.AddWithValue("@title", TokenizeLexical(title));
                insertLex.Parameters.AddWithValue("@heading", TokenizeLexical(heading));
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

            await InvalidateShadowRowsAsync(conn, tx, chunkId: chunkId, documentId: null, connectorId: null, ct);

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
                var title = ExtractTitle(string.Empty, newText);
                var heading = ExtractHeading(newText);
                using var cleanLex = conn.CreateCommand();
                cleanLex.Transaction = tx;
                cleanLex.CommandText = @"
                    DELETE FROM fts_chunks_lexical WHERE chunk_id = @id;
                    INSERT INTO fts_chunks_lexical (chunk_id, document_id, title, heading, text) VALUES (@id, @doc_id, @title, @heading, @text);
                ";
                cleanLex.Parameters.AddWithValue("@id", chunkId);
                cleanLex.Parameters.AddWithValue("@doc_id", docId);
                cleanLex.Parameters.AddWithValue("@title", TokenizeLexical(title));
                cleanLex.Parameters.AddWithValue("@heading", TokenizeLexical(heading));
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
            await InvalidateShadowRowsAsync(conn, tx: null, chunkId: chunkId, documentId: null, connectorId: null, ct);
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

            await InvalidateShadowRowsAsync(conn, tx, chunkId: chunkId, documentId: null, connectorId: null, ct);

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

    /// <summary>
    /// 蓝绿影子重构索引：读路径全程服务旧代；影子表按 chunk_id + source_version 续跑；
    /// 运行期写路径自动失效影子行（脏标记），切换前在写栅栏内追平脏切片后单事务换表；
    /// 保留上一代 chunk_vectors_old 供回滚；SIMD 池提交后惰性按代次刷新。
    /// </summary>
    public async Task<ShadowReindexResult> ExecuteShadowModelReindexAsync(
        Func<IReadOnlyList<string>, Task<IReadOnlyList<float[]>>> vectorProvider,
        string newSpecHash,
        int newDimension,
        IProgress<ShadowReindexProgress>? progress = null,
        CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        const int batchSize = 32;
        int totalChunks;
        int resumedChunks;
        var pending = new List<(string ChunkId, string DocumentId, string Text, long DocVersion)>();

        // 1. 准备影子表：崩溃残留且 cohort/维度完全匹配时续跑，否则重建
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            var reusable = false;
            if (await ShadowTableExistsAsync(conn, null, ct))
            {
                using var matchCmd = conn.CreateCommand();
                matchCmd.CommandText = @"
                    SELECT COUNT(1) FROM chunk_vectors_shadow
                    WHERE producer_cohort IS NOT @cohort
                       OR (@dim > 0 AND dimension != @dim);
                ";
                matchCmd.Parameters.AddWithValue("@cohort", newSpecHash);
                matchCmd.Parameters.AddWithValue("@dim", newDimension);
                reusable = Convert.ToInt64(await matchCmd.ExecuteScalarAsync(ct)) == 0;
            }

            using var prepCmd = conn.CreateCommand();
            if (reusable)
            {
                _logger?.LogInformation("[SqliteKnowledgeBaseStore] 检测到匹配的影子表残留，启用断点续跑: {KbId} (Spec={Spec})", KnowledgeBaseId, newSpecHash);
            }
            else
            {
                prepCmd.CommandText = @"
                    DROP TABLE IF EXISTS chunk_vectors_shadow;
                    CREATE TABLE chunk_vectors_shadow (
                        chunk_id TEXT PRIMARY KEY,
                        document_id TEXT NOT NULL,
                        dimension INTEGER NOT NULL,
                        vector_blob BLOB NOT NULL,
                        producer_cohort TEXT,
                        source_version INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL,
                        FOREIGN KEY(document_id) REFERENCES documents(id) ON DELETE CASCADE
                    );
                    CREATE INDEX idx_chunk_vectors_shadow_doc ON chunk_vectors_shadow(document_id);
                ";
                await prepCmd.ExecuteNonQueryAsync(ct);
            }

            using var totalCmd = conn.CreateCommand();
            totalCmd.CommandText = "SELECT COUNT(1) FROM chunks WHERE enabled = 1;";
            totalChunks = Convert.ToInt32(await totalCmd.ExecuteScalarAsync(ct));

            pending = await QueryShadowPendingChunksAsync(conn, null, ct);
            resumedChunks = totalChunks - pending.Count;
        }
        finally
        {
            _writeGate.Release();
        }

        // 2. 分批编码待处理切片并写入影子表（批间允许并发读写）
        var processed = resumedChunks;
        progress?.Report(new ShadowReindexProgress(processed, totalChunks));
        for (int i = 0; i < pending.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = pending.Skip(i).Take(batchSize).ToList();
            var vectors = await vectorProvider(batch.Select(b => b.Text).ToList());
            ValidateVectorBatch(vectors, batch.Count, newDimension);
            await UpsertShadowBatchAsync(batch, vectors, newSpecHash, ct);
            processed += batch.Count;
            progress?.Report(new ShadowReindexProgress(processed, totalChunks));
        }

        // 3. 追平运行期脏写入 → 写栅栏内原子换表（线性化切换点）
        progress?.Report(new ShadowReindexProgress(totalChunks, totalChunks));
        const int maxCatchUpRounds = 64;
        long newVectorGen = 1;
        for (int round = 0; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            if (round >= maxCatchUpRounds)
            {
                throw new InvalidOperationException("影子索引追平未收敛：入库写入速率持续高于编码速率，请在低峰期重试重构。");
            }

            List<(string ChunkId, string DocumentId, string Text, long DocVersion)> dirty;
            await _writeGate.WaitAsync(ct);
            try
            {
                using var conn = CreateConnection();
                dirty = await QueryShadowPendingChunksAsync(conn, null, ct);
                if (dirty.Count == 0)
                {
                    newVectorGen = await SwapShadowTableAsync(conn, newSpecHash, ct);
                    break;
                }
            }
            finally
            {
                _writeGate.Release();
            }

            for (int i = 0; i < dirty.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();
                var batch = dirty.Skip(i).Take(batchSize).ToList();
                var vectors = await vectorProvider(batch.Select(b => b.Text).ToList());
                ValidateVectorBatch(vectors, batch.Count, newDimension);
                await UpsertShadowBatchAsync(batch, vectors, newSpecHash, ct);
            }
        }

        return new ShadowReindexResult(totalChunks, totalChunks, newVectorGen, resumedChunks);
    }

    /// <summary>
    /// 每批切前验收：数量对齐 + 维度一致 + 有限值 + 非零（拒绝模拟/兜底假向量写入）。
    /// </summary>
    private static void ValidateVectorBatch(IReadOnlyList<float[]> vectors, int expectedCount, int expectedDimension)
    {
        if (vectors.Count != expectedCount)
        {
            throw new InvalidOperationException($"影子索引生成异常: 期望 {expectedCount} 个向量，实际返回 {vectors.Count} 个。");
        }

        for (int i = 0; i < vectors.Count; i++)
        {
            var vec = vectors[i];
            if (expectedDimension > 0 && vec.Length != expectedDimension)
            {
                throw new InvalidOperationException($"影子索引维度门禁失败: 第 {i} 个向量维度 {vec.Length} != 目标规格维度 {expectedDimension}。拒绝切换，请检查目标模型是否就绪。");
            }

            var norm = 0.0;
            for (int j = 0; j < vec.Length; j++)
            {
                if (!float.IsFinite(vec[j]))
                {
                    throw new InvalidOperationException($"影子索引数值门禁失败: 第 {i} 个向量含 NaN/Infinity。拒绝切换。");
                }
                norm += (double)vec[j] * vec[j];
            }
            if (norm <= 1e-12)
            {
                throw new InvalidOperationException($"影子索引零向量门禁失败: 第 {i} 个向量为全零（模型未就绪或兜底假向量）。拒绝切换。");
            }
        }
    }

    private static async Task<List<(string ChunkId, string DocumentId, string Text, long DocVersion)>> QueryShadowPendingChunksAsync(
        SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        var list = new List<(string, string, string, long)>();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT c.id, c.document_id, COALESCE(c.embedding_text, c.text), c.document_version
            FROM chunks c
            LEFT JOIN chunk_vectors_shadow s
                ON s.chunk_id = c.id AND s.source_version = c.document_version
            WHERE c.enabled = 1 AND s.chunk_id IS NULL
            ORDER BY c.ordinal ASC;
        ";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }
        return list;
    }

    private async Task UpsertShadowBatchAsync(
        List<(string ChunkId, string DocumentId, string Text, long DocVersion)> batch,
        IReadOnlyList<float[]> vectors,
        string specHash,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow.ToString("O");
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var tx = conn.BeginTransaction();
            using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = @"
                INSERT INTO chunk_vectors_shadow (chunk_id, document_id, dimension, vector_blob, producer_cohort, source_version, created_at)
                VALUES (@chunk_id, @doc_id, @dimension, @vector_blob, @cohort, @source_version, @created_at)
                ON CONFLICT(chunk_id) DO UPDATE SET
                    document_id = excluded.document_id,
                    dimension = excluded.dimension,
                    vector_blob = excluded.vector_blob,
                    producer_cohort = excluded.producer_cohort,
                    source_version = excluded.source_version,
                    created_at = excluded.created_at;
            ";
            var pChunkId = insertCmd.Parameters.Add("@chunk_id", SqliteType.Text);
            var pDocId = insertCmd.Parameters.Add("@doc_id", SqliteType.Text);
            var pDim = insertCmd.Parameters.Add("@dimension", SqliteType.Integer);
            var pBlob = insertCmd.Parameters.Add("@vector_blob", SqliteType.Blob);
            var pCohort = insertCmd.Parameters.Add("@cohort", SqliteType.Text);
            var pVersion = insertCmd.Parameters.Add("@source_version", SqliteType.Integer);
            var pCreated = insertCmd.Parameters.Add("@created_at", SqliteType.Text);

            for (int j = 0; j < batch.Count; j++)
            {
                var vec = vectors[j];
                pChunkId.Value = batch[j].ChunkId;
                pDocId.Value = batch[j].DocumentId;
                pDim.Value = vec.Length;
                pBlob.Value = MemoryMarshal.Cast<float, byte>(vec.AsSpan()).ToArray();
                pCohort.Value = specHash;
                pVersion.Value = batch[j].DocVersion;
                pCreated.Value = now;
                await insertCmd.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 写栅栏内单事务换表：保留上一代 chunk_vectors_old，记录 previous_spec_hash，
    /// 提交后仅失效内存池（读路径按 vectorGeneration 惰性重载，不在事务外做致命操作）。
    /// </summary>
    private async Task<long> SwapShadowTableAsync(SqliteConnection conn, string newSpecHash, CancellationToken ct)
    {
        using var tx = conn.BeginTransaction();
        using var swapCmd = conn.CreateCommand();
        swapCmd.Transaction = tx;
        swapCmd.CommandText = @"
            DROP TABLE IF EXISTS chunk_vectors_old;
            ALTER TABLE chunk_vectors RENAME TO chunk_vectors_old;
            ALTER TABLE chunk_vectors_shadow RENAME TO chunk_vectors;
            DROP INDEX IF EXISTS idx_chunk_vectors_doc;
            DROP INDEX IF EXISTS idx_chunk_vectors_shadow_doc;
            CREATE INDEX idx_chunk_vectors_doc ON chunk_vectors(document_id);

            UPDATE kb_manifest
            SET index_epoch = index_epoch + 1,
                vector_generation = vector_generation + 1,
                read_revision = read_revision + 1,
                previous_spec_hash = active_spec_hash,
                active_spec_hash = @specHash,
                target_spec_hash = @specHash,
                active_vector_count = (SELECT COUNT(1) FROM chunk_vectors),
                updated_at = @now
            WHERE kb_id = @id;

            SELECT vector_generation FROM kb_manifest WHERE kb_id = @id;
        ";
        swapCmd.Parameters.AddWithValue("@specHash", newSpecHash);
        swapCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        swapCmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
        var scalar = await swapCmd.ExecuteScalarAsync(ct);
        var newVectorGen = scalar != null && scalar != DBNull.Value ? Convert.ToInt64(scalar) : 1;
        tx.Commit();

        InMemoryTensorProjectionPool.GetOrCreate(KnowledgeBaseId).Invalidate();
        _logger?.LogInformation("[SqliteKnowledgeBaseStore] 影子表原子切换完成: {KbId} → Generation {Gen} (Spec={Spec})，上一代已保留供回滚", KnowledgeBaseId, newVectorGen, newSpecHash);
        return newVectorGen;
    }

    private static async Task<bool> ShadowTableExistsAsync(SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='chunk_vectors_shadow';";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    /// <summary>
    /// 影子一致性维护（双写脏标记）：写路径变更切片后删除对应影子行，
    /// 缺失的影子行即「待目标模型重编码」，由切换前追平循环收敛。
    /// 影子表不存在（无重构运行）时零开销直通。
    /// </summary>
    private static async Task InvalidateShadowRowsAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string? chunkId, string? documentId, string? connectorId,
        CancellationToken ct)
    {
        if (!await ShadowTableExistsAsync(conn, tx, ct)) return;

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        if (chunkId != null)
        {
            cmd.CommandText = "DELETE FROM chunk_vectors_shadow WHERE chunk_id = @id;";
            cmd.Parameters.AddWithValue("@id", chunkId);
        }
        else if (documentId != null)
        {
            cmd.CommandText = "DELETE FROM chunk_vectors_shadow WHERE document_id = @id;";
            cmd.Parameters.AddWithValue("@id", documentId);
        }
        else
        {
            cmd.CommandText = "DELETE FROM chunk_vectors_shadow WHERE document_id IN (SELECT id FROM documents WHERE connector_id = @id);";
            cmd.Parameters.AddWithValue("@id", connectorId!);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropShadowVectorTableAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP TABLE IF EXISTS chunk_vectors_shadow;";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<string?> RollbackVectorGenerationAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _writeGate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using (var existCmd = conn.CreateCommand())
            {
                existCmd.CommandText = "SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='chunk_vectors_old';";
                if (Convert.ToInt64(await existCmd.ExecuteScalarAsync(ct)) == 0) return null;
            }

            var manifest = await GetManifestInternalAsync(conn, null, ct);
            var previousSpec = manifest.PreviousEmbeddingSpecHash;
            if (string.IsNullOrEmpty(previousSpec))
            {
                using var cohortCmd = conn.CreateCommand();
                cohortCmd.CommandText = @"
                    SELECT producer_cohort FROM chunk_vectors_old
                    WHERE producer_cohort IS NOT NULL AND producer_cohort NOT IN ('manual', 'hitl_edit')
                    LIMIT 1;
                ";
                previousSpec = (string?)await cohortCmd.ExecuteScalarAsync(ct);
            }
            if (string.IsNullOrEmpty(previousSpec)) previousSpec = "spec_lexical_only";

            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                DROP TABLE IF EXISTS chunk_vectors;
                ALTER TABLE chunk_vectors_old RENAME TO chunk_vectors;
                DROP TABLE IF EXISTS chunk_vectors_shadow;
                CREATE INDEX IF NOT EXISTS idx_chunk_vectors_doc ON chunk_vectors(document_id);

                UPDATE kb_manifest
                SET index_epoch = index_epoch + 1,
                    vector_generation = vector_generation + 1,
                    read_revision = read_revision + 1,
                    active_spec_hash = @prev,
                    target_spec_hash = @prev,
                    previous_spec_hash = NULL,
                    active_vector_count = (SELECT COUNT(1) FROM chunk_vectors),
                    updated_at = @now
                WHERE kb_id = @id;
            ";
            cmd.Parameters.AddWithValue("@prev", previousSpec);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@id", KnowledgeBaseId);
            await cmd.ExecuteNonQueryAsync(ct);
            tx.Commit();

            InMemoryTensorProjectionPool.GetOrCreate(KnowledgeBaseId).Invalidate();
            _logger?.LogInformation("[SqliteKnowledgeBaseStore] 向量代次回滚完成: {KbId} → Spec {Spec}", KnowledgeBaseId, previousSpec);
            return previousSpec;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<VectorGenerationInfo> GetVectorGenerationInfoAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var conn = CreateConnection(readOnly: true);

        var manifest = await GetManifestInternalAsync(conn, null, ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                (SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='chunk_vectors_old'),
                (SELECT COUNT(1) FROM sqlite_master WHERE type='table' AND name='chunk_vectors_shadow'),
                (SELECT COUNT(1) FROM chunk_vectors);
        ";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var hasOld = reader.GetInt64(0) > 0;
        var hasShadow = reader.GetInt64(1) > 0;
        var activeCount = reader.GetInt32(2);
        await reader.CloseAsync();

        var shadowRows = 0;
        if (hasShadow)
        {
            using var shadowCmd = conn.CreateCommand();
            shadowCmd.CommandText = "SELECT COUNT(1) FROM chunk_vectors_shadow;";
            shadowRows = Convert.ToInt32(await shadowCmd.ExecuteScalarAsync(ct));
        }

        return new VectorGenerationInfo(
            ActiveSpecId: manifest.ActiveEmbeddingSpecHash,
            PreviousSpecId: hasOld ? manifest.PreviousEmbeddingSpecHash : null,
            CanRollback: hasOld,
            ActiveVectorCount: activeCount,
            ShadowExists: hasShadow,
            ShadowRowCount: shadowRows);
    }
}
