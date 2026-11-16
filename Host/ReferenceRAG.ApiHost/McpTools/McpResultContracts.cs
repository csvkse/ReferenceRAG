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

/// <summary>knowledge_base_list 的响应契约（outputSchema 来源）。</summary>
public sealed class KnowledgeBaseListResult
{
    public bool Success { get; set; }
    public int Count { get; set; }
    public List<KnowledgeBaseListItem> KnowledgeBases { get; set; } = new();
}

public sealed class KnowledgeBaseListItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; }
    public int DesiredConfigRevision { get; set; }
    public int EffectiveConfigRevision { get; set; }
    public string EmbeddingSpecId { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

/// <summary>list_sources 的响应契约（outputSchema 来源）。</summary>
public sealed class ListSourcesResult
{
    public bool Success { get; set; }
    public int KnowledgeBaseCount { get; set; }
    public int Count { get; set; }
    public List<ListSourcesItem> Sources { get; set; } = new();
}

public sealed class ListSourcesItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Enabled { get; set; }
    public string Origin { get; set; } = "";
    public string? KnowledgeBaseId { get; set; }
    public string? ConnectorId { get; set; }
    public int FileCount { get; set; }
    public int ChunkCount { get; set; }
}

/// <summary>get_index_status 的响应契约（outputSchema 来源）。</summary>
public sealed class IndexStatusResult
{
    public bool Success { get; set; }
    public int KnowledgeBaseCount { get; set; }
    public int TotalChunks { get; set; }
    public int TotalDocuments { get; set; }
    public int TotalVectors { get; set; }
    public List<IndexStatusPerKb> KnowledgeBases { get; set; } = new();
    public string LastUpdated { get; set; } = "";
}

public sealed class IndexStatusPerKb
{
    public string KnowledgeBaseId { get; set; } = "";
    public string Name { get; set; } = "";
    public int ChunkCount { get; set; }
    public int DocumentCount { get; set; }
    public int VectorCount { get; set; }
    public int VocabularySize { get; set; }
    public string EmbeddingSpecId { get; set; } = "";
    public string Status { get; set; } = "";
}

/// <summary>knowledge_base_search 的响应契约（outputSchema 来源）。</summary>
public sealed class KnowledgeBaseSearchResult
{
    public bool Success { get; set; }
    public string Query { get; set; } = "";
    public int RequestedTopK { get; set; }
    public int EffectiveTopK { get; set; }
    public int Count { get; set; }
    public string ScoreKind { get; set; } = "";
    public double ElapsedMs { get; set; }
    public List<string> SuccessfulKbs { get; set; } = new();
    public List<string> FailedKbs { get; set; } = new();
    public List<KnowledgeBaseSearchItem> Items { get; set; } = new();
}

public sealed class KnowledgeBaseSearchItem
{
    public string ChunkId { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string Text { get; set; } = "";
    public double Score { get; set; }
    public string ScoreKind { get; set; } = "";
    public long DocumentVersion { get; set; }
    public string? ConnectorId { get; set; }
}

/// <summary>get_document_info 的响应契约（outputSchema 来源）。</summary>
public sealed class DocumentInfoResult
{
    public bool Success { get; set; }
    public string? KnowledgeBaseId { get; set; }
    public DocumentInfoChunk Chunk { get; set; } = new();
    public DocumentInfoFile? File { get; set; }
}

public sealed class DocumentInfoChunk
{
    public string Id { get; set; } = "";
    public string DocumentId { get; set; } = "";
    public int Ordinal { get; set; }
    public string Content { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? HeadingPath { get; set; }
}

public sealed class DocumentInfoFile
{
    public string Path { get; set; } = "";
}

/// <summary>get_system_stats 的响应契约：只声明顶层，嵌套配置按 object 泛化。</summary>
public sealed class SystemStatsResult
{
    public bool Success { get; set; }
    public Dictionary<string, object?> System { get; set; } = new();
    public Dictionary<string, object?> Embedding { get; set; } = new();
    public Dictionary<string, object?> Search { get; set; } = new();
}
