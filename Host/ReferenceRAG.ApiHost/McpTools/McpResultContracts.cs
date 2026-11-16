using System.Collections.Generic;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// list_files 的强类型响应契约：仅用于生成 outputSchema，
/// 让客户端能按字段取结构化结果而不是从文本里二次解析。
/// 字段必须与 McpResult.Ok 输出的匿名对象保持同名（camelCase）。
/// </summary>
public sealed class ListFilesResult
{
    public bool Success { get; set; }
    public ListFilesAppliedFilters AppliedFilters { get; set; } = new();
    public int Count { get; set; }
    public int TotalMatched { get; set; }
    public bool Truncated { get; set; }
    public List<ListFilesItem> Files { get; set; } = new();
}

public sealed class ListFilesAppliedFilters
{
    public string? ConnectorId { get; set; }
    public string? PathKeyword { get; set; }
}

public sealed class ListFilesItem
{
    public string KnowledgeBaseId { get; set; } = "";
    public string KnowledgeBaseName { get; set; } = "";
    public string ConnectorId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string? Locator { get; set; }
    public int ChunkCount { get; set; }
    public string UpdatedAt { get; set; } = "";
}
