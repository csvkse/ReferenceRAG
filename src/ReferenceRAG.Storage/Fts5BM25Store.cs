using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using System.Text;
using System.Text.RegularExpressions;

namespace ReferenceRAG.Storage;

/// <summary>
/// FTS5 BM25 存储实现 - 单一索引模式，支持中英文混合搜索
///
/// 特点：
/// 1. 中文按字符分词，英文按单词分词
/// 2. 预处理内容后再存入 FTS5 索引
/// 3. 使用 FTS5 内置 bm25() 排名函数
/// </summary>
public class Fts5BM25Store : IBM25Store, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _lock;
    private readonly bool _ownsResources;
    private readonly ILogger<Fts5BM25Store>? _logger;
    private bool _disposed;

    // FTS5 表名
    private const string FtsTableName = "bm25_fts";

    // 英文停用词
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "in", "on", "at", "to", "for",
        "of", "with", "by", "from", "is", "are", "was", "were", "be", "been",
        "being", "have", "has", "had", "do", "does", "did", "will", "would",
        "could", "should", "may", "might", "must", "shall", "can", "need",
        "it", "its", "this", "that", "these", "those", "i", "you", "he",
        "she", "we", "they", "what", "which", "who", "when", "where", "why",
        "how", "all", "each", "every", "both", "few", "more", "most", "other",
        "some", "such", "no", "not", "only", "same", "so", "than", "too",
        "very", "just", "as", "if", "then", "because", "while", "although"
    };

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
    /// 初始化数据库表
    /// </summary>
    private void InitializeDatabase()
    {
        // 创建 FTS5 虚拟表
        var createFts = $@"
            CREATE VIRTUAL TABLE IF NOT EXISTS {FtsTableName} USING fts5(
                id UNINDEXED,
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
    public async Task IndexDocumentAsync(string chunkId, string content)
    {
        var tokenizedContent = TokenizeForIndex(content);
        await _lock.WaitAsync();
        try
        {
            using var deleteCmd = _connection.CreateCommand();
            deleteCmd.CommandText = $"DELETE FROM {FtsTableName} WHERE id = @id";
            deleteCmd.Parameters.AddWithValue("@id", chunkId);
            await deleteCmd.ExecuteNonQueryAsync();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"INSERT INTO {FtsTableName}(id, content) VALUES (@id, @content)";
            cmd.Parameters.AddWithValue("@id", chunkId);
            cmd.Parameters.AddWithValue("@content", tokenizedContent);
            await cmd.ExecuteNonQueryAsync();
        }
        finally { _lock.Release(); }
    }

    /// <inheritdoc />
    public async Task IndexBatchAsync(IEnumerable<(string chunkId, string content)> documents, IProgress<int>? progress = null)
    {
        progress?.Report(10);
        var docsList = documents.ToList();

        await _lock.WaitAsync();
        try
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var (chunkId, content) in docsList)
                {
                    var tokenizedContent = TokenizeForIndex(content);

                    using var deleteCmd = _connection.CreateCommand();
                    deleteCmd.CommandText = $"DELETE FROM {FtsTableName} WHERE id = @id";
                    deleteCmd.Parameters.AddWithValue("@id", chunkId);
                    deleteCmd.Transaction = transaction;
                    await deleteCmd.ExecuteNonQueryAsync();

                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = $"INSERT INTO {FtsTableName}(id, content) VALUES (@id, @content)";
                    cmd.Parameters.AddWithValue("@id", chunkId);
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
            var sql = $@"
                SELECT id, content, bm25({FtsTableName}, {k1}, {b}) as score
                FROM {FtsTableName}
                WHERE {FtsTableName} MATCH @query
                ORDER BY bm25({FtsTableName}, {k1}, {b})
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

            return new BM25IndexStats
            {
                TotalDocuments  = (int)totalDocs,
                AverageDocLength = avgDocLength,
                VocabularySize  = (int)totalDocs
            };
        }
        finally { _lock.Release(); }
    }

    #endregion

    #region 分词方法

    /// <summary>
    /// 为索引分词 - 将内容转换为空格分隔的 token
    /// 中文按字符分词，英文按单词分词
    /// </summary>
    private string TokenizeForIndex(string text)
    {
        var tokens = Tokenize(text);
        return string.Join(" ", tokens);
    }

    /// <summary>
    /// 转义 FTS5 查询字符串 - 支持中英文混合分词
    /// </summary>
    private string EscapeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        var tokens = Tokenize(query);

        if (tokens.Count == 0)
            return query;

        // 每个 token 用双引号包裹，用 OR 连接
        return string.Join(" OR ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }

    /// <summary>
    /// 分词 - 支持中英文混合
    /// 中文按字符分词，英文按单词分词
    /// </summary>
    private List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        var tokens = new List<string>();
        var currentToken = new StringBuilder();

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (currentToken.Length > 0)
                {
                    tokens.Add(currentToken.ToString().ToLowerInvariant());
                    currentToken.Clear();
                }
            }
            else if (IsChinese(c))
            {
                if (currentToken.Length > 0)
                {
                    tokens.Add(currentToken.ToString().ToLowerInvariant());
                    currentToken.Clear();
                }
                tokens.Add(c.ToString());
            }
            else if (IsPunctuation(c))
            {
                if (currentToken.Length > 0)
                {
                    tokens.Add(currentToken.ToString().ToLowerInvariant());
                    currentToken.Clear();
                }
            }
            else
            {
                currentToken.Append(c);
            }
        }

        if (currentToken.Length > 0)
        {
            tokens.Add(currentToken.ToString().ToLowerInvariant());
        }

        return tokens.Where(t => !StopWords.Contains(t) && t.Length > 0).ToList();
    }

    /// <summary>
    /// 判断字符是否为中文字符
    /// </summary>
    private static bool IsChinese(char c)
    {
        return (c >= 0x4E00 && c <= 0x9FFF) ||
               (c >= 0x3400 && c <= 0x4DBF) ||
               (c >= 0xF900 && c <= 0xFAFF) ||
               (c >= 0x20000 && c <= 0x2A6DF) ||
               (c >= 0x2A700 && c <= 0x2B73F) ||
               (c >= 0x2B740 && c <= 0x2B81F) ||
               (c >= 0x2B820 && c <= 0x2CEAF);
    }

    /// <summary>
    /// 判断字符是否为标点符号
    /// </summary>
    private static bool IsPunctuation(char c)
    {
        return char.GetUnicodeCategory(c) switch
        {
            System.Globalization.UnicodeCategory.ConnectorPunctuation => true,
            System.Globalization.UnicodeCategory.DashPunctuation => true,
            System.Globalization.UnicodeCategory.OpenPunctuation => true,
            System.Globalization.UnicodeCategory.ClosePunctuation => true,
            System.Globalization.UnicodeCategory.InitialQuotePunctuation => true,
            System.Globalization.UnicodeCategory.FinalQuotePunctuation => true,
            System.Globalization.UnicodeCategory.OtherPunctuation => true,
            _ => false
        };
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
