using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ReferenceRAG.Storage;

/// <summary>
/// SQLite 向量存储 - 使用 sqlite-vec 扩展，按模型分表实现向量索引
/// 
/// 架构说明：
/// - 主表（files, chunks）：存储文件和分段元数据
/// - 向量子表（vec_{model}）：每个模型一个 vec0 虚拟表，拥有独立的向量索引
/// - 模型元数据表（models）：记录每个模型的维度和统计信息
/// </summary>
public class SqliteVectorStore : IVectorStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _dbPath;
    private readonly ILogger<SqliteVectorStore>? _logger;
    private bool _disposed;

    // 缓存已创建的向量表维度
    private readonly Dictionary<string, int> _modelDimensions = new();

    // 用于序列化写事务的锁 (SQLite不支持同一连接的并行事务)
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteVectorStore(string dbPath, int dimension = 384, ILogger<SqliteVectorStore>? logger = null)
    {
        _dbPath = dbPath;
        _logger = logger;

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        _connection = new SqliteConnection(builder.ConnectionString);
        _connection.Open();

        // SQLite 性能优化
        OptimizeSqlitePerformance();

        // 加载 sqlite-vec 扩展
        LoadVectorExtension();

        InitializeDatabase();
        MigrateLegacyData();
        LoadModelDimensions(dimension);
    }

    /// <summary>
    /// 加载 sqlite-vec 扩展
    /// </summary>
    private void LoadVectorExtension()
    {
        // 查找扩展文件路径
        var extensionPath = FindExtensionPath();
        if (extensionPath == null)
        {
            throw new InvalidOperationException(
                "无法找到 sqlite-vec 扩展文件 (vec0.dll)。请确保 NuGet 包 SQLitePCLRaw.bundle_e_sqlitevec 已正确安装。");
        }

        // 加载扩展
        _connection.LoadExtension(extensionPath);
    }

    /// <summary>
    /// SQLite 性能优化配置
    /// </summary>
    private void OptimizeSqlitePerformance()
    {
        // WAL 模式：允许并发读，写操作更高效
        ExecutePragma("PRAGMA journal_mode = WAL;");

        // 关闭同步：提高写入性能（牺牲一定数据安全性，但 WAL 模式下仍可靠）
        ExecutePragma("PRAGMA synchronous = NORMAL;");

        // 增大缓存：使用更多内存提升性能
        ExecutePragma("PRAGMA cache_size = -64000;"); // 64MB

        // 临时表使用内存
        ExecutePragma("PRAGMA temp_store = MEMORY;");

        // 启用内存映射，减少 I/O
        ExecutePragma("PRAGMA mmap_size = 268435456;"); // 256MB

        // 增大页面大小
        ExecutePragma("PRAGMA page_size = 4096;");

        // 启用 WAL 自动检查点
        ExecutePragma("PRAGMA wal_autocheckpoint = 1000;");
    }

    private void ExecutePragma(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 查找 sqlite-vec 扩展文件路径
    /// </summary>
    private static string? FindExtensionPath()
    {
        // 获取应用程序基目录
        var baseDir = AppContext.BaseDirectory;

        // 根据操作系统确定扩展文件名
        var extensionName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "vec0.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "vec0.dylib"  // macOS 上也是 vec0.dylib 而不是 libvec0.dylib
                : "vec0.so";    // Linux 上是 vec0.so 而不是 libvec0.so

        // 可能的搜索路径
        var searchPaths = new[]
        {
            // NuGet 包安装路径（run NativeLibrary 应该能找到）
            Path.Combine(baseDir, "runtimes", GetRuntimeIdentifier(), "native", extensionName),
            // 直接在基目录
            Path.Combine(baseDir, extensionName),
            // 相对于执行程序集
            GetAssemblyRelativePath(extensionName)
        };

        foreach (var path in searchPaths)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return path;
            }
        }

        // 尝试通过 NativeLibrary 加载（如果已加载则返回句柄）
        if (NativeLibrary.TryLoad(extensionName, out var handle))
        {
            // 扩展已加载，返回空表示使用已加载的模块
            NativeLibrary.Free(handle);
            return extensionName;
        }

        return null;
    }

    private static string GetRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "win-arm64"
                : "win-x64";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "osx-arm64"
                : "osx-x64";
        }
        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "linux-arm64"
            : "linux-x64";
    }

    private static string? GetAssemblyRelativePath(string extensionName)
    {
        try
        {
            var assemblyLocation = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(assemblyLocation))
                return null;

            var assemblyDir = Path.GetDirectoryName(assemblyLocation);
            if (string.IsNullOrEmpty(assemblyDir))
                return null;

            return Path.Combine(assemblyDir, "runtimes", GetRuntimeIdentifier(), "native", extensionName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 初始化数据库表
    /// </summary>
    private void InitializeDatabase()
    {
        using var transaction = _connection.BeginTransaction();

        // 文件表
        var createFilesTable = @"
            CREATE TABLE IF NOT EXISTS files (
                id TEXT PRIMARY KEY,
                path TEXT NOT NULL UNIQUE,
                file_name TEXT DEFAULT '',
                title TEXT,
                content_hash TEXT,
                content_length INTEGER DEFAULT 0,
                tags TEXT,
                parent_folder TEXT,
                source TEXT DEFAULT '',
                chunk_count INTEGER DEFAULT 0,
                total_tokens INTEGER DEFAULT 0,
                created_at TEXT,
                updated_at TEXT,
                indexed_at TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_files_path ON files(path);
            CREATE INDEX IF NOT EXISTS idx_files_hash ON files(content_hash);
        ";

        ExecuteNonQuery(createFilesTable, transaction);

        // 分段表
        var createChunksTable = @"
            CREATE TABLE IF NOT EXISTS chunks (
                id TEXT PRIMARY KEY,
                file_id TEXT NOT NULL,
                chunk_index INTEGER NOT NULL,
                content TEXT NOT NULL,
                token_count INTEGER,
                start_line INTEGER,
                end_line INTEGER,
                start_column INTEGER,
                end_column INTEGER,
                heading_path TEXT,
                level INTEGER,
                weight REAL,
                chunk_type INTEGER,
                aggregate_type INTEGER,
                child_chunk_count INTEGER,
                FOREIGN KEY (file_id) REFERENCES files(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_chunks_file ON chunks(file_id);
            CREATE INDEX IF NOT EXISTS idx_chunks_aggregate ON chunks(aggregate_type);
        ";

        ExecuteNonQuery(createChunksTable, transaction);

        // 模型元数据表（记录每个模型的维度）
        var createModelsTable = @"
            CREATE TABLE IF NOT EXISTS models (
                name TEXT PRIMARY KEY,
                dimension INTEGER NOT NULL,
                vector_count INTEGER DEFAULT 0,
                created_at TEXT,
                updated_at TEXT
            );
        ";

        ExecuteNonQuery(createModelsTable, transaction);

        // 向后兼容：为已有 files 表添加缺失的列
        var fileMigrations = new[]
        {
            "ALTER TABLE files ADD COLUMN file_name TEXT DEFAULT ''",
            "ALTER TABLE files ADD COLUMN content_length INTEGER DEFAULT 0",
            "ALTER TABLE files ADD COLUMN source TEXT DEFAULT ''",
            "ALTER TABLE files ADD COLUMN chunk_count INTEGER DEFAULT 0",
            "ALTER TABLE files ADD COLUMN total_tokens INTEGER DEFAULT 0",
        };
        foreach (var migration in fileMigrations)
        {
            try { ExecuteNonQuery(migration, transaction); }
            catch { /* 列已存在，忽略 */ }
        }

        // 向后兼容：为已有 chunks 表添加 chunk_order 和 content_hash 字段
        var chunkMigrations = new[]
        {
            "ALTER TABLE chunks ADD COLUMN chunk_order REAL DEFAULT 1.0",
            "ALTER TABLE chunks ADD COLUMN content_hash TEXT DEFAULT ''"
        };
        foreach (var migration in chunkMigrations)
        {
            try { ExecuteNonQuery(migration, transaction); }
            catch { /* 列已存在，忽略 */ }
        }

        transaction.Commit();

        // 性能优化：WAL模式和数据库索引
        EnsurePerformanceOptimizations();
    }

    /// <summary>
    /// 性能优化：WAL模式和数据库索引
    /// </summary>
    private void EnsurePerformanceOptimizations()
    {
        // WAL 模式（提升并发读写性能）
        ExecutePragma("PRAGMA journal_mode=WAL");
        ExecutePragma("PRAGMA synchronous=NORMAL");
        ExecutePragma("PRAGMA cache_size=-64000");

        // 索引（如不存在则创建，幂等操作）
        var indexes = new[]
        {
            "CREATE INDEX IF NOT EXISTS idx_chunks_order ON chunks(chunk_order)",
            "CREATE INDEX IF NOT EXISTS idx_chunks_hash ON chunks(content_hash)",
            "CREATE INDEX IF NOT EXISTS idx_files_source_time ON files(source, indexed_at DESC)",
            "CREATE INDEX IF NOT EXISTS idx_files_hash ON files(content_hash)"
        };
        foreach (var idx in indexes)
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = idx;
                cmd.ExecuteNonQuery();
            }
            catch { /* 已存在，忽略 */ }
    }

    /// <summary>
    /// 加载已有模型的维度信息
    /// </summary>
    private void LoadModelDimensions(int defaultDimension = 384)
    {
        var sql = "SELECT name, dimension FROM models";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var modelName = reader.GetString(0);
            var dimension = reader.GetInt32(1);
            _modelDimensions[modelName] = dimension;
        }

        // 如果没有找到任何模型，添加默认模型
        if (_modelDimensions.Count == 0)
        {
            _modelDimensions["default"] = defaultDimension;
        }
    }

    /// <summary>
    /// 迁移旧版数据（Semantic Kernel SqliteVec 结构）
    /// </summary>
    private void MigrateLegacyData()
    {
        // 检查是否存在旧的 document_chunks 表
        var checkOldTable = "SELECT name FROM sqlite_master WHERE type='table' AND name='document_chunks'";
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = checkOldTable;
            var exists = cmd.ExecuteScalar() != null;
            if (!exists) return; // 没有旧数据需要迁移
        }

        _logger?.LogInformation("检测到旧版数据结构，开始迁移...");

        try
        {
            // 1. 检查旧向量表是否存在并获取维度
            var oldVecTableExists = false;
            var oldDimension = 384; // 默认维度
            var checkOldVec = "SELECT name FROM sqlite_master WHERE type='table' AND name='vec_document_chunks'";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = checkOldVec;
                oldVecTableExists = cmd.ExecuteScalar() != null;
            }

            // 2. 迁移分段数据 document_chunks -> chunks
            var migrateChunks = @"
                INSERT OR IGNORE INTO chunks 
                (id, file_id, chunk_index, content, token_count, start_line, end_line, 
                 start_column, end_column, heading_path, level, weight, chunk_type, 
                 aggregate_type, child_chunk_count)
                SELECT Key, FileId, ChunkIndex, Content, TokenCount, StartLine, EndLine,
                       StartColumn, EndColumn, HeadingPath, Level, Weight, ChunkType,
                       AggregateType, ChildChunkCount
                FROM document_chunks
            ";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = migrateChunks;
                var rows = cmd.ExecuteNonQuery();
                _logger?.LogInformation("迁移了 {Rows} 条分段记录", rows);
            }

            // 3. 注册默认模型（使用旧向量表的维度）
            const string defaultModel = "default";
            var registerModel = @"
                INSERT OR IGNORE INTO models (name, dimension, created_at, updated_at)
                VALUES (@name, @dimension, @createdAt, @updatedAt)
            ";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = registerModel;
                cmd.Parameters.AddWithValue("@name", defaultModel);
                cmd.Parameters.AddWithValue("@dimension", oldDimension);
                cmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            // 4. 创建新的向量表并迁移向量数据
            if (oldVecTableExists)
            {
                var newTableName = ModelToTableName(defaultModel);
                
                // 创建新向量表
                var createNewVec = $@"
                    CREATE VIRTUAL TABLE IF NOT EXISTS {newTableName} USING vec0(
                        chunk_id TEXT PRIMARY KEY,
                        embedding FLOAT[{oldDimension}]
                    );
                ";
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = createNewVec;
                    cmd.ExecuteNonQuery();
                }

                // 迁移向量数据（vec_document_chunks -> vec_default）
                var migrateVectors = $@"
                    INSERT OR IGNORE INTO {newTableName} (chunk_id, embedding)
                    SELECT Key, ContentEmbedding FROM vec_document_chunks
                    WHERE ContentEmbedding IS NOT NULL
                ";
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = migrateVectors;
                    var rows = cmd.ExecuteNonQuery();
                    _logger?.LogInformation("迁移了 {Rows} 条向量记录", rows);
                }
            }

            // 5. 更新模型统计
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE models SET updated_at = @updatedAt WHERE name = 'default'";
                cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            // 6. 重命名旧表（保留备份，不删除）
            try
            {
                using var transaction = _connection.BeginTransaction();
                ExecuteNonQuery("ALTER TABLE document_chunks RENAME TO _legacy_document_chunks", transaction);
                if (oldVecTableExists)
                {
                    ExecuteNonQuery("ALTER TABLE vec_document_chunks RENAME TO _legacy_vec_document_chunks", transaction);
                }
                transaction.Commit();
                _logger?.LogInformation("旧表已重命名为 _legacy_* 前缀保留");
            }
            catch
            {
                // 忽略重命名错误
            }

            _logger?.LogInformation("数据迁移完成");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据迁移失败: {Message}", ex.Message);
            // 不抛出异常，允许继续使用
        }
    }

    /// <summary>
    /// 将模型名转换为合法的表名
    /// </summary>
    private static string ModelToTableName(string modelName)
    {
        // 替换不合法的字符为下划线
        var tableName = new string(modelName.Select(c =>
            char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return $"vec_{tableName}";
    }

    /// <summary>
    /// 确保模型的向量表存在
    /// </summary>
    private void EnsureModelTableExists(string modelName, int dimension)
    {
        if (_modelDimensions.TryGetValue(modelName, out var existingDim))
        {
            if (existingDim != dimension)
            {
                throw new InvalidOperationException(
                    $"模型 '{modelName}' 已存在，维度为 {existingDim}，但传入维度为 {dimension}。" +
                    $"请先删除旧向量后再切换。");
            }
            return; // 表已存在且维度匹配
        }

        var tableName = ModelToTableName(modelName);

        // 创建 vec0 虚拟表（sqlite-vec 向量索引）
        var sql = $@"
            CREATE VIRTUAL TABLE IF NOT EXISTS {tableName} USING vec0(
                chunk_id TEXT PRIMARY KEY,
                embedding FLOAT[{dimension}]
            );
        ";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();

        // 记录模型元数据
        var insertModel = @"
            INSERT OR REPLACE INTO models (name, dimension, created_at, updated_at)
            VALUES (@name, @dimension, @createdAt, @updatedAt)
        ";
        using var insertCmd = _connection.CreateCommand();
        insertCmd.CommandText = insertModel;
        insertCmd.Parameters.AddWithValue("@name", modelName);
        insertCmd.Parameters.AddWithValue("@dimension", dimension);
        insertCmd.Parameters.AddWithValue("@createdAt", DateTime.UtcNow.ToString("O"));
        insertCmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O"));
        insertCmd.ExecuteNonQuery();

        _modelDimensions[modelName] = dimension;
    }

    /// <summary>
    /// 获取模型维度
    /// </summary>
    public int? GetModelDimension(string modelName)
    {
        return _modelDimensions.TryGetValue(modelName, out var dim) ? dim : null;
    }

    /// <summary>
    /// 获取所有已注册的模型
    /// </summary>
    public IReadOnlyDictionary<string, int> GetRegisteredModels()
    {
        return _modelDimensions;
    }

    // ==================== 文件操作 ====================

    public async Task UpsertFileAsync(FileRecord file, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var sql = @"
                INSERT OR REPLACE INTO files
                (id, path, file_name, title, content_hash, content_length, tags, parent_folder, source, chunk_count, total_tokens, created_at, updated_at, indexed_at)
                VALUES
                (@id, @path, @fileName, @title, @contentHash, @contentLength, @tags, @parentFolder, @source, @chunkCount, @totalTokens, @createdAt, @updatedAt, @indexedAt)
            ";

            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "@id", file.Id);
            AddParameter(command, "@path", file.Path);
            AddParameter(command, "@fileName", file.FileName ?? "");
            AddParameter(command, "@title", file.Title ?? "");
            AddParameter(command, "@contentHash", file.ContentHash ?? "");
            AddParameter(command, "@contentLength", file.ContentLength);
            AddParameter(command, "@tags", file.Tags != null ? JsonSerializer.Serialize(file.Tags) : "[]");
            AddParameter(command, "@parentFolder", file.ParentFolder ?? "");
            AddParameter(command, "@source", file.Source ?? "");
            AddParameter(command, "@chunkCount", file.ChunkCount);
            AddParameter(command, "@totalTokens", file.TotalTokens);
            AddParameter(command, "@createdAt", file.CreatedAt?.ToString("O") ?? "");
            AddParameter(command, "@updatedAt", file.ModifiedAt?.ToString("O") ?? "");
            AddParameter(command, "@indexedAt", file.IndexedAt.ToString("O"));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<FileRecord?> GetFileAsync(string id, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM files WHERE id = @id";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadFileRecord(reader);
        }

        return null;
    }

    public async Task<FileRecord?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM files WHERE path = @path";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@path", path);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadFileRecord(reader);
        }

        return null;
    }

    public async Task<FileRecord?> GetFileByHashAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM files WHERE content_hash = @hash";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@hash", contentHash);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadFileRecord(reader);
        }

        return null;
    }

    public async Task DeleteFileAsync(string id, CancellationToken cancellationToken = default)
    {
        // 删除关联的分段（会级联删除向量）
        await DeleteChunksByFileAsync(id, cancellationToken);

        var sql = "DELETE FROM files WHERE id = @id";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@id", id);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IEnumerable<FileRecord>> GetAllFilesAsync(CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM files ORDER BY path";
        var files = new List<FileRecord>();

        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(ReadFileRecord(reader));
        }

        return files;
    }

    public async Task<IAsyncEnumerable<FileRecord>> StreamAllFilesAsync(CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM files ORDER BY path";
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        // Return an async enumerable that manages the reader and command lifetime
        var asyncEnumerable = ReadAllFilesAsync(reader, cmd, cancellationToken);
        var enumerator = asyncEnumerable.GetAsyncEnumerator(cancellationToken);
        return new DisposingAsyncEnumerable<FileRecord>(
            enumerator: enumerator,
            disposeAction: () =>
            {
                reader?.Dispose();
                cmd?.Dispose();
            });
    }

    private async IAsyncEnumerable<FileRecord> ReadAllFilesAsync(
        SqliteDataReader reader,
        SqliteCommand cmd,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                yield return ReadFileRecord(reader);
            }
        }
        finally
        {
            // Cleanup happens in the DisposingAsyncEnumerable
        }
    }

    // ==================== 分段操作 ====================

    public async Task UpsertChunkAsync(ChunkRecord chunk, CancellationToken cancellationToken = default)
    {
        var sql = @"
            INSERT OR REPLACE INTO chunks
            (id, file_id, chunk_index, content, token_count, start_line, end_line,
             start_column, end_column, heading_path, level, weight, chunk_type,
             aggregate_type, child_chunk_count, chunk_order, content_hash)
            VALUES
            (@id, @fileId, @chunkIndex, @content, @tokenCount, @startLine, @endLine,
             @startColumn, @endColumn, @headingPath, @level, @weight, @chunkType,
             @aggregateType, @childChunkCount, @chunkOrder, @contentHash)
        ";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@id", chunk.Id);
        AddParameter(command, "@fileId", chunk.FileId);
        AddParameter(command, "@chunkIndex", chunk.ChunkIndex);
        AddParameter(command, "@content", chunk.Content);
        AddParameter(command, "@tokenCount", chunk.TokenCount);
        AddParameter(command, "@startLine", chunk.StartLine);
        AddParameter(command, "@endLine", chunk.EndLine);
        AddParameter(command, "@startColumn", chunk.StartColumn);
        AddParameter(command, "@endColumn", chunk.EndColumn);
        AddParameter(command, "@headingPath", chunk.HeadingPath ?? "");
        AddParameter(command, "@level", chunk.Level);
        AddParameter(command, "@weight", chunk.Weight);
        AddParameter(command, "@chunkType", (int)chunk.ChunkType);
        AddParameter(command, "@aggregateType", (int)chunk.AggregateType);
        AddParameter(command, "@childChunkCount", chunk.ChildChunkCount);
        AddParameter(command, "@chunkOrder", chunk.ChunkOrder);
        AddParameter(command, "@contentHash", chunk.ContentHash ?? "");

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertChunksAsync(IEnumerable<ChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        var chunkList = chunks.ToList();
        if (chunkList.Count == 0) return;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            const int batchSize = 500;  // 每500条一个事务，提高写入吞吐量
            for (int i = 0; i < chunkList.Count; i += batchSize)
            {
                var batch = chunkList.Skip(i).Take(batchSize).ToList();
                await UpsertChunkBatchAsync(batch, cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task UpsertChunkBatchAsync(List<ChunkRecord> batch, CancellationToken cancellationToken)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            // 预处理语句 - SQL只编译一次，复用command对象提高性能
            var sql = @"
                INSERT OR REPLACE INTO chunks
                (id, file_id, chunk_index, content, token_count, start_line, end_line,
                 start_column, end_column, heading_path, level, weight, chunk_type,
                 aggregate_type, child_chunk_count, chunk_order, content_hash)
                VALUES
                (@id, @fileId, @chunkIndex, @content, @tokenCount, @startLine, @endLine,
                 @startColumn, @endColumn, @headingPath, @level, @weight, @chunkType,
                 @aggregateType, @childChunkCount, @chunkOrder, @contentHash)
            ";

            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;

            foreach (var chunk in batch)
            {
                command.Parameters.Clear();
                AddParameter(command, "@id", chunk.Id);
                AddParameter(command, "@fileId", chunk.FileId);
                AddParameter(command, "@chunkIndex", chunk.ChunkIndex);
                AddParameter(command, "@content", chunk.Content);
                AddParameter(command, "@tokenCount", chunk.TokenCount);
                AddParameter(command, "@startLine", chunk.StartLine);
                AddParameter(command, "@endLine", chunk.EndLine);
                AddParameter(command, "@startColumn", chunk.StartColumn);
                AddParameter(command, "@endColumn", chunk.EndColumn);
                AddParameter(command, "@headingPath", chunk.HeadingPath ?? "");
                AddParameter(command, "@level", chunk.Level);
                AddParameter(command, "@weight", chunk.Weight);
                AddParameter(command, "@chunkType", (int)chunk.ChunkType);
                AddParameter(command, "@aggregateType", (int)chunk.AggregateType);
                AddParameter(command, "@childChunkCount", chunk.ChildChunkCount);
                AddParameter(command, "@chunkOrder", chunk.ChunkOrder);
                AddParameter(command, "@contentHash", chunk.ContentHash ?? "");
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM chunks WHERE id = @id";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadChunkRecord(reader);
        }

        return null;
    }

    public async Task<IEnumerable<ChunkRecord>> GetChunksByFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM chunks WHERE file_id = @fileId ORDER BY chunk_index";
        var chunks = new List<ChunkRecord>();

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@fileId", fileId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            chunks.Add(ReadChunkRecord(reader));
        }

        return chunks;
    }

    public async Task DeleteChunkAsync(string id, CancellationToken cancellationToken = default)
    {
        // 删除所有模型表中的向量
        foreach (var modelName in _modelDimensions.Keys.ToList())
        {
            var tableName = ModelToTableName(modelName);
            var deleteVectorSql = $"DELETE FROM {tableName} WHERE chunk_id = @chunkId";
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = deleteVectorSql;
            cmd.Parameters.AddWithValue("@chunkId", id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 删除分段
        var sql = "DELETE FROM chunks WHERE id = @id";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteChunksByFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        // 获取文件的所有分段ID
        var chunkIds = new List<string>();
        var getChunksSql = "SELECT id FROM chunks WHERE file_id = @fileId";
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = getChunksSql;
            cmd.Parameters.AddWithValue("@fileId", fileId);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                chunkIds.Add(reader.GetString(0));
            }
        }

        // 删除所有模型表中的向量
        foreach (var modelName in _modelDimensions.Keys.ToList())
        {
            var tableName = ModelToTableName(modelName);
            foreach (var chunkId in chunkIds)
            {
                var deleteVectorSql = $"DELETE FROM {tableName} WHERE chunk_id = @chunkId";
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = deleteVectorSql;
                cmd.Parameters.AddWithValue("@chunkId", chunkId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // 删除分段
        var sql = "DELETE FROM chunks WHERE file_id = @fileId";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@fileId", fileId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ==================== 向量操作 ====================

    public async Task UpsertVectorAsync(VectorRecord vector, CancellationToken cancellationToken = default)
    {
        var modelName = vector.ModelName ?? "default";
        var dimension = vector.Vector.Length;

        EnsureModelTableExists(modelName, dimension);

        var tableName = ModelToTableName(modelName);
        var embeddingJson = VectorToJson(vector.Vector);

        var sql = $@"
            INSERT OR REPLACE INTO {tableName} (chunk_id, embedding)
            VALUES (@chunkId, @embedding)
        ";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@chunkId", vector.ChunkId);
        AddParameter(command, "@embedding", embeddingJson);

        await command.ExecuteNonQueryAsync(cancellationToken);

        // 更新模型统计
        await UpdateModelStatsAsync(modelName, cancellationToken);
    }

    public async Task UpsertVectorsAsync(IEnumerable<VectorRecord> vectors, CancellationToken cancellationToken = default)
    {
        var vectorList = vectors.ToList();
        if (vectorList.Count == 0) return;

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            // 按模型分组
            var byModel = vectorList.GroupBy(v => v.ModelName ?? "default");

            foreach (var group in byModel)
            {
                var modelName = group.Key;
                var firstVector = group.First();
                var dimension = firstVector.Vector.Length;

                EnsureModelTableExists(modelName, dimension);

                var tableName = ModelToTableName(modelName);

                // 使用参数化查询插入向量
                foreach (var vector in group)
                {
                    var sql = $@"
                        INSERT OR REPLACE INTO {tableName} (chunk_id, embedding)
                        VALUES (@chunkId, @embedding)
                    ";

                    using var command = _connection.CreateCommand();
                    command.CommandText = sql;
                    AddParameter(command, "@chunkId", vector.ChunkId);
                    AddParameter(command, "@embedding", VectorToJson(vector.Vector));
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            // 更新所有模型的统计
            foreach (var modelName in byModel.Select(g => g.Key))
            {
                await UpdateModelStatsAsync(modelName, cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<VectorRecord?> GetVectorAsync(string id, CancellationToken cancellationToken = default)
    {
        // 向量ID格式为 "vec_{chunkId}" 或直接是 chunkId
        var chunkId = id.StartsWith("vec_") ? id[4..] : id;
        return await GetVectorByChunkIdAsync(chunkId, cancellationToken);
    }

    public async Task<VectorRecord?> GetVectorByChunkIdAsync(string chunkId, CancellationToken cancellationToken = default)
    {
        // 遍历所有模型表查找
        foreach (var modelName in _modelDimensions.Keys)
        {
            var tableName = ModelToTableName(modelName);
            var dimension = _modelDimensions[modelName];

            var sql = $"SELECT chunk_id, embedding FROM {tableName} WHERE chunk_id = @chunkId";

            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = sql;
                AddParameter(command, "@chunkId", chunkId);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var embeddingBytes = reader.GetFieldValue<byte[]>(1);
                    var vector = BlobToVector(embeddingBytes);

                    return new VectorRecord
                    {
                        Id = $"vec_{chunkId}",
                        ChunkId = chunkId,
                        Vector = vector,
                        ModelName = modelName,
                        Dimension = dimension,
                        CreatedAt = DateTime.UtcNow
                    };
                }
            }
            catch
            {
                // 表可能不存在，继续尝试下一个模型
            }
        }

        return null;
    }

    public async Task DeleteVectorAsync(string id, CancellationToken cancellationToken = default)
    {
        var chunkId = id.StartsWith("vec_") ? id[4..] : id;

        foreach (var modelName in _modelDimensions.Keys)
        {
            var tableName = ModelToTableName(modelName);
            var sql = $"DELETE FROM {tableName} WHERE chunk_id = @chunkId";

            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "@chunkId", chunkId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // ==================== 检索操作（使用 sqlite-vec 向量索引）====================

    public async Task<IEnumerable<SearchResult>> SearchAsync(
        float[] queryVector,
        int topK,
        CancellationToken cancellationToken = default)
    {
        // 需要调用方指定模型，这里使用第一个可用模型
        if (_modelDimensions.Count == 0)
        {
            return Enumerable.Empty<SearchResult>();
        }

        var modelName = _modelDimensions.Keys.First();
        return await SearchAsync(queryVector, modelName, topK, cancellationToken);
    }

    /// <summary>
    /// 指定模型的向量搜索
    /// </summary>
    public async Task<IEnumerable<SearchResult>> SearchAsync(
        float[] queryVector,
        string modelName,
        int topK,
        CancellationToken cancellationToken = default)
    {
        // 检查模型是否存在向量数据
        if (!_modelDimensions.TryGetValue(modelName, out var dimension))
        {
            _logger?.LogWarning("模型 '{ModelName}' 无向量数据", modelName);
            return Enumerable.Empty<SearchResult>();
        }

        // 检查维度是否匹配
        if (queryVector.Length != dimension)
        {
            _logger?.LogWarning("查询向量维度 {QueryLen} 与模型 '{ModelName}' 维度 {Dim} 不匹配", queryVector.Length, modelName, dimension);
            return Enumerable.Empty<SearchResult>();
        }

        var tableName = ModelToTableName(modelName);
        var queryJson = VectorToJson(queryVector);

        // 使用单次 JOIN 查询获取所有数据，消除 N+1 问题
        // 优化前: 1 + 2R 次 DB 往返 (R = 结果数)
        // 优化后: 1 次 DB 往返
        // 使用 LEFT JOIN 避免 NULL 导致的数据丢失，所有列用显式别名避免冲突
        // 注意: sqlite-vec 要求 LIMIT 在解析时已知，因此直接嵌入整数值而非使用参数
        // 修改后：LIMIT 在子查询内层，vec0 解析时直接命中
        var sql = $@"
            SELECT
                knn.chunk_id,
                c.id AS c_id, c.file_id AS c_file_id, c.content, c.token_count,
                c.start_line, c.end_line, c.start_column, c.end_column,
                c.heading_path, c.level, c.weight, c.chunk_type,
                c.aggregate_type, c.child_chunk_count, c.chunk_order, c.content_hash,
                f.id AS f_id, f.path, f.title, f.source,
                knn.distance
            FROM (
                SELECT chunk_id, distance
                FROM {tableName}
                WHERE embedding MATCH @query
                ORDER BY distance
                LIMIT {topK}                      -- ← vec0 解析时直接可见，KNN 生效
            ) knn
            LEFT JOIN chunks c ON knn.chunk_id = c.id
            LEFT JOIN files f ON c.file_id = f.id
        ";

        var searchResults = new List<SearchResult>();

        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "@query", queryJson);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                // sqlite-vec 返回的是距离，需要转换为相似度分数
                var distance = reader.GetFloat(reader.GetOrdinal("distance"));
                var score = 1f / (1f + distance);

                // 跳过没有关联 chunk 或 file 的结果
                var chunkId = reader.GetString(reader.GetOrdinal("c_id"));
                if (string.IsNullOrEmpty(chunkId)) continue;

                var filePath = reader.IsDBNull(reader.GetOrdinal("f_id")) ? "" : reader.GetString(reader.GetOrdinal("path"));
                var fileSource = reader.IsDBNull(reader.GetOrdinal("f_id")) ? "" : reader.GetString(reader.GetOrdinal("source"));
                var fileTitle = reader.IsDBNull(reader.GetOrdinal("f_id")) ? "" : reader.GetString(reader.GetOrdinal("title"));

                searchResults.Add(new SearchResult
                {
                    ChunkId = chunkId,
                    FileId = reader.GetString(reader.GetOrdinal("c_file_id")),
                    FilePath = filePath,
                    Source = fileSource,
                    Title = fileTitle,
                    Content = reader.GetString(reader.GetOrdinal("content")),
                    Score = score,
                    StartLine = reader.GetInt32(reader.GetOrdinal("start_line")),
                    EndLine = reader.GetInt32(reader.GetOrdinal("end_line")),
                    HeadingPath = reader.GetString(reader.GetOrdinal("heading_path")),
                    Level = reader.GetInt32(reader.GetOrdinal("level")),
                    AggregateType = (AggregateType)reader.GetInt32(reader.GetOrdinal("aggregate_type")),
                    ChildChunkCount = reader.GetInt32(reader.GetOrdinal("child_chunk_count"))
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "向量搜索失败: {Message}", ex.Message);
            return Enumerable.Empty<SearchResult>();
        }

        return searchResults;
    }

    public async Task<IEnumerable<SearchResult>> SearchByAggregateTypeAsync(
        float[] queryVector,
        AggregateType aggregateType,
        int topK,
        CancellationToken cancellationToken = default)
    {
        // 获取指定类型的分段ID
        var chunkIds = new List<string>();
        var getChunksSql = "SELECT id FROM chunks WHERE aggregate_type = @aggregateType";

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = getChunksSql;
            cmd.Parameters.AddWithValue("@aggregateType", (int)aggregateType);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                chunkIds.Add(reader.GetString(0));
            }
        }

        return await SearchInIdsAsync(queryVector, chunkIds, topK, cancellationToken);
    }

    public async Task<IEnumerable<SearchResult>> SearchInIdsAsync(
        float[] queryVector,
        IEnumerable<string> ids,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var idSet = ids.ToHashSet();
        if (idSet.Count == 0) return Enumerable.Empty<SearchResult>();

        // 使用第一个可用模型
        if (_modelDimensions.Count == 0) return Enumerable.Empty<SearchResult>();

        var modelName = _modelDimensions.Keys.First();
        var dimension = _modelDimensions[modelName];

        if (queryVector.Length != dimension)
        {
            throw new ArgumentException($"查询向量维度不匹配");
        }

        var tableName = ModelToTableName(modelName);
        var queryJson = VectorToJson(queryVector);
        var limitValue = topK * 3; // 多取一些用于过滤

        // 向量搜索后过滤ID
        // 注意: sqlite-vec 要求 LIMIT 在解析时已知，因此直接嵌入整数值而非使用参数
        var sql = $@"
            SELECT v.chunk_id, v.embedding, v.distance
            FROM {tableName} v
            WHERE v.embedding MATCH @query
            ORDER BY v.distance
            LIMIT {limitValue}
        ";

        var results = new List<(string ChunkId, float Distance)>();

        try
        {
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            AddParameter(command, "@query", queryJson);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var chunkId = reader.GetString(0);
                var distance = reader.GetFloat(2);

                if (idSet.Contains(chunkId))
                {
                    results.Add((chunkId, distance));
                }

                if (results.Count >= topK) break;
            }
        }
        catch
        {
            return Enumerable.Empty<SearchResult>();
        }

        // 获取分段和文件信息
        var searchResults = new List<SearchResult>();
        foreach (var (chunkId, distance) in results)
        {
            var chunk = await GetChunkAsync(chunkId, cancellationToken);
            if (chunk == null) continue;

            var file = await GetFileAsync(chunk.FileId, cancellationToken);
            if (file == null) continue;

            var score = 1f / (1f + distance);

            searchResults.Add(new SearchResult
            {
                ChunkId = chunk.Id,
                FileId = chunk.FileId,
                FilePath = file.Path,
                Source = file.Source,
                Title = file.Title,
                Content = chunk.Content,
                Score = score,
                StartLine = chunk.StartLine,
                EndLine = chunk.EndLine,
                HeadingPath = chunk.HeadingPath,
                Level = chunk.Level,
                AggregateType = chunk.AggregateType,
                ChildChunkCount = chunk.ChildChunkCount
            });
        }

        return searchResults;
    }

    public async Task DeleteBySourceAsync(string source, CancellationToken cancellationToken = default)
    {
        // 获取指定源的所有分段ID
        var chunkIds = new List<string>();
        var getChunksSql = @"
            SELECT c.id FROM chunks c
            JOIN files f ON c.file_id = f.id
            WHERE f.source = @source
        ";

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = getChunksSql;
            cmd.Parameters.AddWithValue("@source", source);
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                chunkIds.Add(reader.GetString(0));
            }
        }

        // 删除所有模型表中的向量
        foreach (var modelName in _modelDimensions.Keys.ToList())
        {
            var tableName = ModelToTableName(modelName);
            foreach (var chunkId in chunkIds)
            {
                try
                {
                    var deleteVectorSql = $"DELETE FROM {tableName} WHERE chunk_id = @chunkId";
                    using var cmd = _connection.CreateCommand();
                    cmd.CommandText = deleteVectorSql;
                    cmd.Parameters.AddWithValue("@chunkId", chunkId);
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                catch { /* 忽略表不存在 */ }
            }
        }

        // 删除分段
        var deleteChunksSql = @"
            DELETE FROM chunks WHERE file_id IN (
                SELECT id FROM files WHERE source = @source
            )
        ";
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = deleteChunksSql;
            cmd.Parameters.AddWithValue("@source", source);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 删除文件
        var deleteFilesSql = "DELETE FROM files WHERE source = @source";
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = deleteFilesSql;
            cmd.Parameters.AddWithValue("@source", source);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task StoreBatchAsync(IEnumerable<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        await UpsertVectorsAsync(records, cancellationToken);
    }

    // ==================== 统计与管理操作 ====================

    public async Task<List<VectorStats>> GetVectorStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = new List<VectorStats>();

        // 先构建新缓存，避免 Clear() 在并发搜索时导致模型查找失败
        var freshDimensions = new Dictionary<string, int>();

        var sql = "SELECT name, dimension, updated_at FROM models";
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var modelName = reader.GetString(0);
                var dimension = reader.GetInt32(1);
                var updatedAtStr = reader.IsDBNull(2) ? null : reader.GetString(2);
                DateTime? updatedAt = updatedAtStr != null ? DateTime.Parse(updatedAtStr) : null;

                freshDimensions[modelName] = dimension;

                var tableName = ModelToTableName(modelName);
                long count = 0;

                try
                {
                    var countSql = $"SELECT COUNT(*) FROM {tableName}";
                    using var countCmd = _connection.CreateCommand();
                    countCmd.CommandText = countSql;
                    count = (long)(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
                }
                catch
                {
                    // 向量表可能不存在，count 保持 0
                }

                stats.Add(new VectorStats
                {
                    ModelName = modelName,
                    Dimension = dimension,
                    VectorCount = (int)count,
                    StorageBytes = count * dimension * sizeof(float),
                    ModelExists = count > 0,
                    LastUpdated = updatedAt
                });
            }
        }

        // 增量同步：新增/更新条目，移除已不存在的模型（避免 Clear 导致并发窗口期）
        foreach (var kv in freshDimensions)
            _modelDimensions[kv.Key] = kv.Value;
        foreach (var key in _modelDimensions.Keys.Except(freshDimensions.Keys).ToList())
            _modelDimensions.Remove(key);

        return stats;
    }

    public async Task<int> DeleteVectorsByModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        // 先从数据库检查模型是否存在
        var checkSql = "SELECT COUNT(*) FROM models WHERE name = @name";
        using (var checkCmd = _connection.CreateCommand())
        {
            checkCmd.CommandText = checkSql;
            checkCmd.Parameters.AddWithValue("@name", modelName);
            var exists = (long)await checkCmd.ExecuteScalarAsync(cancellationToken) > 0;
            if (!exists) return 0;
        }

        var tableName = ModelToTableName(modelName);
        var count = 0;

        try
        {
            // 获取数量
            var countSql = $"SELECT COUNT(*) FROM {tableName}";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = countSql;
                count = (int)((long?)await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
            }

            // 删除表
            var dropSql = $"DROP TABLE IF EXISTS {tableName}";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = dropSql;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // 从元数据表删除
            var deleteModelSql = "DELETE FROM models WHERE name = @name";
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = deleteModelSql;
                cmd.Parameters.AddWithValue("@name", modelName);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            _modelDimensions.Remove(modelName);
        }
        catch
        {
            // 忽略错误
        }

        return count;
    }

    public async Task<int> DeleteOrphanedVectorsAsync(IEnumerable<string> existingModelNames, CancellationToken cancellationToken = default)
    {
        var existingSet = existingModelNames.ToHashSet();

        // 从数据库获取所有模型名称
        var dbModels = new List<string>();
        var sql = "SELECT name FROM models";
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = sql;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                dbModels.Add(reader.GetString(0));
            }
        }

        var toDelete = dbModels.Where(k => !existingSet.Contains(k)).ToList();
        var totalDeleted = 0;

        foreach (var modelName in toDelete)
        {
            totalDeleted += await DeleteVectorsByModelAsync(modelName, cancellationToken);
        }

        return totalDeleted;
    }

    public async Task<int> BackfillSourceAsync(IDictionary<string, string> sourceNameToPath, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT id, path FROM files WHERE source = '' OR source IS NULL";
        var orphanedFiles = new List<(string Id, string Path)>();

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                orphanedFiles.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var updated = 0;
        foreach (var (id, path) in orphanedFiles)
        {
            var normalizedPath = path.Replace('\\', '/');
            var matchedSource = sourceNameToPath.FirstOrDefault(kvp =>
            {
                var normalizedSourcePath = kvp.Value.Replace('\\', '/');
                return normalizedPath.StartsWith(normalizedSourcePath, StringComparison.OrdinalIgnoreCase);
            });

            if (matchedSource.Key != null)
            {
                using var updateCommand = _connection.CreateCommand();
                updateCommand.CommandText = "UPDATE files SET source = @source WHERE id = @id";
                AddParameter(updateCommand, "@source", matchedSource.Key);
                AddParameter(updateCommand, "@id", id);
                updated += await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        return updated;
    }

    /// <summary>
    /// 更新源名称（同步更新 files 表中的 source 字段）
    /// </summary>
    public async Task<int> UpdateSourceNameAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        var sql = "UPDATE files SET source = @newSource WHERE source = @oldSource";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@newSource", newName);
        AddParameter(command, "@oldSource", oldName);

        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        return updated;
    }

    /// <summary>
    /// 更新模型统计信息
    /// </summary>
    private async Task UpdateModelStatsAsync(string modelName, CancellationToken cancellationToken)
    {
        var sql = "UPDATE models SET updated_at = @updatedAt WHERE name = @name";
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@name", modelName);
        AddParameter(command, "@updatedAt", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ==================== 辅助方法 ====================

    private void ExecuteNonQuery(string sql, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private void AddParameter(SqliteCommand command, string name, object value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private FileRecord ReadFileRecord(SqliteDataReader reader)
    {
        return new FileRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Path = reader.GetString(reader.GetOrdinal("path")),
            FileName = TryGetString(reader, "file_name") ?? "",
            Title = reader.GetString(reader.GetOrdinal("title")),
            ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
            ContentLength = TryGetLong(reader, "content_length"),
            Tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("tags"))) ?? new List<string>(),
            ParentFolder = reader.GetString(reader.GetOrdinal("parent_folder")),
            Source = TryGetString(reader, "source"),
            ChunkCount = TryGetInt(reader, "chunk_count"),
            TotalTokens = TryGetLong(reader, "total_tokens"),
            CreatedAt = DateTime.TryParse(reader.GetString(reader.GetOrdinal("created_at")), out var createdAt) ? createdAt : null,
            ModifiedAt = DateTime.TryParse(reader.GetString(reader.GetOrdinal("updated_at")), out var modifiedAt) ? modifiedAt : null,
            IndexedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("indexed_at")))
        };
    }

    private static string? TryGetString(SqliteDataReader reader, string columnName)
    {
        try { return reader.GetString(reader.GetOrdinal(columnName)); }
        catch { return null; }
    }

    private static int TryGetInt(SqliteDataReader reader, string columnName)
    {
        try { return reader.GetInt32(reader.GetOrdinal(columnName)); }
        catch { return 0; }
    }

    private static long TryGetLong(SqliteDataReader reader, string columnName)
    {
        try { return reader.GetInt64(reader.GetOrdinal(columnName)); }
        catch { return 0; }
    }

    private static double? TryGetDouble(SqliteDataReader reader, string columnName)
    {
        try { return reader.GetDouble(reader.GetOrdinal(columnName)); }
        catch { return null; }
    }

    private ChunkRecord ReadChunkRecord(SqliteDataReader reader)
    {
        return new ChunkRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            FileId = reader.GetString(reader.GetOrdinal("file_id")),
            ChunkIndex = reader.GetInt32(reader.GetOrdinal("chunk_index")),
            Content = reader.GetString(reader.GetOrdinal("content")),
            TokenCount = reader.GetInt32(reader.GetOrdinal("token_count")),
            StartLine = reader.GetInt32(reader.GetOrdinal("start_line")),
            EndLine = reader.GetInt32(reader.GetOrdinal("end_line")),
            StartColumn = reader.GetInt32(reader.GetOrdinal("start_column")),
            EndColumn = reader.GetInt32(reader.GetOrdinal("end_column")),
            HeadingPath = reader.GetString(reader.GetOrdinal("heading_path")),
            Level = reader.GetInt32(reader.GetOrdinal("level")),
            Weight = reader.GetFloat(reader.GetOrdinal("weight")),
            ChunkType = (ChunkType)reader.GetInt32(reader.GetOrdinal("chunk_type")),
            AggregateType = (AggregateType)reader.GetInt32(reader.GetOrdinal("aggregate_type")),
            ChildChunkCount = reader.GetInt32(reader.GetOrdinal("child_chunk_count")),
            ChunkOrder = TryGetDouble(reader, "chunk_order") ?? 1.0,
            ContentHash = TryGetString(reader, "content_hash")
        };
    }

    /// <summary>
    /// 向量转 JSON 格式（sqlite-vec 接受 JSON 数组格式）
    /// </summary>
    private static string VectorToJson(float[] vector)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        for (int i = 0; i < vector.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vector[i].ToString("G", System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    private byte[] VectorToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToVector(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
        return vector;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection.Close();
            _connection.Dispose();
            _writeLock.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Async enumerable that calls a dispose action when enumeration completes
    /// </summary>
    private sealed class DisposingAsyncEnumerable<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
    {
        private readonly IAsyncEnumerator<T> _enumerator;
        private readonly Action _disposeAction;
        private bool _disposed;

        public DisposingAsyncEnumerable(IAsyncEnumerator<T> enumerator, Action disposeAction)
        {
            _enumerator = enumerator;
            _disposeAction = disposeAction;
        }

        public T Current => _enumerator.Current;

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return this;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await _enumerator.DisposeAsync();
                _disposeAction();
            }
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            try
            {
                return await _enumerator.MoveNextAsync();
            }
            catch
            {
                _disposeAction();
                throw;
            }
        }
    }
}
