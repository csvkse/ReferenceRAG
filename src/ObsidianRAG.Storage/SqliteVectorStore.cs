using Microsoft.Data.Sqlite;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using System.Text.Json;

namespace ObsidianRAG.Storage;

/// <summary>
/// SQLite 向量存储 - 使用 sqlite-vec 扩展
/// </summary>
public class SqliteVectorStore : IVectorStore, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _dbPath;
    private readonly int _dimension;
    private bool _disposed;

    public SqliteVectorStore(string dbPath, int dimension = 384)
    {
        _dbPath = dbPath;
        _dimension = dimension;
        
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
        using var transaction = _connection.BeginTransaction();

        // 文件表
        var createFilesTable = @"
            CREATE TABLE IF NOT EXISTS files (
                id TEXT PRIMARY KEY,
                path TEXT NOT NULL UNIQUE,
                title TEXT,
                content_hash TEXT,
                tags TEXT,
                parent_folder TEXT,
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

        // 向量表（使用 JSON 存储，因为 sqlite-vec 需要扩展）
        var createVectorsTable = @"
            CREATE TABLE IF NOT EXISTS vectors (
                id TEXT PRIMARY KEY,
                chunk_id TEXT NOT NULL UNIQUE,
                vector BLOB NOT NULL,
                model_name TEXT,
                created_at TEXT,
                FOREIGN KEY (chunk_id) REFERENCES chunks(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_vectors_chunk ON vectors(chunk_id);
        ";
        
        ExecuteNonQuery(createVectorsTable, transaction);

        transaction.Commit();
    }

    // ==================== 文件操作 ====================

    public async Task UpsertFileAsync(FileRecord file, CancellationToken cancellationToken = default)
    {
        var sql = @"
            INSERT OR REPLACE INTO files 
            (id, path, title, content_hash, tags, parent_folder, created_at, updated_at, indexed_at)
            VALUES 
            (@id, @path, @title, @contentHash, @tags, @parentFolder, @createdAt, @updatedAt, @indexedAt)
        ";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@id", file.Id);
        AddParameter(command, "@path", file.Path);
        AddParameter(command, "@title", file.Title ?? "");
        AddParameter(command, "@contentHash", file.ContentHash ?? "");
        AddParameter(command, "@tags", file.Tags != null ? JsonSerializer.Serialize(file.Tags) : "[]");
        AddParameter(command, "@parentFolder", file.ParentFolder ?? "");
        AddParameter(command, "@createdAt", file.CreatedAt?.ToString("O") ?? "");
        AddParameter(command, "@updatedAt", file.ModifiedAt?.ToString("O") ?? "");
        AddParameter(command, "@indexedAt", file.IndexedAt.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
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
        // 由于有外键约束，删除文件会级联删除分段和向量
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

    // ==================== 分段操作 ====================

    public async Task UpsertChunkAsync(ChunkRecord chunk, CancellationToken cancellationToken = default)
    {
        var sql = @"
            INSERT OR REPLACE INTO chunks 
            (id, file_id, chunk_index, content, token_count, start_line, end_line, 
             start_column, end_column, heading_path, level, weight, chunk_type, 
             aggregate_type, child_chunk_count)
            VALUES 
            (@id, @fileId, @chunkIndex, @content, @tokenCount, @startLine, @endLine,
             @startColumn, @endColumn, @headingPath, @level, @weight, @chunkType,
             @aggregateType, @childChunkCount)
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

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertChunksAsync(IEnumerable<ChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        var chunkList = chunks.ToList();
        if (chunkList.Count == 0) return;

        // 使用单条批量 INSERT 语句
        var sql = new System.Text.StringBuilder();
        sql.AppendLine(@"INSERT OR REPLACE INTO chunks
            (id, file_id, chunk_index, content, token_count, start_line, end_line,
             start_column, end_column, heading_path, level, weight, chunk_type,
             aggregate_type, child_chunk_count) VALUES");

        var parameters = new List<(string Name, object Value)>();
        for (int i = 0; i < chunkList.Count; i++)
        {
            var chunk = chunkList[i];
            if (i > 0) sql.AppendLine(",");
            sql.Append($"(@id{i}, @fileId{i}, @chunkIndex{i}, @content{i}, @tokenCount{i}, @startLine{i}, @endLine{i}, " +
                      $"@startColumn{i}, @endColumn{i}, @headingPath{i}, @level{i}, @weight{i}, @chunkType{i}, " +
                      $"@aggregateType{i}, @childChunkCount{i})");

            parameters.Add(($"@id{i}", chunk.Id));
            parameters.Add(($"@fileId{i}", chunk.FileId));
            parameters.Add(($"@chunkIndex{i}", chunk.ChunkIndex));
            parameters.Add(($"@content{i}", chunk.Content));
            parameters.Add(($"@tokenCount{i}", chunk.TokenCount));
            parameters.Add(($"@startLine{i}", chunk.StartLine));
            parameters.Add(($"@endLine{i}", chunk.EndLine));
            parameters.Add(($"@startColumn{i}", chunk.StartColumn));
            parameters.Add(($"@endColumn{i}", chunk.EndColumn));
            parameters.Add(($"@headingPath{i}", chunk.HeadingPath ?? ""));
            parameters.Add(($"@level{i}", chunk.Level));
            parameters.Add(($"@weight{i}", chunk.Weight));
            parameters.Add(($"@chunkType{i}", (int)chunk.ChunkType));
            parameters.Add(($"@aggregateType{i}", (int)chunk.AggregateType));
            parameters.Add(($"@childChunkCount{i}", chunk.ChildChunkCount));
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Transaction = transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
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
        var sql = "DELETE FROM chunks WHERE id = @id";
        
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@id", id);
        
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteChunksByFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var sql = "DELETE FROM chunks WHERE file_id = @fileId";
        
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@fileId", fileId);
        
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ==================== 向量操作 ====================

    public async Task UpsertVectorAsync(VectorRecord vector, CancellationToken cancellationToken = default)
    {
        var sql = @"
            INSERT OR REPLACE INTO vectors (id, chunk_id, vector, model_name, created_at)
            VALUES (@id, @chunkId, @vector, @modelName, @createdAt)
        ";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@id", vector.Id);
        AddParameter(command, "@chunkId", vector.ChunkId);
        AddParameter(command, "@vector", VectorToBlob(vector.Vector));
        AddParameter(command, "@modelName", vector.ModelName ?? "");
        AddParameter(command, "@createdAt", DateTime.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertVectorsAsync(IEnumerable<VectorRecord> vectors, CancellationToken cancellationToken = default)
    {
        var vectorList = vectors.ToList();
        if (vectorList.Count == 0) return;

        // 使用单条批量 INSERT 语句（SQLite 支持 VALUES 多行）
        var sql = new System.Text.StringBuilder();
        sql.AppendLine("INSERT OR REPLACE INTO vectors (id, chunk_id, vector, model_name, created_at) VALUES");

        var parameters = new List<(string Name, object Value)>();
        for (int i = 0; i < vectorList.Count; i++)
        {
            if (i > 0) sql.AppendLine(",");
            sql.Append($"(@id{i}, @chunkId{i}, @vector{i}, @modelName{i}, @createdAt{i})");

            parameters.Add(($"@id{i}", vectorList[i].Id));
            parameters.Add(($"@chunkId{i}", vectorList[i].ChunkId));
            parameters.Add(($"@vector{i}", VectorToBlob(vectorList[i].Vector)));
            parameters.Add(($"@modelName{i}", vectorList[i].ModelName ?? ""));
            parameters.Add(($"@createdAt{i}", DateTime.UtcNow.ToString("O")));
        }

        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.CommandText = sql.ToString();
        command.Transaction = transaction;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
        transaction.Commit();
    }

    public async Task<VectorRecord?> GetVectorAsync(string id, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM vectors WHERE id = @id";
        
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@id", id);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadVectorRecord(reader);
        }

        return null;
    }

    public async Task<VectorRecord?> GetVectorByChunkIdAsync(string chunkId, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM vectors WHERE chunk_id = @chunkId";
        
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@chunkId", chunkId);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadVectorRecord(reader);
        }

        return null;
    }

    public async Task DeleteVectorAsync(string id, CancellationToken cancellationToken = default)
    {
        var sql = "DELETE FROM vectors WHERE id = @id";
        
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@id", id);
        
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // ==================== 检索操作 ====================

    public async Task<IEnumerable<SearchResult>> SearchAsync(
        float[] queryVector,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            SELECT v.id, v.chunk_id, v.vector, v.model_name,
                   c.file_id, c.content, c.start_line, c.end_line, 
                   c.heading_path, c.level, c.aggregate_type, c.child_chunk_count,
                   f.path, f.title
            FROM vectors v
            JOIN chunks c ON v.chunk_id = c.id
            JOIN files f ON c.file_id = f.id
        ";

        var results = new List<(VectorRecord Vector, ChunkRecord Chunk, FileRecord File, float Score)>();

        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var vector = ReadVectorRecord(reader);
            var chunk = ReadChunkRecord(reader);
            var file = new FileRecord
            {
                Id = reader.GetString(reader.GetOrdinal("file_id")),
                Path = reader.GetString(reader.GetOrdinal("path")),
                Title = reader.GetString(reader.GetOrdinal("title"))
            };

            var score = CosineSimilarity(queryVector, vector.Vector);
            results.Add((vector, chunk, file, score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .Select(r => new SearchResult
            {
                ChunkId = r.Chunk.Id,
                FileId = r.File.Id,
                FilePath = r.File.Path,
                Title = r.File.Title,
                Content = r.Chunk.Content,
                Score = r.Score,
                StartLine = r.Chunk.StartLine,
                EndLine = r.Chunk.EndLine,
                HeadingPath = r.Chunk.HeadingPath,
                Level = r.Chunk.Level,
                AggregateType = r.Chunk.AggregateType,
                ChildChunkCount = r.Chunk.ChildChunkCount
            });
    }

    public async Task<IEnumerable<SearchResult>> SearchByAggregateTypeAsync(
        float[] queryVector,
        AggregateType aggregateType,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var sql = @"
            SELECT v.id, v.chunk_id, v.vector, v.model_name,
                   c.file_id, c.content, c.start_line, c.end_line, 
                   c.heading_path, c.level, c.aggregate_type, c.child_chunk_count,
                   f.path, f.title
            FROM vectors v
            JOIN chunks c ON v.chunk_id = c.id
            JOIN files f ON c.file_id = f.id
            WHERE c.aggregate_type = @aggregateType
        ";

        var results = new List<(VectorRecord Vector, ChunkRecord Chunk, FileRecord File, float Score)>();

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@aggregateType", (int)aggregateType);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var vector = ReadVectorRecord(reader);
            var chunk = ReadChunkRecord(reader);
            var file = new FileRecord
            {
                Id = reader.GetString(reader.GetOrdinal("file_id")),
                Path = reader.GetString(reader.GetOrdinal("path")),
                Title = reader.GetString(reader.GetOrdinal("title"))
            };

            var score = CosineSimilarity(queryVector, vector.Vector);
            results.Add((vector, chunk, file, score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .Select(r => new SearchResult
            {
                ChunkId = r.Chunk.Id,
                FileId = r.File.Id,
                FilePath = r.File.Path,
                Title = r.File.Title,
                Content = r.Chunk.Content,
                Score = r.Score,
                StartLine = r.Chunk.StartLine,
                EndLine = r.Chunk.EndLine,
                HeadingPath = r.Chunk.HeadingPath,
                Level = r.Chunk.Level,
                AggregateType = r.Chunk.AggregateType,
                ChildChunkCount = r.Chunk.ChildChunkCount
            });
    }

    public async Task<IEnumerable<SearchResult>> SearchInIdsAsync(
        float[] queryVector,
        IEnumerable<string> ids,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return Enumerable.Empty<SearchResult>();

        var placeholders = string.Join(",", idList.Select((_, i) => $"@id{i}"));
        var sql = $@"
            SELECT v.id, v.chunk_id, v.vector, v.model_name,
                   c.file_id, c.content, c.start_line, c.end_line, 
                   c.heading_path, c.level, c.aggregate_type, c.child_chunk_count,
                   f.path, f.title
            FROM vectors v
            JOIN chunks c ON v.chunk_id = c.id
            JOIN files f ON c.file_id = f.id
            WHERE v.chunk_id IN ({placeholders})
        ";

        var results = new List<(VectorRecord Vector, ChunkRecord Chunk, FileRecord File, float Score)>();

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        
        for (int i = 0; i < idList.Count; i++)
        {
            AddParameter(command, $"@id{i}", idList[i]);
        }

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var vector = ReadVectorRecord(reader);
            var chunk = ReadChunkRecord(reader);
            var file = new FileRecord
            {
                Id = reader.GetString(reader.GetOrdinal("file_id")),
                Path = reader.GetString(reader.GetOrdinal("path")),
                Title = reader.GetString(reader.GetOrdinal("title"))
            };

            var score = CosineSimilarity(queryVector, vector.Vector);
            results.Add((vector, chunk, file, score));
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .Select(r => new SearchResult
            {
                ChunkId = r.Chunk.Id,
                FileId = r.File.Id,
                FilePath = r.File.Path,
                Title = r.File.Title,
                Content = r.Chunk.Content,
                Score = r.Score,
                StartLine = r.Chunk.StartLine,
                EndLine = r.Chunk.EndLine,
                HeadingPath = r.Chunk.HeadingPath,
                Level = r.Chunk.Level,
                AggregateType = r.Chunk.AggregateType,
                ChildChunkCount = r.Chunk.ChildChunkCount
            });
    }

    public async Task DeleteBySourceAsync(string source, CancellationToken cancellationToken = default)
    {
        // 删除指定源的所有数据
        var sql = @"
            DELETE FROM vectors WHERE chunk_id IN (
                SELECT c.id FROM chunks c
                JOIN files f ON c.file_id = f.id
                WHERE f.parent_folder = @source
            );
            DELETE FROM chunks WHERE file_id IN (
                SELECT id FROM files WHERE parent_folder = @source
            );
            DELETE FROM files WHERE parent_folder = @source;
        ";

        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@source", source);
        
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task StoreBatchAsync(IEnumerable<VectorRecord> records, CancellationToken cancellationToken = default)
    {
        // 复用 UpsertVectorsAsync 的批量实现
        await UpsertVectorsAsync(records, cancellationToken);
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
            Title = reader.GetString(reader.GetOrdinal("title")),
            ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
            Tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("tags"))) ?? new List<string>(),
            ParentFolder = reader.GetString(reader.GetOrdinal("parent_folder")),
            CreatedAt = DateTime.TryParse(reader.GetString(reader.GetOrdinal("created_at")), out var createdAt) ? createdAt : null,
            ModifiedAt = DateTime.TryParse(reader.GetString(reader.GetOrdinal("updated_at")), out var modifiedAt) ? modifiedAt : null,
            IndexedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("indexed_at")))
        };
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
            ChildChunkCount = reader.GetInt32(reader.GetOrdinal("child_chunk_count"))
        };
    }

    private VectorRecord ReadVectorRecord(SqliteDataReader reader)
    {
        return new VectorRecord
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            ChunkId = reader.GetString(reader.GetOrdinal("chunk_id")),
            Vector = BlobToVector(reader.GetFieldValue<byte[]>(reader.GetOrdinal("vector"))),
            ModelName = reader.GetString(reader.GetOrdinal("model_name"))
        };
    }

    private byte[] VectorToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private float[] BlobToVector(byte[] blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, vector, 0, blob.Length);
        return vector;
    }

    private float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator < 1e-10f ? 0 : dot / denominator;
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
