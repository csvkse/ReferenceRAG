using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.Security;
using ReferenceRAG.Service.Middleware;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public sealed class ApiKeyAuthorizationTests
{
    [Fact]
    public async Task ApiKeyStore_Create_FindByHash_Toggle_Delete_Lifecycle_Works()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"rrag_auth_test_{Guid.NewGuid():N}.db");
        try
        {
            using var store = new ApiKeyStore(tempDb, NullLogger<ApiKeyStore>.Instance);
            await store.InitializeAsync();

            // 1. Create Key
            var (created, rawKey) = await store.CreateAsync(
                name: "Agent Test Key",
                allowApi: true,
                allowMcp: false,
                allowedScopes: "[\"chat\", \"search\"]",
                allowedMcpTools: "[\"SemanticSearch\"]",
                sourceScopeMode: "custom",
                allowedSources: "[\"TechDoc\"]"
            );

            Assert.NotNull(created);
            Assert.StartsWith("rag_live_", rawKey);
            Assert.StartsWith("rag_live_", created.KeyPrefix);
            Assert.False(string.IsNullOrWhiteSpace(created.KeyHash));
            Assert.NotEqual(rawKey, created.KeyHash); // Ensure plaintext is not stored

            // 2. FindByHash
            var computedHash = ApiKeyStore.ComputeHash(rawKey);
            var found = store.FindByHash(computedHash);
            Assert.NotNull(found);
            Assert.Equal(created.Id, found.Id);
            Assert.True(found.AllowApi);
            Assert.False(found.AllowMcp);

            // 3. Toggle
            var toggled = await store.ToggleAsync(created.Id, false);
            Assert.True(toggled);
            var disabled = store.FindByHash(computedHash);
            Assert.NotNull(disabled);
            Assert.False(disabled.IsEnabled);

            // 4. Update
            var updated = await store.UpdateAsync(
                id: created.Id,
                name: "Updated Key Name",
                allowApi: true,
                allowMcp: true,
                allowedScopes: "[\"*\"]",
                allowedMcpTools: "[\"*\"]",
                sourceScopeMode: "all",
                allowedSources: "[]"
            );
            Assert.True(updated);
            var reloaded = store.FindByHash(computedHash);
            Assert.NotNull(reloaded);
            Assert.Equal("Updated Key Name", reloaded.Name);
            Assert.True(reloaded.AllowMcp);

            // 5. Delete
            var deleted = await store.DeleteAsync(created.Id);
            Assert.True(deleted);
            var missing = store.FindByHash(computedHash);
            Assert.Null(missing);
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    [Fact]
    public void ApiKeyPermissionMatrix_EvaluatesChannels_Scopes_And_Sources_Accurately()
    {
        var key = new ApiKeyEntity
        {
            AllowApi = true,
            AllowMcp = false,
            AllowedScopes = "[\"search\", \"chat:completions\"]",
            AllowedMcpTools = "[\"SemanticSearch\"]",
            SourceScopeMode = "custom",
            AllowedSources = "[\"PublicDoc\", \"ArchGuide\"]"
        };

        // Channels
        Assert.True(ApiKeyPermissionMatrix.CanAccessChannel(key, "api"));
        Assert.False(ApiKeyPermissionMatrix.CanAccessChannel(key, "mcp"));

        // Scopes
        Assert.True(ApiKeyPermissionMatrix.HasScope(key, "search"));
        Assert.True(ApiKeyPermissionMatrix.HasScope(key, "chat")); // Prefix matches chat:completions
        Assert.True(ApiKeyPermissionMatrix.HasScope(key, "chat:completions"));
        Assert.False(ApiKeyPermissionMatrix.HasScope(key, "system"));
        Assert.False(ApiKeyPermissionMatrix.HasScope(key, "models"));

        // Source Isolation
        Assert.True(ApiKeyPermissionMatrix.CanAccessSource(key, "PublicDoc"));
        Assert.True(ApiKeyPermissionMatrix.CanAccessSource(key, "ArchGuide"));
        Assert.False(ApiKeyPermissionMatrix.CanAccessSource(key, "ConfidentialHR"));

        var effectiveSources = ApiKeyPermissionMatrix.GetEffectiveAllowedSources(key);
        Assert.NotNull(effectiveSources);
        Assert.Equal(2, effectiveSources.Count);
        Assert.Contains("PublicDoc", effectiveSources);

        // Wildcard Source Isolation
        var wildcardKey = new ApiKeyEntity
        {
            SourceScopeMode = "all"
        };
        Assert.Null(ApiKeyPermissionMatrix.GetEffectiveAllowedSources(wildcardKey));
        Assert.True(ApiKeyPermissionMatrix.CanAccessSource(wildcardKey, "AnyRandomSource"));
    }
}
