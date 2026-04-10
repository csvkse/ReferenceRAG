using Microsoft.Data.Sqlite;
using ObsidianRAG.Core.Interfaces;
using System.Text;
using System.Text.RegularExpressions;

namespace ObsidianRAG.Storage;

/// <summary>
/// FTS5 BM25 存储实现 - 使用 SQLite FTS5 虚拟表和内置 bm25() 排名函数
/// 性能优势：索引构建由 SQLite 自动完成，搜索使用内置 BM25 算法
/// </summary>
public class Fts5BM25Store : IBM25Store, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _dbPath;
    private bool _disposed;

    // 缓存模型启用状态
    private readonly Dictionary<string, bool> _modelEnabledCache = new();
    private readonly Dictionary<string, BM25ModelInfo> _modelInfoCache = new();

    // FTS5 表名格式
    private const string FtsTablePrefix = "bm25_fts_";
    private const string ChunkMappingTable = "bm25_chunk_mapping";

    public Fts5BM25Store(string dbPath)
    {
        _dbPath = dbPath;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();

        InitializeDatabase();
        LoadModelCache();
    }

    /// <summary>
    /// 初始化数据库表
    /// </summary>
    private void InitializeDatabase()
    {
        using var transaction = _connection.BeginTransaction();

        try
        {
            // BM25 模型表
            var createModelsTable = @"
                CREATE TABLE IF NOT EXISTS bm25_models (
                    name TEXT PRIMARY KEY,
                    k1 REAL DEFAULT 1.5,
                    b REAL DEFAULT 0.75,
                    avg_doc_length REAL DEFAULT 0,
                    total_docs INTEGER DEFAULT 0,
                    vocab_size INTEGER DEFAULT 0,
                    is_enabled INTEGER DEFAULT 0,
                    created_at TEXT
                );
            ";
            ExecuteNonQuery(createModelsTable, transaction);

            // Chunk 映射表：存储 chunk_id 到 FTS rowid 的映射
            var createMappingTable = @"
                CREATE TABLE IF NOT EXISTS bm25_chunk_mapping (
                    model_name TEXT,
                    chunk_id TEXT PRIMARY KEY,
                    fts_rowid INTEGER
                );
            ";
            ExecuteNonQuery(createMappingTable, transaction);

            // 创建索引
            var createIndexes = @"
                CREATE INDEX IF NOT EXISTS idx_mapping_model ON bm25_chunk_mapping(model_name);
                CREATE INDEX IF NOT EXISTS idx_mapping_rowid ON bm25_chunk_mapping(fts_rowid);
            ";
            ExecuteNonQuery(createIndexes, transaction);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 加载模型缓存
    /// </summary>
    private void LoadModelCache()
    {
        var sql = "SELECT name, avg_doc_length, total_docs, vocab_size, is_enabled, created_at FROM bm25_models";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var info = new BM25ModelInfo
            {
                Name = reader.GetString(0),
                AverageDocLength = reader.GetFloat(1),
                TotalDocuments = reader.GetInt32(2),
                VocabularySize = reader.GetInt32(3),
                IsEnabled = reader.GetInt32(4) == 1,
                CreatedAt = DateTime.Parse(reader.GetString(5))
            };
            _modelInfoCache[info.Name] = info;
            _modelEnabledCache[info.Name] = info.IsEnabled;
        }
    }

    /// <summary>
    /// 获取或创建 FTS5 虚拟表
    /// </summary>
    private string GetFtsTableName(string modelName)
    {
        return $"{FtsTablePrefix}{modelName}";
    }

    /// <summary>
    /// 创建 FTS5 虚拟表
    /// </summary>
    private void CreateFtsTable(string modelName)
    {
        var ftsTableName = GetFtsTableName(modelName);

        // 删除已存在的 FTS 表（如果存在）
        ExecuteNonQuery($"DROP TABLE IF EXISTS {ftsTableName}");

        // 创建 FTS5 虚拟表
        // content 列存储文档内容，FTS5 自动处理分词和索引
        var createFts = $@"
            CREATE VIRTUAL TABLE {ftsTableName} USING fts5(
                chunk_id UNINDEXED,
                content,
                tokenize='unicode61 remove_diacritics 1'
            );
        ";
        ExecuteNonQuery(createFts);
    }

    /// <summary>
    /// 分词 - 支持中英文混合（与 SqliteBM25Store 保持一致）
    /// </summary>
    private List<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var tokens = new List<string>();

        // 混合分词：中文按字符拆分 + 英文按单词拆分
        // 匹配中文字符序列
        var chinesePattern = @"[\u4e00-\u9fff]+";
        // 匹配英文单词序列
        var englishPattern = @"[a-zA-Z0-9_]+";
        // 匹配其他字符序列
        var otherPattern = @"[^\s\p{L}\p{N}]+";

        var remaining = text.ToLower();

        // 提取所有中文片段
        var chineseMatches = Regex.Matches(text, chinesePattern);
        foreach (Match match in chineseMatches)
        {
            foreach (var c in match.Value)
            {
                tokens.Add(c.ToString());
            }
        }

        // 提取所有英文单词
        var englishMatches = Regex.Matches(text, englishPattern);
        foreach (Match match in englishMatches)
        {
            var word = match.Value.ToLower();
            if (word.Length > 1)
            {
                tokens.Add(word);
            }
        }

        // 提取其他有意义字符
        var otherMatches = Regex.Matches(text, otherPattern);
        foreach (Match match in otherMatches)
        {
            var val = match.Value.Trim();
            if (!string.IsNullOrEmpty(val) && val.Length > 1)
            {
                tokens.Add(val.ToLower());
            }
        }

        return tokens;
    }

    private void ExecuteNonQuery(string sql, SqliteTransaction? transaction = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        if (transaction != null)
            cmd.Transaction = transaction;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 确保 FTS 表存在
    /// </summary>
    private async Task EnsureFtsTableExistsAsync(string modelName)
    {
        var ftsTableName = GetFtsTableName(modelName);

        // 检查 FTS 表是否存在
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.AddWithValue("@name", ftsTableName);
        var result = await cmd.ExecuteScalarAsync();

        if (result == null)
        {
            // FTS 表不存在，创建它
            CreateFtsTable(modelName);
        }
    }

    #region IBM25Store Interface Implementation

    /// <inheritdoc />
    public async Task<BM25ModelInfo> CreateModelAsync(string name, float k1 = 1.5f, float b = 0.75f)
    {
        if (ModelExists(name))
        {
            throw new InvalidOperationException($"模型 '{name}' 已存在");
        }

        var now = DateTime.UtcNow.ToString("O");

        using var transaction = _connection.BeginTransaction();
        try
        {
            // 创建模型记录
            var insertModel = @"
                INSERT INTO bm25_models (name, avg_doc_length, total_docs, vocab_size, is_enabled, created_at)
                VALUES (@name, 0, 0, 0, 1, @createdAt)
            ";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = insertModel;
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@createdAt", now);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            // 创建 FTS5 虚拟表
            CreateFtsTable(name);

            transaction.Commit();

            var modelInfo = new BM25ModelInfo
            {
                Name = name,
                IsEnabled = true,
                CreatedAt = DateTime.Parse(now)
            };

            _modelInfoCache[name] = modelInfo;
            _modelEnabledCache[name] = true;

            return modelInfo;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<BM25ModelInfo>> GetAllModelsAsync()
    {
        return await Task.Run(() =>
        {
            var result = new List<BM25ModelInfo>();
            var sql = "SELECT name, avg_doc_length, total_docs, vocab_size, is_enabled, created_at FROM bm25_models";

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new BM25ModelInfo
                {
                    Name = reader.GetString(0),
                    AverageDocLength = reader.GetFloat(1),
                    TotalDocuments = reader.GetInt32(2),
                    VocabularySize = reader.GetInt32(3),
                    IsEnabled = reader.GetInt32(4) == 1,
                    CreatedAt = DateTime.Parse(reader.GetString(5))
                });
            }

            return result;
        });
    }

    /// <inheritdoc />
    public async Task<BM25ModelInfo?> GetModelInfoAsync(string name)
    {
        return await Task.Run(() =>
        {
            if (_modelInfoCache.TryGetValue(name, out var cached))
            {
                // 从数据库刷新统计信息
                var sql = "SELECT total_docs, vocab_size FROM bm25_models WHERE name = @name";
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@name", name);

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    cached.TotalDocuments = reader.GetInt32(0);
                    cached.VocabularySize = reader.GetInt32(1);
                }

                return cached;
            }

            return null;
        });
    }

    /// <inheritdoc />
    public async Task DeleteModelAsync(string name)
    {
        if (!ModelExists(name))
        {
            return;
        }

        using var transaction = _connection.BeginTransaction();
        try
        {
            // 删除 FTS 表
            var ftsTableName = GetFtsTableName(name);
            ExecuteNonQuery($"DROP TABLE IF EXISTS {ftsTableName}", transaction);

            // 删除映射数据
            var deleteMapping = "DELETE FROM bm25_chunk_mapping WHERE model_name = @name";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = deleteMapping;
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            // 删除模型记录
            var deleteModel = "DELETE FROM bm25_models WHERE name = @name";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = deleteModel;
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();

            _modelInfoCache.Remove(name);
            _modelEnabledCache.Remove(name);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task EnableModelAsync(string name)
    {
        await Task.Run(() =>
        {
            if (!ModelExists(name))
            {
                throw new InvalidOperationException($"模型 '{name}' 不存在");
            }

            ExecuteNonQuery("UPDATE bm25_models SET is_enabled = 1 WHERE name = @name");

            _modelEnabledCache[name] = true;
            if (_modelInfoCache.TryGetValue(name, out var info))
            {
                info.IsEnabled = true;
            }
        });
    }

    /// <inheritdoc />
    public async Task DisableModelAsync(string name)
    {
        await Task.Run(() =>
        {
            if (!ModelExists(name))
            {
                throw new InvalidOperationException($"模型 '{name}' 不存在");
            }

            ExecuteNonQuery("UPDATE bm25_models SET is_enabled = 0 WHERE name = @name");

            _modelEnabledCache[name] = false;
            if (_modelInfoCache.TryGetValue(name, out var info))
            {
                info.IsEnabled = false;
            }
        });
    }

    /// <inheritdoc />
    public async Task IndexDocumentAsync(string modelName, string chunkId, string content)
    {
        if (!ModelExists(modelName))
        {
            throw new InvalidOperationException($"模型 '{modelName}' 不存在");
        }

        var ftsTableName = GetFtsTableName(modelName);

        using var transaction = _connection.BeginTransaction();
        try
        {
            // 删除旧文档（如果存在）
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $"DELETE FROM {ftsTableName} WHERE chunk_id = @chunkId";
                cmd.Parameters.AddWithValue("@chunkId", chunkId);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            // 删除旧映射
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM bm25_chunk_mapping WHERE model_name = @modelName AND chunk_id = @chunkId";
                cmd.Parameters.AddWithValue("@modelName", modelName);
                cmd.Parameters.AddWithValue("@chunkId", chunkId);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            // 插入新文档
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $"INSERT INTO {ftsTableName} (chunk_id, content) VALUES (@chunkId, @content)";
                cmd.Parameters.AddWithValue("@chunkId", chunkId);
                cmd.Parameters.AddWithValue("@content", content);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            // 获取 FTS rowid
            long ftsRowid;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT rowid FROM {ftsTableName} WHERE chunk_id = @chunkId";
                cmd.Parameters.AddWithValue("@chunkId", chunkId);
                cmd.Transaction = transaction;
                ftsRowid = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }

            // 创建映射
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO bm25_chunk_mapping (model_name, chunk_id, fts_rowid) VALUES (@modelName, @chunkId, @ftsRowid)";
                cmd.Parameters.AddWithValue("@modelName", modelName);
                cmd.Parameters.AddWithValue("@chunkId", chunkId);
                cmd.Parameters.AddWithValue("@ftsRowid", ftsRowid);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task IndexBatchAsync(string modelName, IEnumerable<(string chunkId, string content)> documents, IProgress<int>? progress = null)
    {
        if (!ModelExists(modelName))
        {
            throw new InvalidOperationException($"模型 '{modelName}' 不存在");
        }

        var docList = documents.ToList();
        if (docList.Count == 0) return;

        var ftsTableName = GetFtsTableName(modelName);

        // 确保 FTS 表存在（如果模型已存在但 FTS 表是新创建的）
        await EnsureFtsTableExistsAsync(modelName);

        using var transaction = _connection.BeginTransaction();
        try
        {
            // 阶段1：清空旧数据（如果 FTS 表已存在）
            progress?.Report(5);

            // 删除旧映射
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM bm25_chunk_mapping WHERE model_name = @modelName";
                cmd.Parameters.AddWithValue("@modelName", modelName);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            progress?.Report(10);

            // 批量插入新文档
            var mappingValues = new List<string>();

            for (int i = 0; i < docList.Count; i++)
            {
                var (chunkId, content) = docList[i];

                // 使用与 Legacy 一致的分词器进行分词
                var tokens = Tokenize(content);
                // 用空格连接 token，作为 FTS5 的 content
                var tokenizedContent = string.Join(" ", tokens);

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = $"INSERT INTO {ftsTableName} (chunk_id, content) VALUES (@chunkId, @content)";
                    cmd.Parameters.AddWithValue("@chunkId", chunkId);
                    cmd.Parameters.AddWithValue("@content", tokenizedContent);
                    cmd.Transaction = transaction;
                    await cmd.ExecuteNonQueryAsync();
                }

                // 获取 rowid
                long rowid;
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = $"SELECT rowid FROM {ftsTableName} WHERE chunk_id = @chunkId";
                    cmd.Parameters.AddWithValue("@chunkId", chunkId);
                    cmd.Transaction = transaction;
                    rowid = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                }

                // 构建 mapping value
                mappingValues.Add($"('{EscapeString(modelName)}', '{EscapeString(chunkId)}', {rowid})");

                // 批量插入 mapping
                if (mappingValues.Count >= 100 || i == docList.Count - 1)
                {
                    var mappingSql = $"INSERT INTO bm25_chunk_mapping (model_name, chunk_id, fts_rowid) VALUES {string.Join(",", mappingValues)}";
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = mappingSql;
                        cmd.Transaction = transaction;
                        await cmd.ExecuteNonQueryAsync();
                    }
                    mappingValues.Clear();
                }

                progress?.Report(10 + (int)(i * 80.0 / docList.Count));
            }

            // 更新模型统计
            progress?.Report(95);
            await UpdateModelStatsAsync(modelName, transaction);

            transaction.Commit();
            progress?.Report(100);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>
    /// 转义字符串中的单引号
    /// </summary>
    private string EscapeString(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Replace("'", "''");
    }

    /// <summary>
    /// 更新模型统计信息
    /// </summary>
    private async Task UpdateModelStatsAsync(string modelName, SqliteTransaction? transaction = null)
    {
        var ftsTableName = GetFtsTableName(modelName);

        // 获取文档数量
        long totalDocs = 0;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT COUNT(*) FROM {ftsTableName}";
            if (transaction != null) cmd.Transaction = transaction;
            totalDocs = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        // 获取平均文档长度
        double avgDocLength = 0;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT AVG(LENGTH(content)) FROM {ftsTableName}";
            if (transaction != null) cmd.Transaction = transaction;
            var result = await cmd.ExecuteScalarAsync();
            if (result != null && result != DBNull.Value)
            {
                avgDocLength = Convert.ToDouble(result);
            }
        }

        // 获取词表大小（使用 FTS5 的词表）
        int vocabSize = 0;
        using (var cmd = _connection.CreateCommand())
        {
            // FTS5 不直接暴露词表，我们用分词结果近似
            cmd.CommandText = $"SELECT COUNT(DISTINCT chunk_id) FROM {ftsTableName}";
            if (transaction != null) cmd.Transaction = transaction;
            vocabSize = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        // 更新模型表
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                UPDATE bm25_models
                SET total_docs = @totalDocs,
                    avg_doc_length = @avgDocLength,
                    vocab_size = @vocabSize
                WHERE name = @name";
            cmd.Parameters.AddWithValue("@totalDocs", totalDocs);
            cmd.Parameters.AddWithValue("@avgDocLength", avgDocLength);
            cmd.Parameters.AddWithValue("@vocabSize", vocabSize);
            cmd.Parameters.AddWithValue("@name", modelName);
            if (transaction != null) cmd.Transaction = transaction;
            await cmd.ExecuteNonQueryAsync();
        }

        // 更新缓存
        if (_modelInfoCache.TryGetValue(modelName, out var info))
        {
            info.TotalDocuments = (int)totalDocs;
            info.AverageDocLength = avgDocLength;
            info.VocabularySize = vocabSize;
        }
    }

    /// <inheritdoc />
    public async Task RebuildFullIndexAsync(string modelName, IProgress<int>? progress = null)
    {
        if (!ModelExists(modelName))
        {
            throw new InvalidOperationException($"模型 '{modelName}' 不存在");
        }

        // 清空现有索引
        await ClearModelAsync(modelName);

        // 重新计算统计信息
        await UpdateModelStatsAsync(modelName);
        progress?.Report(100);
    }

    /// <inheritdoc />
    public async Task ClearModelAsync(string modelName)
    {
        if (!ModelExists(modelName))
        {
            return;
        }

        var ftsTableName = GetFtsTableName(modelName);

        using var transaction = _connection.BeginTransaction();
        try
        {
            // 清空 FTS 表
            ExecuteNonQuery($"DELETE FROM {ftsTableName}", transaction);

            // 清空映射表
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM bm25_chunk_mapping WHERE model_name = @modelName";
                cmd.Parameters.AddWithValue("@modelName", modelName);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            // 重置统计
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    UPDATE bm25_models
                    SET total_docs = 0, avg_doc_length = 0, vocab_size = 0
                    WHERE name = @name";
                cmd.Parameters.AddWithValue("@name", modelName);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();

            if (_modelInfoCache.TryGetValue(modelName, out var info))
            {
                info.TotalDocuments = 0;
                info.AverageDocLength = 0;
                info.VocabularySize = 0;
            }
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<List<BM25SearchResult>> SearchAsync(string modelName, string query, int topK = 10, float k1 = 1.5f, float b = 0.75f)
    {
        if (!ModelExists(modelName))
        {
            throw new InvalidOperationException($"模型 '{modelName}' 不存在");
        }

        if (!IsModelEnabled(modelName))
        {
            return new List<BM25SearchResult>();
        }

        var ftsTableName = GetFtsTableName(modelName);

        // 使用 FTS5 的 bm25() 函数进行搜索
        // bm25() 返回负值，越小表示越相关
        // FTS5 bm25() 支持传入 k1、b 参数控制词频饱和度和文档长度归一化

        // 对查询进行分词，与索引时分词方式一致
        var queryTokens = Tokenize(query);

        // 如果没有有效的 token，返回空结果
        if (queryTokens.Count == 0)
        {
            return new List<BM25SearchResult>();
        }

        // 用双引号包裹每个 token，使其作为字面字符串搜索
        // 避免 FTS5 特殊字符（如 . / ? 等）被解释为语法
        var tokenizedQuery = string.Join(" OR ", queryTokens.Select(t => $"\"{t}\""));

        var sql = $@"
            SELECT
                m.chunk_id,
                f.content,
                bm25({ftsTableName}, {k1}, {b}) as score
            FROM {ftsTableName} f
            INNER JOIN bm25_chunk_mapping m ON f.rowid = m.fts_rowid
            WHERE {ftsTableName} MATCH @query
            ORDER BY bm25({ftsTableName}, {k1}, {b})
            LIMIT @topK";

        var results = new List<BM25SearchResult>();
        int rank = 1;

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@query", tokenizedQuery);
            cmd.Parameters.AddWithValue("@topK", topK);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new BM25SearchResult
                {
                    ChunkId = reader.GetString(0),
                    Content = reader.GetString(1),
                    Score = Math.Abs(reader.GetFloat(2)), // 取绝对值，因为 bm25() 返回负值
                    Rank = rank++
                });
            }
        }

        return results;
    }

    /// <inheritdoc />
    public bool IsModelEnabled(string name)
    {
        return _modelEnabledCache.TryGetValue(name, out var enabled) && enabled;
    }

    /// <inheritdoc />
    public bool ModelExists(string name)
    {
        return _modelInfoCache.ContainsKey(name);
    }

    #endregion

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _connection?.Close();
        _connection?.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }
}
