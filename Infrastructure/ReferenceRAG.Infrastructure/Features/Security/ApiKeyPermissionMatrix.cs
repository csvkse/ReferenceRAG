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

    public static bool IsMcpToolPermitted(ApiKeyEntity key, string toolName)
    {
        if (!key.AllowMcp) return false;
        if (string.IsNullOrWhiteSpace(key.AllowedMcpTools)) return false;
        var tools = ParseJsonOrTokens(key.AllowedMcpTools);
        if (tools.Contains("*")) return true;
        return tools.Contains(toolName, StringComparer.OrdinalIgnoreCase);
    }

    public static HashSet<string> ParseJsonOrTokens(string input)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(input)) return set;
        try
        {
            if (input.TrimStart().StartsWith('['))
            {
                var list = JsonSerializer.Deserialize<List<string>>(input);
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

        foreach (var token in input.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = token.Trim().Trim('"', '[', ']', '\'', ' ');
            if (!string.IsNullOrEmpty(clean)) set.Add(clean);
        }
        return set;
    }
}
