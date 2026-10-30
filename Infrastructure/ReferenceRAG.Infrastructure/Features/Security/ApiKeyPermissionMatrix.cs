using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// API Key 细粒度权限矩阵决断器：
/// - 统一控制 API 与 MCP 双通道协议访问；
/// - 提供细粒度模块能力决断（Chat, Search, Sources, Files, Index, Models, System）；
/// - 提供数据源级 ABAC 资源隔离与下推过滤器；
/// - 提供 MCP 工具动态投影过滤断言。
/// </summary>
public static class ApiKeyPermissionMatrix
{
    public static bool CanAccessChannel(ApiKeyEntity key, string channel)
    {
        return channel.ToLowerInvariant() switch
        {
            "api" => key.AllowApi,
            "mcp" => key.AllowMcp,
            _ => false
        };
    }

    public static bool HasScope(ApiKeyEntity key, string scope)
    {
        if (string.IsNullOrWhiteSpace(key.AllowedScopes)) return false;
        var scopes = ParseJsonOrTokens(key.AllowedScopes);
        if (scopes.Contains("*")) return true;

        // 1. 直接精确匹配
        if (scopes.Contains(scope, StringComparer.OrdinalIgnoreCase)) return true;

        // 2. 如果入参是具体动作（如 "chat:completions"），且权限集包含粗粒度模块（如 "chat"），放行
        var colonIdx = scope.IndexOf(':');
        if (colonIdx > 0 && scopes.Contains(scope[..colonIdx], StringComparer.OrdinalIgnoreCase)) return true;
        var slashIdx = scope.IndexOf('/');
        if (slashIdx > 0 && scopes.Contains(scope[..slashIdx], StringComparer.OrdinalIgnoreCase)) return true;

        // 反向：如果入参是模块（如 "chat"），且权限集包含该模块下的任意细粒度动作（以 "chat:" 或 "chat/" 开头），放行
        if (scopes.Any(s => s.StartsWith(scope + ":", StringComparison.OrdinalIgnoreCase) || s.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    public static bool CanAccessSource(ApiKeyEntity key, string sourceName)
    {
        if (string.Equals(key.SourceScopeMode, "all", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(sourceName)) return false;
        var allowed = ParseJsonOrTokens(key.AllowedSources);
        return allowed.Contains("*") || allowed.Contains(sourceName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 获取当前 Key 限制的有效知识源集合。若返回 null，代表允许全库无限制检索。
    /// </summary>
    public static IReadOnlyList<string>? GetEffectiveAllowedSources(ApiKeyEntity? key)
    {
        if (key == null) return null;
        if (string.Equals(key.SourceScopeMode, "all", StringComparison.OrdinalIgnoreCase))
            return null;

        var sources = ParseJsonOrTokens(key.AllowedSources);
        if (sources.Contains("*")) return null;
        return sources.ToList();
    }

    /// <summary>
    /// 依据双层权限（所属业务功能模块 + 细粒度工具白名单）决断是否允许调用 MCP 工具
    /// 遵循 WebToolForLinux 架构方案：按业务功能模块划分，统一授权 API 与 MCP。
    /// 1. 若 AllowMcp = false，直接拒绝；
    /// 2. 检查 toolName 所属的业务功能模块，若 API Key 未被授予该模块权限（!HasScope(key, module)），直接拒绝（一票否决）；
    /// 3. 若所属模块已授权，再校验细粒度工具白名单（允许 "*" 通配或具体工具名）。
    /// </summary>
    public static bool IsMcpToolPermitted(ApiKeyEntity key, string toolName)
    {
        if (!key.AllowMcp) return false;

        // 1. 业务功能模块级联校验 (核心安全屏障：模块未授权则其下所有工具禁止访问)
        var module = McpCatalogRegistry.GetModuleByToolName(toolName);
        if (!string.IsNullOrEmpty(module) && !HasScope(key, module))
        {
            return false;
        }

        // 2. 细粒度工具白名单过滤 (向下兼容与细分授权)
        if (string.IsNullOrWhiteSpace(key.AllowedMcpTools)) return false;
        var tools = ParseJsonOrTokens(key.AllowedMcpTools);
        if (tools.Contains("*")) return true;
        return tools.Contains(toolName, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<string>> TokenCache = new(StringComparer.Ordinal);
    private const int MaxCacheSize = 1024;

    /// <summary>
    /// 解析 JSON 数组或分隔符字符串为大小写不敏感的 HashSet
    /// 引入享元/记忆化模式（Flyweight / Memoization Pattern）缓存解析结果，避免高并发热路径上频繁 JSON 反序列化产生 GC 压力
    /// </summary>
    public static HashSet<string> ParseJsonOrTokens(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trimmed = input.Trim();
        if (TokenCache.TryGetValue(trimmed, out var cached))
        {
            return cached;
        }

        var parsed = ParseJsonOrTokensCore(trimmed);
        if (TokenCache.Count < MaxCacheSize)
        {
            TokenCache.TryAdd(trimmed, parsed);
        }
        return parsed;
    }

    private static HashSet<string> ParseJsonOrTokensCore(string trimmed)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (trimmed.StartsWith('['))
            {
                var list = JsonSerializer.Deserialize<List<string>>(trimmed);
                if (list != null)
                {
                    foreach (var item in list)
                    {
                        if (!string.IsNullOrWhiteSpace(item)) set.Add(item.Trim());
                    }
                    return set;
                }
            }
        }
        catch { }

        foreach (var token in trimmed.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = token.Trim().Trim('"', '[', ']', '\'', ' ');
            if (!string.IsNullOrEmpty(clean)) set.Add(clean);
        }
        return set;
    }

    /// <summary>
    /// 清空解析缓存（用于高频基准测试或单元测试重置）
    /// </summary>
    public static void ClearTokenCache()
    {
        TokenCache.Clear();
    }
}
