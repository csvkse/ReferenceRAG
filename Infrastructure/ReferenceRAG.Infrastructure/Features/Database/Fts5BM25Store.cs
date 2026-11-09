using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Storage;

/// <summary>
/// FTS5 BM25 存储实现 - 支持多字段加权 (Title 5.0, Heading 2.0, Body 1.0) 与分词 LRU 缓存加速
/// </summary>
public class Fts5BM25Store : IBM25Store, IScopedLexicalStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock;
    private readonly bool _ownsResources;
    private readonly ILogger<Fts5BM25Store>? _logger;
    private bool _disposed;

    // FTS5 表名
    private const string FtsTableName = "bm25_fts";

    /// <summary>生产用：接受共享连接，与其他 Store 共用同一连接和锁。</summary>
    public Fts5BM25Store(SharedSqliteConnection sharedDb, ILogger<Fts5BM25Store>? logger = null)
    {
        _logger = logger;
        _connection = sharedDb.Connection;
        _lock = sharedDb.Lock;
        _ownsResources = false;
        InitializeDatabase();
    }

    /// <summary>兼容构造函数（测试 / 独立使用）。</summary>
    public Fts5BM25Store(string dbPath, ILogger<Fts5BM25Store>? logger = null)
    {
        _logger = logger;
        _lock = new SemaphoreSlim(1, 1);
        _ownsResources = true;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();
        InitializeDatabase();
    }

    /// <summary>
    /// 初始化数据库表 (多字段加权模式)
    /// </summary>
    private void InitializeDatabase()
    {
        bool needsUpgrade = false;
        try
        {
            using var pragma = _connection.CreateCommand();
            pragma.CommandText = $"PRAGMA table_info({FtsTableName})";
            using var reader = pragma.ExecuteReader();
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                cols.Add(reader.GetString(1));
            }
            if (cols.Count > 0 && (!cols.Contains("title") || !cols.Contains("heading")))
            {
                needsUpgrade = true;
            }
        }
        catch
        {
            // 表可能尚未创建
        }

        if (needsUpgrade)
        {
            try
            {
                ExecuteNonQuery($"DROP TABLE IF EXISTS {FtsTableName}");
            }
            catch { }
        }

        // 创建多字段加权 FTS5 虚拟表
        var createFts = $@"
            CREATE VIRTUAL TABLE IF NOT EXISTS {FtsTableName} USING fts5(
                id UNINDEXED,
                title,
                heading,
                content,
                tokenize='unicode61 remove_diacritics 1'
            );
        ";
        ExecuteNonQuery(createFts);
    }

    private void ExecuteNonQuery(string sql, SqliteTransaction? transaction = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        if (transaction != null)
            cmd.Transaction = transaction;
        cmd.ExecuteNonQuery();
    }

    #region IBM25Store Interface Implementation

    /// <inheritdoc />
    public Task IndexDocumentAsync(string chunkId, string content) =>
        IndexDocumentAsync(chunkId, string.Empty, string.Empty, content);

    /// <inheritdoc />
    public async Task IndexDocumentAsync(string chunkId, string title, string heading, string content)
    {
        var tokenizedTitle = TokenizeForIndex(title);
        var tokenizedHeading = TokenizeForIndex(heading);
        var tokenizedContent = TokenizeForIndex(content);

        await _lock.WaitAsync();
        try
        {
            using var deleteCmd = _connection.CreateCommand();
            deleteCmd.CommandText = $"DELETE FROM {FtsTableName} WHERE id = @id";
            deleteCmd.Parameters.AddWithValue("@id", chunkId);
            await deleteCmd.ExecuteNonQueryAsync();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO {FtsTableName}(id, title, heading, content) VALUES (@id, @title, @heading, @content)";
            cmd.Parameters.AddWithValue("@id", chunkId);
            cmd.Parameters.AddWithValue("@title", tokenizedTitle);
            cmd.Parameters.AddWithValue("@heading", tokenizedHeading);
            cmd.Parameters.AddWithValue("@content", tokenizedContent);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public Task IndexBatchAsync(IEnumerable<(string chunkId, string content)> documents, IProgress<int>? progress = null) =>
        IndexBatchAsync(documents.Select(d => (d.chunkId, string.Empty, string.Empty, d.content)), progress);

    /// <inheritdoc />
    public async Task IndexBatchAsync(IEnumerable<(string chunkId, string title, string heading, string content)> documents, IProgress<int>? progress = null)
    {
        progress?.Report(10);
        var docsList = documents.ToList();

        await _lock.WaitAsync();
        try
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var (chunkId, title, heading, content) in docsList)
                {
                    var tokenizedTitle = TokenizeForIndex(title);
                    var tokenizedHeading = TokenizeForIndex(heading);
                    var tokenizedContent = TokenizeForIndex(content);

                    using var deleteCmd = _connection.CreateCommand();
                    deleteCmd.CommandText = $"DELETE FROM {FtsTableName} WHERE id = @id";
                    deleteCmd.Parameters.AddWithValue("@id", chunkId);
                    deleteCmd.Transaction = transaction;
                    await deleteCmd.ExecuteNonQueryAsync();

                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = $"INSERT INTO {FtsTableName}(id, title, heading, content) VALUES (@id, @title, @heading, @content)";
                    cmd.Parameters.AddWithValue("@id", chunkId);
                    cmd.Parameters.AddWithValue("@title", tokenizedTitle);
                    cmd.Parameters.AddWithValue("@heading", tokenizedHeading);
                    cmd.Parameters.AddWithValue("@content", tokenizedContent);
                    cmd.Transaction = transaction;
                    await cmd.ExecuteNonQueryAsync();
                }
                transaction.Commit();
            }
            catch { transaction.Rollback(); throw; }
        }
        finally { _lock.Release(); }
        progress?.Report(100);
    }

    /// <inheritdoc />
    public async Task ClearIndexAsync()
    {
        await _lock.WaitAsync();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {FtsTableName}";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex) { _logger?.LogError(ex, "清空索引失败"); throw; }
        finally { _lock.Release(); }
    }

    public async Task<bool> VerifyExactIndexAsync(IReadOnlyDictionary<string, string> expectedDocuments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedDocuments);
        var remaining = expectedDocuments.ToDictionary(pair => pair.Key, pair => TokenizeForIndex(pair.Value), StringComparer.Ordinal);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT id, content FROM {FtsTableName}";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                if (!remaining.Remove(id, out var expected) || reader.GetString(1) != expected) return false;
            }
            return remaining.Count == 0;
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task DeleteDocumentsByIdsAsync(IEnumerable<string> chunkIds)
    {
        var ids = chunkIds.ToList();
        if (ids.Count == 0) return;

        await _lock.WaitAsync();
        try
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var id in ids)
                {
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = $"DELETE FROM {FtsTableName} WHERE id = @id";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Transaction = transaction;
                    await cmd.ExecuteNonQueryAsync();
                }
                transaction.Commit();
            }
            catch { transaction.Rollback(); throw; }
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task<List<BM25SearchResult>> SearchAsync(string query, int topK = 10, float k1 = 1.5f, float b = 0.75f)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<BM25SearchResult>();

        await _lock.WaitAsync();
        try
        {
            // SQLite FTS5 多字段加权: Title 5.0, Heading 2.0, Body 1.0
            var sql = $@"
                SELECT id, content, bm25({FtsTableName}, 5.0, 2.0, 1.0) as score
                FROM {FtsTableName}
                WHERE {FtsTableName} MATCH @query
                ORDER BY bm25({FtsTableName}, 5.0, 2.0, 1.0)
                LIMIT @topK";

            var results = new List<BM25SearchResult>();
            int rank = 1;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@query", EscapeFtsQuery(query));
            cmd.Parameters.AddWithValue("@topK", topK);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new BM25SearchResult
                {
                    ChunkId = reader.GetString(0),
                    Content = reader.GetString(1),
                    Score   = Math.Abs(reader.GetFloat(2)),
                    Rank    = rank++
                });
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "搜索失败: {Query}", query);
            return new List<BM25SearchResult>();
        }
        finally { _lock.Release(); }
    }

    public async Task<List<BM25SearchResult>> SearchScopedAsync(
        string query, IReadOnlyCollection<string> allowedChunkIds, int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(allowedChunkIds);
        if (query.Length > 4096) throw new ArgumentOutOfRangeException(nameof(query));
        if (topK is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(topK));
        cancellationToken.ThrowIfCancellationRequested();
        var tokens = Tokenize(query).Where(t => t.Any(char.IsLetterOrDigit))
            .Distinct(StringComparer.Ordinal).Take(64).ToArray();
        if (allowedChunkIds.Count == 0 || tokens.Length == 0) return new();
        var match = string.Join(" OR ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));

        await _lock.WaitAsync(cancellationToken);
        try
        {
            using (var setup = _connection.CreateCommand())
            {
                setup.CommandText = "CREATE TEMP TABLE IF NOT EXISTS lexical_allowed_ids (id TEXT PRIMARY KEY) WITHOUT ROWID; DELETE FROM lexical_allowed_ids;";
                await setup.ExecuteNonQueryAsync(cancellationToken);
            }
            using (var insert = _connection.CreateCommand())
            {
                insert.CommandText = "INSERT OR IGNORE INTO lexical_allowed_ids(id) VALUES (@id)";
                var parameter = insert.Parameters.Add("@id", SqliteType.Text);
                foreach (var id in allowedChunkIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    parameter.Value = id;
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $@"SELECT f.id, f.content, bm25({FtsTableName}, 5.0, 2.0, 1.0) AS score
                FROM {FtsTableName} AS f INNER JOIN lexical_allowed_ids AS a ON a.id = f.id
                WHERE {FtsTableName} MATCH @query ORDER BY score, f.id LIMIT @topK";
            cmd.Parameters.AddWithValue("@query", match);
            cmd.Parameters.AddWithValue("@topK", topK);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var results = new List<BM25SearchResult>();
            while (await reader.ReadAsync(cancellationToken))
                results.Add(new BM25SearchResult { ChunkId = reader.GetString(0), Content = reader.GetString(1),
                    Score = Math.Abs(reader.GetDouble(2)), Rank = results.Count + 1 });
            return results;
        }
        finally
        {
            using var cleanup = _connection.CreateCommand();
            cleanup.CommandText = "DROP TABLE IF EXISTS temp.lexical_allowed_ids";
            try { cleanup.ExecuteNonQuery(); }
            finally { _lock.Release(); }
        }
    }

    /// <inheritdoc />
    public async Task<BM25IndexStats> GetStatsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            long totalDocs = 0;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM {FtsTableName}";
                totalDocs = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }

            double avgDocLength = 0;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT AVG(LENGTH(content)) FROM {FtsTableName}";
                var result = await cmd.ExecuteScalarAsync();
                if (result != null && result != DBNull.Value)
                    avgDocLength = Convert.ToDouble(result);
            }

            long vocabSize = 0;
            try
            {
                using var vocabCmd = _connection.CreateCommand();
                vocabCmd.CommandText = $"CREATE VIRTUAL TABLE IF NOT EXISTS temp.bm25_vocab USING fts5vocab({FtsTableName}, 'row')";
                await vocabCmd.ExecuteNonQueryAsync();

                using var countCmd = _connection.CreateCommand();
                countCmd.CommandText = "SELECT COUNT(*) FROM temp.bm25_vocab";
                vocabSize = Convert.ToInt64(await countCmd.ExecuteScalarAsync());
            }
            catch
            {
                vocabSize = 0;
            }

            return new BM25IndexStats
            {
                TotalDocuments   = (int)totalDocs,
                AverageDocLength = avgDocLength,
                VocabularySize   = (int)vocabSize
            };
        }
        finally { _lock.Release(); }
    }

    #endregion

    /// <inheritdoc />
    public async Task<int> CleanupOrphanDocumentsAsync(IEnumerable<string> validChunkIds, CancellationToken cancellationToken = default)
    {
        var validSet = validChunkIds.ToHashSet();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var orphanIds = new List<string>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT id FROM {FtsTableName}";
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetString(0);
                    if (!validSet.Contains(id)) orphanIds.Add(id);
                }
            }
            if (orphanIds.Count == 0) return 0;

            using var tx = _connection.BeginTransaction();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {FtsTableName} WHERE id = @id";
                var param = cmd.Parameters.Add("@id", SqliteType.Text);
                foreach (var id in orphanIds)
                {
                    param.Value = id;
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                tx.Commit();
            }
            catch { tx.Rollback(); throw; }
            return orphanIds.Count;
        }
        finally { _lock.Release(); }
    }

    #region 分词方法

    private static string TokenizeForIndex(string text)
    {
        return TokenizationCacheProxy.Default.TokenizeForIndex(text);
    }

    private static string EscapeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        var tokens = Tokenize(query);

        if (tokens.Count == 0)
            return query;

        return string.Join(" OR ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }

    private static List<string> Tokenize(string text)
    {
        return TokenizationCacheProxy.Default.Tokenize(text).ToList();
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsResources)
        {
            _lock.Dispose();
            _connection?.Close();
            _connection?.Dispose();
        }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
