using Microsoft.Data.Sqlite;
using ObsidianRAG.Core.Interfaces;
using System.Text;
using System.Text.RegularExpressions;

namespace ObsidianRAG.Storage;

/// <summary>
/// SQLite BM25 存储实现 - 使用倒排索引支持多模型管理
/// </summary>
public class SqliteBM25Store : IBM25Store, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _dbPath;
    private bool _disposed;

    // 缓存模型启用状态
    private readonly Dictionary<string, bool> _modelEnabledCache = new();
    private readonly Dictionary<string, BM25ModelInfo> _modelInfoCache = new();

    // 停用词列表（与 BM25Searcher 保持一致）
    private static readonly HashSet<string> StopWords = new()
    {
        // 中文停用词
        "的", "是", "在", "了", "和", "与", "或", "也", "都", "就",
        "不", "有", "这", "那", "我", "你", "他", "她", "它",
        "个", "上", "下", "中", "来", "去", "说", "对", "要",
        // 英文停用词
        "the", "a", "an", "is", "are", "was", "were", "be", "been",
        "to", "of", "in", "for", "on", "with", "at", "by", "from",
        "and", "or", "but", "not", "this", "that", "it", "as"
    };

    public SqliteBM25Store(string dbPath)
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

        // 倒排索引表
        var createInvertedIndexTable = @"
            CREATE TABLE IF NOT EXISTS bm25_inverted_index (
                model_name TEXT,
                term TEXT,
                chunk_id TEXT,
                term_freq INTEGER,
                doc_length INTEGER,
                PRIMARY KEY (model_name, term, chunk_id)
            );
        ";
        ExecuteNonQuery(createInvertedIndexTable, transaction);

        // 文档长度表
        var createDocLengthTable = @"
            CREATE TABLE IF NOT EXISTS bm25_doc_length (
                model_name TEXT,
                chunk_id TEXT PRIMARY KEY,
                doc_length INTEGER
            );
        ";
        ExecuteNonQuery(createDocLengthTable, transaction);

        // 文档频率表
        var createDocFreqTable = @"
            CREATE TABLE IF NOT EXISTS bm25_doc_freq (
                model_name TEXT,
                term TEXT,
                doc_freq INTEGER,
                PRIMARY KEY (model_name, term)
            );
        ";
        ExecuteNonQuery(createDocFreqTable, transaction);

        // 创建索引以加速查询
        var createIndexes = @"
            CREATE INDEX IF NOT EXISTS idx_inverted_model_term ON bm25_inverted_index(model_name, term);
            CREATE INDEX IF NOT EXISTS idx_inverted_model_chunk ON bm25_inverted_index(model_name, chunk_id);
            CREATE INDEX IF NOT EXISTS idx_doc_length_model ON bm25_doc_length(model_name);
            CREATE INDEX IF NOT EXISTS idx_doc_freq_model ON bm25_doc_freq(model_name);
        ";
        ExecuteNonQuery(createIndexes, transaction);

        transaction.Commit();
    }

    /// <summary>
    /// 加载模型缓存
    /// </summary>
    private void LoadModelCache()
    {
        var sql = "SELECT name, k1, b, avg_doc_length, total_docs, vocab_size, is_enabled, created_at FROM bm25_models";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var info = new BM25ModelInfo
            {
                Name = reader.GetString(0),
                K1 = reader.GetFloat(1),
                B = reader.GetFloat(2),
                AverageDocLength = reader.GetFloat(3),
                TotalDocuments = reader.GetInt32(4),
                VocabularySize = reader.GetInt32(5),
                IsEnabled = reader.GetInt32(6) == 1,
                CreatedAt = DateTime.Parse(reader.GetString(7))
            };
            _modelInfoCache[info.Name] = info;
            _modelEnabledCache[info.Name] = info.IsEnabled;
        }
    }

    /// <summary>
    /// 分词 - 支持中英文混合
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

    private static bool IsChinese(char c)
    {
        return (c >= 0x4E00 && c <= 0x9FFF) ||
               (c >= 0x3400 && c <= 0x4DBF) ||
               (c >= 0xF900 && c <= 0xFAFF);
    }

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

    // ==================== 模型管理 ====================

    public async Task<BM25ModelInfo> CreateModelAsync(string name, float k1 = 1.5f, float b = 0.75f)
    {
        if (ModelExists(name))
        {
            throw new InvalidOperationException($"模型 '{name}' 已存在");
        }

        var createdAt = DateTime.UtcNow;
        var sql = @"
            INSERT INTO bm25_models (name, k1, b, avg_doc_length, total_docs, vocab_size, is_enabled, created_at)
            VALUES (@name, @k1, @b, 0, 0, 0, 1, @createdAt)
        ";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@k1", k1);
        command.Parameters.AddWithValue("@b", b);
        command.Parameters.AddWithValue("@createdAt", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync();

        var info = new BM25ModelInfo
        {
            Name = name,
            K1 = k1,
            B = b,
            AverageDocLength = 0,
            TotalDocuments = 0,
            VocabularySize = 0,
            IsEnabled = true,
            CreatedAt = createdAt
        };

        _modelInfoCache[name] = info;
        _modelEnabledCache[name] = true;

        return info;
    }

    public async Task<List<BM25ModelInfo>> GetAllModelsAsync()
    {
        var sql = "SELECT name, k1, b, avg_doc_length, total_docs, vocab_size, is_enabled, created_at FROM bm25_models";
        var models = new List<BM25ModelInfo>();

        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        using var reader = await command.ExecuteReaderAsync();
        while (reader.Read())
        {
            models.Add(new BM25ModelInfo
            {
                Name = reader.GetString(0),
                K1 = reader.GetFloat(1),
                B = reader.GetFloat(2),
                AverageDocLength = reader.GetFloat(3),
                TotalDocuments = reader.GetInt32(4),
                VocabularySize = reader.GetInt32(5),
                IsEnabled = reader.GetInt32(6) == 1,
                CreatedAt = DateTime.Parse(reader.GetString(7))
            });
        }

        return models;
    }

    public async Task<BM25ModelInfo?> GetModelInfoAsync(string name)
    {
        if (_modelInfoCache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var sql = "SELECT name, k1, b, avg_doc_length, total_docs, vocab_size, is_enabled, created_at FROM bm25_models WHERE name = @name";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@name", name);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var info = new BM25ModelInfo
            {
                Name = reader.GetString(0),
                K1 = reader.GetFloat(1),
                B = reader.GetFloat(2),
                AverageDocLength = reader.GetFloat(3),
                TotalDocuments = reader.GetInt32(4),
                VocabularySize = reader.GetInt32(5),
                IsEnabled = reader.GetInt32(6) == 1,
                CreatedAt = DateTime.Parse(reader.GetString(7))
            };
            _modelInfoCache[name] = info;
            return info;
        }

        return null;
    }

    public async Task DeleteModelAsync(string name)
    {
        using var transaction = _connection.BeginTransaction();

        try
        {
            // 删除倒排索引
            var deleteIndex = "DELETE FROM bm25_inverted_index WHERE model_name = @name";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = deleteIndex;
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            // 删除文档长度
            var deleteDocLength = "DELETE FROM bm25_doc_length WHERE model_name = @name";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = deleteDocLength;
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            // 删除文档频率
            var deleteDocFreq = "DELETE FROM bm25_doc_freq WHERE model_name = @name";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = deleteDocFreq;
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            // 删除模型
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

    public async Task EnableModelAsync(string name)
    {
        var sql = "UPDATE bm25_models SET is_enabled = 1 WHERE name = @name";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@name", name);
        await command.ExecuteNonQueryAsync();

        _modelEnabledCache[name] = true;
        if (_modelInfoCache.TryGetValue(name, out var info))
        {
            info.IsEnabled = true;
        }
    }

    public async Task DisableModelAsync(string name)
    {
        var sql = "UPDATE bm25_models SET is_enabled = 0 WHERE name = @name";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@name", name);
        await command.ExecuteNonQueryAsync();

        _modelEnabledCache[name] = false;
        if (_modelInfoCache.TryGetValue(name, out var info))
        {
            info.IsEnabled = false;
        }
    }

    // ==================== 索引操作 ====================

    public async Task IndexDocumentAsync(string modelName, string chunkId, string content)
    {
        if (!ModelExists(modelName))
        {
            throw new InvalidOperationException($"模型 '{modelName}' 不存在");
        }

        var tokens = Tokenize(content);
        var docLength = tokens.Count;

        // 构建词频字典
        var termFreq = new Dictionary<string, int>();
        foreach (var token in tokens)
        {
            termFreq.TryGetValue(token, out var count);
            termFreq[token] = count + 1;
        }

        using var transaction = _connection.BeginTransaction();

        try
        {
            // 更新文档长度
            var upsertDocLength = @"
                INSERT OR REPLACE INTO bm25_doc_length (model_name, chunk_id, doc_length)
                VALUES (@modelName, @chunkId, @docLength)
            ";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = upsertDocLength;
                cmd.Parameters.AddWithValue("@modelName", modelName);
                cmd.Parameters.AddWithValue("@chunkId", chunkId);
                cmd.Parameters.AddWithValue("@docLength", docLength);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            // 先删除旧term记录
            var deleteOldTerms = "DELETE FROM bm25_inverted_index WHERE model_name = @modelName AND chunk_id = @chunkId";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = deleteOldTerms;
                cmd.Parameters.AddWithValue("@modelName", modelName);
                cmd.Parameters.AddWithValue("@chunkId", chunkId);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            // 插入新的term记录
            foreach (var (term, freq) in termFreq)
            {
                var insertTerm = @"
                    INSERT OR REPLACE INTO bm25_inverted_index (model_name, term, chunk_id, term_freq, doc_length)
                    VALUES (@modelName, @term, @chunkId, @termFreq, @docLength)
                ";
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = insertTerm;
                    cmd.Parameters.AddWithValue("@modelName", modelName);
                    cmd.Parameters.AddWithValue("@term", term);
                    cmd.Parameters.AddWithValue("@chunkId", chunkId);
                    cmd.Parameters.AddWithValue("@termFreq", freq);
                    cmd.Parameters.AddWithValue("@docLength", docLength);
                    cmd.Transaction = transaction;
                    await cmd.ExecuteNonQueryAsync();
                }

                // 更新文档频率
                var updateDocFreq = @"
                    INSERT INTO bm25_doc_freq (model_name, term, doc_freq)
                    VALUES (@modelName, @term, 1)
                    ON CONFLICT(model_name, term) DO UPDATE SET doc_freq = doc_freq + 1
                ";
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = updateDocFreq;
                    cmd.Parameters.AddWithValue("@modelName", modelName);
                    cmd.Parameters.AddWithValue("@term", term);
                    cmd.Transaction = transaction;
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            transaction.Commit();

            // 更新模型统计
            await UpdateModelStatsAsync(modelName);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task IndexBatchAsync(string modelName, IEnumerable<(string chunkId, string content)> documents, IProgress<int>? progress = null)
    {
        if (!ModelExists(modelName))
        {
            throw new InvalidOperationException($"模型 '{modelName}' 不存在");
        }

        var docList = documents.ToList();
        var total = docList.Count;
        var processed = 0;
        const int batchSize = 100;

        for (int i = 0; i < docList.Count; i += batchSize)
        {
            var batch = docList.Skip(i).Take(batchSize).ToList();

            using var transaction = _connection.BeginTransaction();

            try
            {
                foreach (var (chunkId, content) in batch)
                {
                    var tokens = Tokenize(content);
                    var docLength = tokens.Count;

                    var termFreq = new Dictionary<string, int>();
                    foreach (var token in tokens)
                    {
                        termFreq.TryGetValue(token, out var count);
                        termFreq[token] = count + 1;
                    }

                    // 更新文档长度
                    var upsertDocLength = @"
                        INSERT OR REPLACE INTO bm25_doc_length (model_name, chunk_id, doc_length)
                        VALUES (@modelName, @chunkId, @docLength)
                    ";
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = upsertDocLength;
                        cmd.Parameters.AddWithValue("@modelName", modelName);
                        cmd.Parameters.AddWithValue("@chunkId", chunkId);
                        cmd.Parameters.AddWithValue("@docLength", docLength);
                        cmd.Transaction = transaction;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // 先删除旧term记录
                    var deleteOldTerms = "DELETE FROM bm25_inverted_index WHERE model_name = @modelName AND chunk_id = @chunkId";
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.CommandText = deleteOldTerms;
                        cmd.Parameters.AddWithValue("@modelName", modelName);
                        cmd.Parameters.AddWithValue("@chunkId", chunkId);
                        cmd.Transaction = transaction;
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // 插入新的term记录
                    foreach (var (term, freq) in termFreq)
                    {
                        var insertTerm = @"
                            INSERT OR REPLACE INTO bm25_inverted_index (model_name, term, chunk_id, term_freq, doc_length)
                            VALUES (@modelName, @term, @chunkId, @termFreq, @docLength)
                        ";
                        using (var cmd = _connection.CreateCommand())
                        {
                            cmd.CommandText = insertTerm;
                            cmd.Parameters.AddWithValue("@modelName", modelName);
                            cmd.Parameters.AddWithValue("@term", term);
                            cmd.Parameters.AddWithValue("@chunkId", chunkId);
                            cmd.Parameters.AddWithValue("@termFreq", freq);
                            cmd.Parameters.AddWithValue("@docLength", docLength);
                            cmd.Transaction = transaction;
                            await cmd.ExecuteNonQueryAsync();
                        }

                        var updateDocFreq = @"
                            INSERT INTO bm25_doc_freq (model_name, term, doc_freq)
                            VALUES (@modelName, @term, 1)
                            ON CONFLICT(model_name, term) DO UPDATE SET doc_freq = doc_freq + 1
                        ";
                        using (var cmd = _connection.CreateCommand())
                        {
                            cmd.CommandText = updateDocFreq;
                            cmd.Parameters.AddWithValue("@modelName", modelName);
                            cmd.Parameters.AddWithValue("@term", term);
                            cmd.Transaction = transaction;
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }

                    processed++;
                }

                transaction.Commit();

                progress?.Report((int)(processed * 100.0 / total));

                // 每批后更新统计
                await UpdateModelStatsAsync(modelName);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

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

    public async Task ClearModelAsync(string modelName)
    {
        using var transaction = _connection.BeginTransaction();

        try
        {
            var deleteIndex = "DELETE FROM bm25_inverted_index WHERE model_name = @modelName";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = deleteIndex;
                cmd.Parameters.AddWithValue("@modelName", modelName);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            var deleteDocLength = "DELETE FROM bm25_doc_length WHERE model_name = @modelName";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = deleteDocLength;
                cmd.Parameters.AddWithValue("@modelName", modelName);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            var deleteDocFreq = "DELETE FROM bm25_doc_freq WHERE model_name = @modelName";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = deleteDocFreq;
                cmd.Parameters.AddWithValue("@modelName", modelName);
                cmd.Transaction = transaction;
                await cmd.ExecuteNonQueryAsync();
            }

            transaction.Commit();

            // 重置模型统计
            var resetStats = @"
                UPDATE bm25_models
                SET avg_doc_length = 0, total_docs = 0, vocab_size = 0
                WHERE name = @name
            ";
            using var command = _connection.CreateCommand();
            command.CommandText = resetStats;
            command.Parameters.AddWithValue("@name", modelName);
            await command.ExecuteNonQueryAsync();

            if (_modelInfoCache.TryGetValue(modelName, out var info))
            {
                info.AverageDocLength = 0;
                info.TotalDocuments = 0;
                info.VocabularySize = 0;
            }
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    // ==================== 搜索操作 ====================

    public async Task<List<BM25SearchResult>> SearchAsync(string modelName, string query, int topK = 10)
    {
        if (!ModelExists(modelName))
        {
            throw new InvalidOperationException($"模型 '{modelName}' 不存在");
        }

        if (!IsModelEnabled(modelName))
        {
            return new List<BM25SearchResult>();
        }

        var modelInfo = await GetModelInfoAsync(modelName);
        if (modelInfo == null || modelInfo.TotalDocuments == 0)
        {
            return new List<BM25SearchResult>();
        }

        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0)
        {
            return new List<BM25SearchResult>();
        }

        var k1 = modelInfo.K1;
        var b = modelInfo.B;
        var avgDocLength = modelInfo.AverageDocLength;
        var totalDocs = modelInfo.TotalDocuments;

        // 获取所有相关文档的分数
        var scores = new Dictionary<string, double>();

        foreach (var term in queryTokens.Distinct())
        {
            // 获取文档频率
            var df = 0;
            var dfSql = "SELECT doc_freq FROM bm25_doc_freq WHERE model_name = @modelName AND term = @term";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = dfSql;
                cmd.Parameters.AddWithValue("@modelName", modelName);
                cmd.Parameters.AddWithValue("@term", term);
                var result = await cmd.ExecuteScalarAsync();
                df = result != null ? Convert.ToInt32(result) : 0;
            }

            if (df == 0) continue;

            // 计算 IDF
            var idf = Math.Log((totalDocs - df + 0.5) / (df + 0.5) + 1);

            // 获取包含该 term 的所有文档
            var docsSql = @"
                SELECT chunk_id, term_freq, doc_length
                FROM bm25_inverted_index
                WHERE model_name = @modelName AND term = @term
            ";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = docsSql;
                cmd.Parameters.AddWithValue("@modelName", modelName);
                cmd.Parameters.AddWithValue("@term", term);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var chunkId = reader.GetString(0);
                    var tf = reader.GetInt32(1);
                    var docLength = reader.GetInt32(2);

                    // 计算 BM25 分数
                    var numerator = tf * (k1 + 1);
                    var denominator = tf + k1 * (1 - b + b * docLength / avgDocLength);
                    var bm25Score = idf * numerator / denominator;

                    if (!scores.ContainsKey(chunkId))
                    {
                        scores[chunkId] = 0;
                    }
                    scores[chunkId] += bm25Score;
                }
            }
        }

        // 排序并返回 topK 结果
        var results = scores
            .OrderByDescending(s => s.Value)
            .Take(topK)
            .Select((s, index) => new BM25SearchResult
            {
                ChunkId = s.Key,
                Score = s.Value,
                Rank = index + 1
            })
            .ToList();

        // 获取文档内容
        foreach (var result in results)
        {
            var contentSql = "SELECT content FROM chunks WHERE id = @chunkId";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = contentSql;
                cmd.Parameters.AddWithValue("@chunkId", result.ChunkId);
                var content = await cmd.ExecuteScalarAsync();
                result.Content = content?.ToString() ?? string.Empty;
            }
        }

        return results;
    }

    // ==================== 状态查询 ====================

    public bool IsModelEnabled(string name)
    {
        return _modelEnabledCache.TryGetValue(name, out var enabled) && enabled;
    }

    public bool ModelExists(string name)
    {
        return _modelInfoCache.ContainsKey(name);
    }

    // ==================== 辅助方法 ====================

    /// <summary>
    /// 更新模型统计信息
    /// </summary>
    private async Task UpdateModelStatsAsync(string modelName)
    {
        // 计算文档总数
        var countSql = "SELECT COUNT(*) FROM bm25_doc_length WHERE model_name = @modelName";
        long totalDocs;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = countSql;
            cmd.Parameters.AddWithValue("@modelName", modelName);
            totalDocs = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        if (totalDocs == 0)
        {
            return;
        }

        // 计算平均文档长度
        var avgSql = "SELECT AVG(doc_length) FROM bm25_doc_length WHERE model_name = @modelName";
        double avgDocLength;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = avgSql;
            cmd.Parameters.AddWithValue("@modelName", modelName);
            var result = await cmd.ExecuteScalarAsync();
            avgDocLength = result != null ? Convert.ToDouble(result) : 0;
        }

        // 计算词表大小
        var vocabSql = "SELECT COUNT(DISTINCT term) FROM bm25_doc_freq WHERE model_name = @modelName";
        int vocabSize;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = vocabSql;
            cmd.Parameters.AddWithValue("@modelName", modelName);
            vocabSize = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        // 更新统计
        var updateSql = @"
            UPDATE bm25_models
            SET avg_doc_length = @avgDocLength,
                total_docs = @totalDocs,
                vocab_size = @vocabSize
            WHERE name = @name
        ";
        using var command = _connection.CreateCommand();
        command.CommandText = updateSql;
        command.Parameters.AddWithValue("@avgDocLength", avgDocLength);
        command.Parameters.AddWithValue("@totalDocs", totalDocs);
        command.Parameters.AddWithValue("@vocabSize", vocabSize);
        command.Parameters.AddWithValue("@name", modelName);
        await command.ExecuteNonQueryAsync();

        // 更新缓存
        if (_modelInfoCache.TryGetValue(modelName, out var info))
        {
            info.AverageDocLength = avgDocLength;
            info.TotalDocuments = (int)totalDocs;
            info.VocabularySize = vocabSize;
        }
    }

    private void ExecuteNonQuery(string sql, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Close();
            _connection.Dispose();
            _disposed = true;
        }
    }
}
