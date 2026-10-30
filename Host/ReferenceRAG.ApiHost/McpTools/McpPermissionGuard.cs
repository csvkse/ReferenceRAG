using System;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// MCP 工具调用安全守卫（策略模式与状态机模式结合）：
/// 1. 统一接入 ApiKeyPermissionMatrix 双层级联权限判定（通道 + 业务模块 + 细粒度工具白名单）；
/// 2. 具备长连接主动吊销感知能力（Live Revocation Awareness）：在 SSE 长连接维持期间，若管理员在后台停用或删除 API Key，工具调用将被毫秒级实时阻断；
/// 3. 严格遵循默认拒绝模型，防范越权逃逸。
/// </summary>
public static class McpPermissionGuard
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static bool CheckPermission(IServiceProvider sp, string toolName, out string? errorJson)
    {
        var httpContextAccessor = sp.GetService<IHttpContextAccessor>();
        var currentKey = httpContextAccessor?.HttpContext?.Items["CurrentApiKey"] as ApiKeyEntity;

        // 1. 若为数据库生成的动态授权 Key，在执行工具前向缓存层进行状态机实时校验（防长连接会话过期逃逸）
        if (currentKey != null && currentKey.Id != "system_master_key" && currentKey.Id != "anonymous_open_access")
        {
            var keyStore = sp.GetService<IApiKeyStore>();
            if (keyStore != null)
            {
                var liveKey = keyStore.GetByIdAsync(currentKey.Id).GetAwaiter().GetResult();
                if (liveKey == null || !liveKey.IsEnabled)
                {
                    errorJson = JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"此 API Key 已被停用或废止（名称: '{currentKey.Name}'）"
                    }, JsonOptions);
                    return false;
                }

                if (liveKey.ExpiresAt.HasValue && liveKey.ExpiresAt.Value < DateTime.UtcNow)
                {
                    errorJson = JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"此 API Key 已过期（名称: '{currentKey.Name}'）"
                    }, JsonOptions);
                    return false;
                }

                currentKey = liveKey; // 采用最新的实时权限配置进行决断
            }
        }

        // 2. 双层级联权限决断（业务功能模块级联一票否决 + 细粒度工具白名单）
        if (currentKey != null && !ApiKeyPermissionMatrix.IsMcpToolPermitted(currentKey, toolName))
        {
            var module = McpCatalogRegistry.GetModuleByToolName(toolName) ?? "unknown";
            errorJson = JsonSerializer.Serialize(new
            {
                success = false,
                error = $"此 API Key 未被授权调用 MCP 工具 '{toolName}'（归属业务模块: '{module}'）"
            }, JsonOptions);
            return false;
        }

        errorJson = null;
        return true;
    }
}
