using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

public interface IApiKeyStore
{
    Task InitializeAsync();
    ApiKeyEntity? FindByHash(string keyHash);
    Task<IReadOnlyList<ApiKeyEntity>> GetAllAsync();
    Task<ApiKeyEntity?> GetByIdAsync(string id);
    Task<(ApiKeyEntity entity, string rawKey)> CreateAsync(
        string name,
        bool allowApi = true,
        bool allowMcp = true,
        string allowedScopes = "[\"*\"]",
        string allowedMcpTools = "[\"*\"]",
        string sourceScopeMode = "all",
        string allowedSources = "[]",
        DateTime? expiresAt = null);
    Task<bool> UpdateAsync(
        string id,
        string name,
        bool allowApi,
        bool allowMcp,
        string allowedScopes,
        string allowedMcpTools,
        string sourceScopeMode,
        string allowedSources,
        DateTime? expiresAt = null);
    Task<bool> ToggleAsync(string id, bool isEnabled);
    Task<bool> DeleteAsync(string id);
    void RecordLastUsed(string id);
}
