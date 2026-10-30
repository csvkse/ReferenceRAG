using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public void McpTool_WhenModuleScopeDenied_MustBeRejected_EvenIfToolWhitelistIsWildcard()
    {
        // 核心安全漏洞防御测试：
        // 即使密钥配置了 AllowedMcpTools = "*"（工具全通配），
        // 如果该密钥的 AllowedScopes 未包含对应的业务功能模块（如 search / files / sources / index / system），
        // 级联校验必须一票否决，彻底阻断跨模块越权穿透！
        var restrictedKey = new ApiKeyEntity
        {
            AllowApi = true,
            AllowMcp = true,
            AllowedScopes = "[\"chat\", \"models\"]", // 仅授予 chat 和 models 模块
            AllowedMcpTools = "[\"*\"]",              // 攻击场景：工具白名单通配
            SourceScopeMode = "all"
        };

        // search 模块下的工具必须被阻断
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(restrictedKey, "SemanticSearch"));
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(restrictedKey, "HybridSearch"));
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(restrictedKey, "EmbedText"));

        // files 模块下的工具必须被阻断
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(restrictedKey, "GetFileTree"));
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(restrictedKey, "ReadNoteContent"));

        // sources 模块下的工具必须被阻断
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(restrictedKey, "ListSources"));

        // index 模块下的工具必须被阻断
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(restrictedKey, "GetIndexStatus"));

        // system 模块下的工具必须被阻断
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(restrictedKey, "Ping"));
    }

    [Fact]
    public void McpTool_WhenModuleScopeGranted_AndToolWhitelisted_Succeeds()
    {
        // 统一业务功能模块授权 + 二级工具细粒度白名单双层放行测试
        var fineGrainedKey = new ApiKeyEntity
        {
            AllowApi = true,
            AllowMcp = true,
            AllowedScopes = "[\"search\", \"files\"]", // 授予 search 与 files 模块
            AllowedMcpTools = "[\"SemanticSearch\", \"GetFileTree\"]", // 细粒度只允许两个特定工具
            SourceScopeMode = "all"
        };

        // 已授权模块 + 已白名单工具 -> 放行
        Assert.True(ApiKeyPermissionMatrix.IsMcpToolPermitted(fineGrainedKey, "SemanticSearch"));
        Assert.True(ApiKeyPermissionMatrix.IsMcpToolPermitted(fineGrainedKey, "GetFileTree"));

        // 已授权模块但未在白名单的工具 -> 阻断
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(fineGrainedKey, "HybridSearch"));
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(fineGrainedKey, "ReadNoteContent"));

        // 未授权模块的工具 -> 阻断
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(fineGrainedKey, "ListSources"));
    }

    [Fact]
    public void McpTool_WhenAllowMcpIsFalse_AlwaysDenied_RegardlessOfScopes()
    {
        // 通道级一票否决：如果 AllowMcp = false，即使模块和工具全通配也绝对拒绝
        var apiOnlyKey = new ApiKeyEntity
        {
            AllowApi = true,
            AllowMcp = false,
            AllowedScopes = "[\"*\"]",
            AllowedMcpTools = "[\"*\"]"
        };

        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(apiOnlyKey, "SemanticSearch"));
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(apiOnlyKey, "GetFileTree"));
        Assert.False(ApiKeyPermissionMatrix.IsMcpToolPermitted(apiOnlyKey, "Ping"));
    }

    [Fact]
    public void AllRegisteredMcpTools_MustHaveValidModuleMapping_InCatalogRegistry()
    {
        // 架构守护门禁 (Architecture Gate)：
        // 自动反射扫描宿主程序集中所有标记为 [McpServerToolType] 类下的 [McpServerTool] 方法，
        // 强制断言每一个 MCP 工具都必须在 McpCatalogRegistry 中存在明确的业务功能模块映射！
        // 杜绝任何未来新增的 MCP 工具脱管逃逸或遗漏配置。
        var toolTypes = typeof(ReferenceRAG.Service.McpTools.RagSearchTools).Assembly
            .GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolTypeAttribute), inherit: true).Any())
            .ToList();

        Assert.NotEmpty(toolTypes);

        var toolMethods = toolTypes
            .SelectMany(t => t.GetMethods())
            .Where(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), inherit: true).Any())
            .ToList();

        Assert.NotEmpty(toolMethods);

        foreach (var method in toolMethods)
        {
            var toolName = method.Name;
            var mappedModule = McpCatalogRegistry.GetModuleByToolName(toolName);

            Assert.False(
                string.IsNullOrWhiteSpace(mappedModule),
                $"[架构门禁失败] MCP 工具 '{toolName}' 在 {method.DeclaringType?.Name} 中定义，但在 McpCatalogRegistry 中缺少业务模块映射！请在 McpCatalogRegistry.cs 中将其挂载至对应功能模块。");

            Assert.True(
                McpCatalogRegistry.IsKnownTool(toolName),
                $"[架构门禁失败] McpCatalogRegistry.IsKnownTool('{toolName}') 应返回 true！");
        }
    }

    [Fact]
    public void McpCatalogRegistry_Integrity_AllToolsHaveValidPropertiesAndRiskLevel()
    {
        var catalog = McpCatalogRegistry.GetCatalog();
        Assert.NotNull(catalog);
        Assert.True(catalog.Count >= 6, "系统应至少包含 chat, search, files, sources, index, models, system 核心功能模块");

        var validRiskLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Read", "Execute", "Dangerous" };

        foreach (var category in catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(category.Key), "分类 Key 不能为空");
            Assert.False(string.IsNullOrWhiteSpace(category.Name), "分类 Name 不能为空");
            Assert.False(string.IsNullOrWhiteSpace(category.Description), "分类 Description 不能为空");
            Assert.False(string.IsNullOrWhiteSpace(category.ModuleCode), "分类 ModuleCode 不能为空");

            foreach (var tool in category.Tools)
            {
                Assert.False(string.IsNullOrWhiteSpace(tool.Name), "工具 Name 不能为空");
                Assert.False(string.IsNullOrWhiteSpace(tool.Description), "工具 Description 不能为空");
                Assert.Contains(tool.RiskLevel, validRiskLevels);
                Assert.Equal(category.ModuleCode, tool.ModuleCode);
            }
        }
    }

    [Fact]
    public void ApiKeyPermissionMatrix_TokenCache_Performance_And_Consistency()
    {
        // 享元/记忆化模式（Flyweight / Memoization Pattern）验证：
        // 验证相同输入字符串多次调用解析时，命中内部 ConcurrentDictionary 缓存，
        // 消除 JSON 反序列化开销与 GC 压力，并确保解析结果一致。
        ApiKeyPermissionMatrix.ClearTokenCache();
        var json = "[\"search\", \"files\", \"sources\"]";

        var set1 = ApiKeyPermissionMatrix.ParseJsonOrTokens(json);
        var set2 = ApiKeyPermissionMatrix.ParseJsonOrTokens(json);

        Assert.NotNull(set1);
        Assert.Same(set1, set2); // 引用同一享元缓存实例
        Assert.Equal(3, set1.Count);
        Assert.Contains("search", set1);
        Assert.Contains("files", set1);
        Assert.Contains("sources", set1);
    }

    [Fact]
    public async Task McpPermissionGuard_LiveRevocation_RejectsDisabledKeyInSseSession()
    {
        // 状态机与主动废止感知（Live Revocation Awareness）测试：
        // 模拟 SSE 长连接开启时凭据合法，但在连接持续期间，管理员在后台停用或删除了该 Key。
        // McpPermissionGuard 在执行具体工具时必须能够感知存储层最新状态，立即阻断后续工具调用！
        var tempDb = Path.Combine(Path.GetTempPath(), $"rrag_guard_test_{Guid.NewGuid():N}.db");
        try
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            var store = new ApiKeyStore(tempDb, NullLogger<ApiKeyStore>.Instance);
            await store.InitializeAsync();

            var (keyEntity, rawKey) = await store.CreateAsync(
                name: "Cursor SSE Agent",
                allowApi: true,
                allowMcp: true,
                allowedScopes: "[\"search\"]",
                allowedMcpTools: "[\"SemanticSearch\"]"
            );

            services.AddSingleton<IApiKeyStore>(store);

            var httpContext = new DefaultHttpContext();
            httpContext.Items["CurrentApiKey"] = keyEntity; // 模拟 SSE 握手时注入的快照
            var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
            services.AddSingleton<IHttpContextAccessor>(httpContextAccessor);

            var sp = services.BuildServiceProvider();

            // 1. 密钥处于启用状态，工具调用放行
            var pass = ReferenceRAG.Service.McpTools.McpPermissionGuard.CheckPermission(sp, "SemanticSearch", out var err1);
            Assert.True(pass);
            Assert.Null(err1);

            // 2. 管理员在后台停用该 Key（状态迁移：Active -> Disabled）
            await store.ToggleAsync(keyEntity.Id, false);

            // 3. SSE 长连接未断开，但下一次工具调用必须被实时拦截！
            var blocked = ReferenceRAG.Service.McpTools.McpPermissionGuard.CheckPermission(sp, "SemanticSearch", out var err2);
            Assert.False(blocked);
            Assert.NotNull(err2);
            Assert.Contains("停用或废止", err2);

            // 4. 超级管理员主密钥（system_master_key）免受影响
            httpContext.Items["CurrentApiKey"] = new ApiKeyEntity
            {
                Id = "system_master_key",
                Name = "系统静态主密钥",
                IsEnabled = true,
                AllowMcp = true,
                AllowedScopes = "[\"*\"]",
                AllowedMcpTools = "[\"*\"]"
            };
            var masterPass = ReferenceRAG.Service.McpTools.McpPermissionGuard.CheckPermission(sp, "SemanticSearch", out var masterErr);
            Assert.True(masterPass);
            Assert.Null(masterErr);

            // 5. 空对象模式（anonymous_open_access 免密模式）放行
            httpContext.Items["CurrentApiKey"] = new ApiKeyEntity
            {
                Id = "anonymous_open_access",
                Name = "免密开放访问",
                IsEnabled = true,
                AllowMcp = true,
                AllowedScopes = "[\"*\"]",
                AllowedMcpTools = "[\"*\"]"
            };
            var anonPass = ReferenceRAG.Service.McpTools.McpPermissionGuard.CheckPermission(sp, "SemanticSearch", out var anonErr);
            Assert.True(anonPass);
            Assert.Null(anonErr);
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }
}
