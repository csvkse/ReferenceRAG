using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Storage;

/// <summary>
/// 全局元数据与控制目录实现（使用 SQLite，保存在 data/system/catalog.db）
/// </summary>
public sealed class CatalogStore : ICatalogStore, IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<CatalogStore>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public CatalogStore(string dbPath, ILogger<CatalogStore>? logger = null)
    {
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
        await _gate.WaitAsync(ct);
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

                CREATE TABLE IF NOT EXISTS knowledge_bases (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    desired_config_revision INTEGER NOT NULL DEFAULT 1,
                    effective_config_revision INTEGER NOT NULL DEFAULT 0,
                    embedding_spec_id TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS connectors (
                    id TEXT PRIMARY KEY,
                    knowledge_base_id TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    target_path TEXT NOT NULL,
                    file_pattern TEXT,
                    config_revision INTEGER NOT NULL DEFAULT 1,
                    enabled INTEGER NOT NULL DEFAULT 1,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(knowledge_base_id) REFERENCES knowledge_bases(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS jobs (
                    id TEXT PRIMARY KEY,
                    operation TEXT NOT NULL,
                    knowledge_base_id TEXT NOT NULL,
                    expected_index_epoch INTEGER NOT NULL DEFAULT 1,
                    status TEXT NOT NULL,
                    phase TEXT NOT NULL,
                    total_units INTEGER NOT NULL DEFAULT 0,
                    processed_units INTEGER NOT NULL DEFAULT 0,
                    idempotency_key TEXT,
                    error_message TEXT,
                    created_at TEXT NOT NULL,
                    finished_at TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_jobs_kb_id ON jobs(knowledge_base_id);
                CREATE INDEX IF NOT EXISTS idx_jobs_idempotency ON jobs(idempotency_key);

                CREATE TABLE IF NOT EXISTS job_attempts (
                    id TEXT PRIMARY KEY,
                    job_id TEXT NOT NULL,
                    attempt_number INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    checkpoint_data TEXT,
                    error_message TEXT,
                    started_at TEXT NOT NULL,
                    completed_at TEXT,
                    FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS embedding_specs (
                    id TEXT PRIMARY KEY,
                    model_name TEXT NOT NULL,
                    dimension INTEGER NOT NULL,
                    metric TEXT NOT NULL,
                    query_prefix TEXT,
                    document_prefix TEXT,
                    max_tokens INTEGER NOT NULL DEFAULT 512,
                    enable_dual_channel INTEGER NOT NULL DEFAULT 0,
                    ingestion_backend TEXT,
                    ingestion_base_url TEXT,
                    ingestion_api_key TEXT,
                    search_backend TEXT,
                    created_at TEXT NOT NULL
                );

                INSERT OR IGNORE INTO embedding_specs (id, model_name, dimension, metric, query_prefix, document_prefix, max_tokens, created_at)
                VALUES 
                    ('bge-m3-default', 'BAAI/bge-m3', 1024, 'cosine', 'query: ', '', 8192, datetime('now')),
                    ('bge-small-zh-default', 'bge-small-zh-v1.5', 512, 'cosine', '', '', 512, datetime('now')),
                    ('openai-small-default', 'text-embedding-3-small', 1536, 'cosine', '', '', 8191, datetime('now')),
                    ('minilm-default', 'all-MiniLM-L6-v2', 384, 'cosine', '', '', 512, datetime('now'));
            ";
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
            _logger?.LogInformation("[CatalogStore] catalog.db 数据库架构初始化成功: {DbPath}", _dbPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<KnowledgeBaseDefinition?> GetKnowledgeBaseAsync(string id, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, description, enabled, desired_config_revision,
                   effective_config_revision, embedding_spec_id, created_at, updated_at
            FROM knowledge_bases
            WHERE id = @id;
        ";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadKnowledgeBase(reader);
        }
        return null;
    }

    public async Task<IReadOnlyList<KnowledgeBaseDefinition>> ListKnowledgeBasesAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var list = new List<KnowledgeBaseDefinition>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, description, enabled, desired_config_revision,
                   effective_config_revision, embedding_spec_id, created_at, updated_at
            FROM knowledge_bases
            ORDER BY created_at DESC;
        ";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadKnowledgeBase(reader));
        }
        return list;
    }

    public async Task<KnowledgeBaseDefinition> SaveKnowledgeBaseAsync(KnowledgeBaseDefinition kb, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO knowledge_bases (
                    id, name, description, enabled, desired_config_revision,
                    effective_config_revision, embedding_spec_id, created_at, updated_at
                )
                VALUES (
                    @id, @name, @description, @enabled, @desired_rev,
                    @effective_rev, @spec_id, @created_at, @updated_at
                )
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name,
                    description = excluded.description,
                    enabled = excluded.enabled,
                    desired_config_revision = excluded.desired_config_revision,
                    effective_config_revision = excluded.effective_config_revision,
                    embedding_spec_id = excluded.embedding_spec_id,
                    updated_at = excluded.updated_at;
            ";
            cmd.Parameters.AddWithValue("@id", kb.Id);
            cmd.Parameters.AddWithValue("@name", kb.Name);
            cmd.Parameters.AddWithValue("@description", (object?)kb.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@enabled", kb.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@desired_rev", kb.DesiredConfigRevision);
            cmd.Parameters.AddWithValue("@effective_rev", kb.EffectiveConfigRevision);
            cmd.Parameters.AddWithValue("@spec_id", kb.EmbeddingSpecId);
            cmd.Parameters.AddWithValue("@created_at", kb.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@updated_at", kb.UpdatedAt.ToString("O"));

            await cmd.ExecuteNonQueryAsync(ct);
            return kb;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteKnowledgeBaseAsync(string id, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM knowledge_bases WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            var affected = await cmd.ExecuteNonQueryAsync(ct);
            return affected > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ConnectorDefinition> SaveConnectorAsync(ConnectorDefinition connector, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO connectors (
                    id, knowledge_base_id, kind, target_path, file_pattern,
                    config_revision, enabled, created_at, updated_at
                )
                VALUES (
                    @id, @kb_id, @kind, @target_path, @file_pattern,
                    @config_rev, @enabled, @created_at, @updated_at
                )
                ON CONFLICT(id) DO UPDATE SET
                    kind = excluded.kind,
                    target_path = excluded.target_path,
                    file_pattern = excluded.file_pattern,
                    config_revision = excluded.config_revision,
                    enabled = excluded.enabled,
                    updated_at = excluded.updated_at;
            ";
            cmd.Parameters.AddWithValue("@id", connector.Id);
            cmd.Parameters.AddWithValue("@kb_id", connector.KnowledgeBaseId);
            cmd.Parameters.AddWithValue("@kind", connector.Kind);
            cmd.Parameters.AddWithValue("@target_path", connector.TargetPath);
            cmd.Parameters.AddWithValue("@file_pattern", (object?)connector.FilePattern ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@config_rev", connector.ConfigRevision);
            cmd.Parameters.AddWithValue("@enabled", connector.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@created_at", connector.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@updated_at", connector.UpdatedAt.ToString("O"));

            await cmd.ExecuteNonQueryAsync(ct);
            return connector;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ConnectorDefinition>> GetConnectorsByKnowledgeBaseAsync(string kbId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var list = new List<ConnectorDefinition>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, knowledge_base_id, kind, target_path, file_pattern,
                   config_revision, enabled, created_at, updated_at
            FROM connectors
            WHERE knowledge_base_id = @kb_id
            ORDER BY created_at ASC;
        ";
        cmd.Parameters.AddWithValue("@kb_id", kbId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadConnector(reader));
        }
        return list;
    }

    public async Task<bool> DeleteConnectorAsync(string connectorId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM connectors WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", connectorId);
            var affected = await cmd.ExecuteNonQueryAsync(ct);
            return affected > 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JobDefinition> SaveJobAsync(JobDefinition job, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO jobs (
                    id, operation, knowledge_base_id, expected_index_epoch,
                    status, phase, total_units, processed_units,
                    idempotency_key, error_message, created_at, finished_at
                )
                VALUES (
                    @id, @op, @kb_id, @epoch,
                    @status, @phase, @total, @processed,
                    @idempotency, @error, @created_at, @finished_at
                )
                ON CONFLICT(id) DO UPDATE SET
                    status = excluded.status,
                    phase = excluded.phase,
                    total_units = excluded.total_units,
                    processed_units = excluded.processed_units,
                    error_message = excluded.error_message,
                    finished_at = excluded.finished_at;
            ";
            cmd.Parameters.AddWithValue("@id", job.Id);
            cmd.Parameters.AddWithValue("@op", job.Operation);
            cmd.Parameters.AddWithValue("@kb_id", job.KnowledgeBaseId);
            cmd.Parameters.AddWithValue("@epoch", job.ExpectedIndexEpoch);
            cmd.Parameters.AddWithValue("@status", job.Status.ToString());
            cmd.Parameters.AddWithValue("@phase", job.Phase.ToString());
            cmd.Parameters.AddWithValue("@total", job.TotalUnits);
            cmd.Parameters.AddWithValue("@processed", job.ProcessedUnits);
            cmd.Parameters.AddWithValue("@idempotency", (object?)job.IdempotencyKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@error", (object?)job.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", job.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@finished_at", (object?)job.FinishedAt?.ToString("O") ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
            return job;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JobDefinition?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, operation, knowledge_base_id, expected_index_epoch,
                   status, phase, total_units, processed_units,
                   idempotency_key, error_message, created_at, finished_at
            FROM jobs
            WHERE id = @id;
        ";
        cmd.Parameters.AddWithValue("@id", jobId);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return ReadJob(reader);
        }
        return null;
    }

    public async Task<IReadOnlyList<JobDefinition>> ListJobsAsync(string? kbId = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        var list = new List<JobDefinition>();
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        if (string.IsNullOrEmpty(kbId))
        {
            cmd.CommandText = @"
                SELECT id, operation, knowledge_base_id, expected_index_epoch,
                       status, phase, total_units, processed_units,
                       idempotency_key, error_message, created_at, finished_at
                FROM jobs
                ORDER BY created_at DESC;
            ";
        }
        else
        {
            cmd.CommandText = @"
                SELECT id, operation, knowledge_base_id, expected_index_epoch,
                       status, phase, total_units, processed_units,
                       idempotency_key, error_message, created_at, finished_at
                FROM jobs
                WHERE knowledge_base_id = @kb_id
                ORDER BY created_at DESC;
            ";
            cmd.Parameters.AddWithValue("@kb_id", kbId);
        }
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadJob(reader));
        }
        return list;
    }

    private static KnowledgeBaseDefinition ReadKnowledgeBase(SqliteDataReader reader)
    {
        return new KnowledgeBaseDefinition(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            Description: reader.IsDBNull(2) ? null : reader.GetString(2),
            Enabled: reader.GetInt32(3) == 1,
            DesiredConfigRevision: reader.GetInt64(4),
            EffectiveConfigRevision: reader.GetInt64(5),
            EmbeddingSpecId: reader.GetString(6),
            CreatedAt: DateTime.Parse(reader.GetString(7)),
            UpdatedAt: DateTime.Parse(reader.GetString(8))
        );
    }

    private static ConnectorDefinition ReadConnector(SqliteDataReader reader)
    {
        return new ConnectorDefinition(
            Id: reader.GetString(0),
            KnowledgeBaseId: reader.GetString(1),
            Kind: reader.GetString(2),
            TargetPath: reader.GetString(3),
            FilePattern: reader.IsDBNull(4) ? null : reader.GetString(4),
            ConfigRevision: reader.GetInt64(5),
            Enabled: reader.GetInt32(6) == 1,
            CreatedAt: DateTime.Parse(reader.GetString(7)),
            UpdatedAt: DateTime.Parse(reader.GetString(8))
        );
    }

    private static JobDefinition ReadJob(SqliteDataReader reader)
    {
        Enum.TryParse<JobStatus>(reader.GetString(4), out var status);
        Enum.TryParse<OperationPhase>(reader.GetString(5), out var phase);
        return new JobDefinition(
            Id: reader.GetString(0),
            Operation: reader.GetString(1),
            KnowledgeBaseId: reader.GetString(2),
            ExpectedIndexEpoch: reader.GetInt64(3),
            Status: status,
            Phase: phase,
            TotalUnits: reader.GetInt32(6),
            ProcessedUnits: reader.GetInt32(7),
            IdempotencyKey: reader.IsDBNull(8) ? null : reader.GetString(8),
            ErrorMessage: reader.IsDBNull(9) ? null : reader.GetString(9),
            CreatedAt: DateTime.Parse(reader.GetString(10)),
            FinishedAt: reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11))
        );
    }

    public async Task SaveEmbeddingSpecAsync(EmbeddingSpec spec, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            using var conn = CreateConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO embedding_specs (
                    id, model_name, dimension, metric, query_prefix, document_prefix, max_tokens,
                    enable_dual_channel, ingestion_backend, ingestion_base_url, ingestion_api_key, search_backend, created_at)
                VALUES (
                    @id, @model_name, @dimension, @metric, @query_prefix, @document_prefix, @max_tokens,
                    @enable_dual_channel, @ingestion_backend, @ingestion_base_url, @ingestion_api_key, @search_backend, @created_at)
                ON CONFLICT(id) DO UPDATE SET
                    model_name = excluded.model_name,
                    dimension = excluded.dimension,
                    metric = excluded.metric,
                    query_prefix = excluded.query_prefix,
                    document_prefix = excluded.document_prefix,
                    max_tokens = excluded.max_tokens,
                    enable_dual_channel = excluded.enable_dual_channel,
                    ingestion_backend = excluded.ingestion_backend,
                    ingestion_base_url = excluded.ingestion_base_url,
                    ingestion_api_key = excluded.ingestion_api_key,
                    search_backend = excluded.search_backend;
            ";
            cmd.Parameters.AddWithValue("@id", spec.Id);
            cmd.Parameters.AddWithValue("@model_name", spec.ModelName);
            cmd.Parameters.AddWithValue("@dimension", spec.Dimension);
            cmd.Parameters.AddWithValue("@metric", spec.Metric);
            cmd.Parameters.AddWithValue("@query_prefix", (object?)spec.QueryPrefix ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@document_prefix", (object?)spec.DocumentPrefix ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@max_tokens", spec.MaxTokens);
            cmd.Parameters.AddWithValue("@enable_dual_channel", spec.EnableDualChannel ? 1 : 0);
            cmd.Parameters.AddWithValue("@ingestion_backend", (object?)spec.IngestionBackend ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ingestion_base_url", (object?)spec.IngestionBaseUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ingestion_api_key", (object?)spec.IngestionApiKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@search_backend", (object?)spec.SearchBackend ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EmbeddingSpec?> GetEmbeddingSpecAsync(string id, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, model_name, dimension, metric, query_prefix, document_prefix, max_tokens,
                   enable_dual_channel, ingestion_backend, ingestion_base_url, ingestion_api_key, search_backend
            FROM embedding_specs
            WHERE id = @id;
        ";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new EmbeddingSpec(
                Id: reader.GetString(0),
                ModelName: reader.GetString(1),
                Dimension: reader.GetInt32(2),
                Metric: reader.GetString(3),
                QueryPrefix: reader.IsDBNull(4) ? null : reader.GetString(4),
                DocumentPrefix: reader.IsDBNull(5) ? null : reader.GetString(5),
                MaxTokens: reader.GetInt32(6),
                EnableDualChannel: !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
                IngestionBackend: reader.IsDBNull(8) ? null : reader.GetString(8),
                IngestionBaseUrl: reader.IsDBNull(9) ? null : reader.GetString(9),
                IngestionApiKey: reader.IsDBNull(10) ? null : reader.GetString(10),
                SearchBackend: reader.IsDBNull(11) ? null : reader.GetString(11)
            );
        }
        return null;
    }

    public async Task<IReadOnlyList<EmbeddingSpec>> ListEmbeddingSpecsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var conn = CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, model_name, dimension, metric, query_prefix, document_prefix, max_tokens,
                   enable_dual_channel, ingestion_backend, ingestion_base_url, ingestion_api_key, search_backend
            FROM embedding_specs
            ORDER BY id ASC;
        ";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<EmbeddingSpec>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(new EmbeddingSpec(
                Id: reader.GetString(0),
                ModelName: reader.GetString(1),
                Dimension: reader.GetInt32(2),
                Metric: reader.GetString(3),
                QueryPrefix: reader.IsDBNull(4) ? null : reader.GetString(4),
                DocumentPrefix: reader.IsDBNull(5) ? null : reader.GetString(5),
                MaxTokens: reader.GetInt32(6),
                EnableDualChannel: !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
                IngestionBackend: reader.IsDBNull(8) ? null : reader.GetString(8),
                IngestionBaseUrl: reader.IsDBNull(9) ? null : reader.GetString(9),
                IngestionApiKey: reader.IsDBNull(10) ? null : reader.GetString(10),
                SearchBackend: reader.IsDBNull(11) ? null : reader.GetString(11)
            ));
        }
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gate.Dispose();
        _disposed = true;
    }
}
