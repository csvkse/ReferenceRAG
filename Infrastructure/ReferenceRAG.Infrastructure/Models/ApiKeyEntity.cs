using System;

namespace ReferenceRAG.Core.Models;

/// <summary>
/// API Key 实体：支持 REST API 与 MCP 双通道控制、树状细粒度功能授权及数据源级隔离。
/// </summary>
public sealed class ApiKeyEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;

    // 通道权限 (Channels)
    public bool AllowApi { get; set; } = true;
    public bool AllowMcp { get; set; } = true;

    // 细粒度业务模块权限 (JSON 数组，如 ["chat", "search", "sources", "files", "index", "models", "system"] 或 ["*"])
    public string AllowedScopes { get; set; } = "[\"*\"]";

    // MCP 工具白名单 (JSON 数组，如 ["SemanticSearch", "HybridSearch"] 或 ["*"])
    public string AllowedMcpTools { get; set; } = "[\"*\"]";

    // 数据源隔离模式: "all" (全部知识源) | "custom" (限制特定知识源)
    public string SourceScopeMode { get; set; } = "all";

    // 授权的知识源名称/标识 (JSON 数组，如 ["VNote", "TechDoc"])
    public string AllowedSources { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}
