using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Infrastructure.Features.Security;

public sealed class ApiKeyStore : IApiKeyStore, IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<ApiKeyStore>? _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, ApiKeyEntity> _cachedByHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ApiKeyEntity> _cachedById = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public ApiKeyStore(ConfigManager configManager, ILogger<ApiKeyStore>? logger = null)
    {
        _logger = logger;
        var cfg = configManager.Load();
        _dbPath = Path.Combine(cfg.DataPath ?? "data", "auth.db");
    }

    public ApiKeyStore(string dbPath, ILogger<ApiKeyStore>? logger = null)
    {
        _logger = logger;
        _dbPath = dbPath;
    }

    public static string ComputeHash(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(rawKey.Trim());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        await _lock.WaitAsync();
        try
        {
            if (_initialized) return;

            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();

            var ddl = @"
                CREATE TABLE IF NOT EXISTS api_keys (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    key_prefix TEXT NOT NULL,
                    key_hash TEXT NOT NULL UNIQUE,
                    is_enabled INTEGER NOT NULL DEFAULT 1,
                    allow_api INTEGER NOT NULL DEFAULT 1,
                    allow_mcp INTEGER NOT NULL DEFAULT 1,
                    allowed_scopes TEXT NOT NULL DEFAULT '[""*""]',
                    allowed_mcp_tools TEXT NOT NULL DEFAULT '[""*""]',
                    source_scope_mode TEXT NOT NULL DEFAULT 'all',
                    allowed_sources TEXT NOT NULL DEFAULT '[]',
                    created_at TEXT NOT NULL,
                    last_used_at TEXT,
                    expires_at TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_api_keys_hash ON api_keys(key_hash);
                CREATE INDEX IF NOT EXISTS idx_api_keys_enabled ON api_keys(is_enabled);
            ";

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = ddl;
                await cmd.ExecuteNonQueryAsync();
            }

            // 加载全部 Key 到内存缓存
            _cachedByHash.Clear();
            _cachedById.Clear();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, name, key_prefix, key_hash, is_enabled, allow_api, allow_mcp, allowed_scopes, allowed_mcp_tools, source_scope_mode, allowed_sources, created_at, last_used_at, expires_at FROM api_keys";
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var entity = ReadRow(reader);
                    _cachedByHash[entity.KeyHash] = entity;
                    _cachedById[entity.Id] = entity;
                }
            }

            _initialized = true;
            _logger?.LogInformation("API Key 存储体系已初始化，已载入 {Count} 个密钥实体", _cachedByHash.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    public ApiKeyEntity? FindByHash(string keyHash)
    {
        if (string.IsNullOrWhiteSpace(keyHash)) return null;
        if (!_initialized)
        {
            // 同步快速回退初始化
            InitializeAsync().GetAwaiter().GetResult();
        }

        if (_cachedByHash.TryGetValue(keyHash.Trim(), out var entity))
        {
            return entity;
        }
        return null;
    }

    public async Task<IReadOnlyList<ApiKeyEntity>> GetAllAsync()
    {
        await InitializeAsync();
        return _cachedById.Values.ToList();
    }

    public async Task<ApiKeyEntity?> GetByIdAsync(string id)
    {
        await InitializeAsync();
        return _cachedById.TryGetValue(id, out var entity) ? entity : null;
    }

    public async Task<(ApiKeyEntity entity, string rawKey)> CreateAsync(
        string name,
        bool allowApi = true,
        bool allowMcp = true,
        string allowedScopes = "[\"*\"]",
        string allowedMcpTools = "[\"*\"]",
        string sourceScopeMode = "all",
        string allowedSources = "[]",
        DateTime? expiresAt = null)
    {
        await InitializeAsync();

        // 统一格式: rag_live_<32位安全随机十六进制>
        var rawKey = $"rag_live_{Guid.NewGuid():N}";
        var keyPrefix = rawKey[..15]; // "rag_live_" + 6 字符
        var keyHash = ComputeHash(rawKey);

        var entity = new ApiKeyEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? "新 API Key" : name.Trim(),
            KeyPrefix = keyPrefix,
            KeyHash = keyHash,
            IsEnabled = true,
            AllowApi = allowApi,
            AllowMcp = allowMcp,
            AllowedScopes = string.IsNullOrWhiteSpace(allowedScopes) ? "[\"*\"]" : allowedScopes.Trim(),
            AllowedMcpTools = string.IsNullOrWhiteSpace(allowedMcpTools) ? "[\"*\"]" : allowedMcpTools.Trim(),
            SourceScopeMode = string.Equals(sourceScopeMode, "custom", StringComparison.OrdinalIgnoreCase) ? "custom" : "all",
            AllowedSources = string.IsNullOrWhiteSpace(allowedSources) ? "[]" : allowedSources.Trim(),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt
        };

        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO api_keys (
                    id, name, key_prefix, key_hash, is_enabled, allow_api, allow_mcp,
                    allowed_scopes, allowed_mcp_tools, source_scope_mode, allowed_sources,
                    created_at, last_used_at, expires_at
                ) VALUES (
                    @id, @name, @key_prefix, @key_hash, @is_enabled, @allow_api, @allow_mcp,
                    @allowed_scopes, @allowed_mcp_tools, @source_scope_mode, @allowed_sources,
                    @created_at, @last_used_at, @expires_at
                );
            ";
            cmd.Parameters.AddWithValue("@id", entity.Id);
            cmd.Parameters.AddWithValue("@name", entity.Name);
            cmd.Parameters.AddWithValue("@key_prefix", entity.KeyPrefix);
            cmd.Parameters.AddWithValue("@key_hash", entity.KeyHash);
            cmd.Parameters.AddWithValue("@is_enabled", entity.IsEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@allow_api", entity.AllowApi ? 1 : 0);
            cmd.Parameters.AddWithValue("@allow_mcp", entity.AllowMcp ? 1 : 0);
            cmd.Parameters.AddWithValue("@allowed_scopes", entity.AllowedScopes);
            cmd.Parameters.AddWithValue("@allowed_mcp_tools", entity.AllowedMcpTools);
            cmd.Parameters.AddWithValue("@source_scope_mode", entity.SourceScopeMode);
            cmd.Parameters.AddWithValue("@allowed_sources", entity.AllowedSources);
            cmd.Parameters.AddWithValue("@created_at", entity.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@last_used_at", (object?)entity.LastUsedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@expires_at", (object?)entity.ExpiresAt?.ToString("O") ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync();

            _cachedByHash[entity.KeyHash] = entity;
            _cachedById[entity.Id] = entity;
            _logger?.LogInformation("成功创建 API Key: {Name} ({Prefix}...) [ID: {Id}]", entity.Name, entity.KeyPrefix, entity.Id);
        }
        finally
        {
            _lock.Release();
        }

        return (entity, rawKey);
    }

    public async Task<bool> UpdateAsync(
        string id,
        string name,
        bool allowApi,
        bool allowMcp,
        string allowedScopes,
        string allowedMcpTools,
        string sourceScopeMode,
        string allowedSources,
        DateTime? expiresAt = null)
    {
        await InitializeAsync();
        if (!_cachedById.TryGetValue(id, out var entity)) return false;

        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                UPDATE api_keys SET
                    name = @name,
                    allow_api = @allow_api,
                    allow_mcp = @allow_mcp,
                    allowed_scopes = @allowed_scopes,
                    allowed_mcp_tools = @allowed_mcp_tools,
                    source_scope_mode = @source_scope_mode,
                    allowed_sources = @allowed_sources,
                    expires_at = @expires_at
                WHERE id = @id;
            ";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", name.Trim());
            cmd.Parameters.AddWithValue("@allow_api", allowApi ? 1 : 0);
            cmd.Parameters.AddWithValue("@allow_mcp", allowMcp ? 1 : 0);
            cmd.Parameters.AddWithValue("@allowed_scopes", allowedScopes);
            cmd.Parameters.AddWithValue("@allowed_mcp_tools", allowedMcpTools);
            cmd.Parameters.AddWithValue("@source_scope_mode", sourceScopeMode);
            cmd.Parameters.AddWithValue("@allowed_sources", allowedSources);
            cmd.Parameters.AddWithValue("@expires_at", (object?)expiresAt?.ToString("O") ?? DBNull.Value);

            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows > 0)
            {
                entity.Name = name.Trim();
                entity.AllowApi = allowApi;
                entity.AllowMcp = allowMcp;
                entity.AllowedScopes = allowedScopes;
                entity.AllowedMcpTools = allowedMcpTools;
                entity.SourceScopeMode = sourceScopeMode;
                entity.AllowedSources = allowedSources;
                entity.ExpiresAt = expiresAt;
                return true;
            }
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> ToggleAsync(string id, bool isEnabled)
    {
        await InitializeAsync();
        if (!_cachedById.TryGetValue(id, out var entity)) return false;

        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE api_keys SET is_enabled = @enabled WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@enabled", isEnabled ? 1 : 0);

            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows > 0)
            {
                entity.IsEnabled = isEnabled;
                return true;
            }
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id)
    {
        await InitializeAsync();
        if (!_cachedById.TryGetValue(id, out var entity)) return false;

        await _lock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection($"Data Source={_dbPath}");
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM api_keys WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);

            var rows = await cmd.ExecuteNonQueryAsync();
            if (rows > 0)
            {
                _cachedById.TryRemove(id, out _);
                _cachedByHash.TryRemove(entity.KeyHash, out _);
                return true;
            }
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void RecordLastUsed(string id)
    {
        if (_cachedById.TryGetValue(id, out var entity))
        {
            var now = DateTime.UtcNow;
            entity.LastUsedAt = now;

            // 异步离线落盘
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var conn = new SqliteConnection($"Data Source={_dbPath}");
                    await conn.OpenAsync();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE api_keys SET last_used_at = @now WHERE id = @id;";
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@now", now.ToString("O"));
                    await cmd.ExecuteNonQueryAsync();
                }
                catch { /* 忽略瞬态记录更新异常 */ }
            });
        }
    }

    private static ApiKeyEntity ReadRow(SqliteDataReader reader)
    {
        return new ApiKeyEntity
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            KeyPrefix = reader.GetString(2),
            KeyHash = reader.GetString(3),
            IsEnabled = reader.GetInt32(4) == 1,
            AllowApi = reader.GetInt32(5) == 1,
            AllowMcp = reader.GetInt32(6) == 1,
            AllowedScopes = reader.GetString(7),
            AllowedMcpTools = reader.GetString(8),
            SourceScopeMode = reader.GetString(9),
            AllowedSources = reader.GetString(10),
            CreatedAt = DateTime.Parse(reader.GetString(11)),
            LastUsedAt = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
            ExpiresAt = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13))
        };
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
