namespace ObsidianRAG.Core.Models;

/// <summary>
/// AI 查询请求
/// </summary>
public class AIQueryRequest
{
    /// <summary>
    /// 查询文本
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// 查询模式
    /// </summary>
    public QueryMode Mode { get; set; } = QueryMode.Standard;

    /// <summary>
    /// 返回结果数量
    /// </summary>
    public int TopK { get; set; } = 10;

    /// <summary>
    /// 上下文窗口大小
    /// </summary>
    public int ContextWindow { get; set; } = 1;

    /// <summary>
    /// 最大 token 数
    /// </summary>
    public int MaxTokens { get; set; } = 3000;

    /// <summary>
    /// 限定搜索的源（空=全部）
    /// </summary>
    public List<string>? Sources { get; set; }

    /// <summary>
    /// 过滤条件
    /// </summary>
    public SearchFilter? Filters { get; set; }

    /// <summary>
    /// 选项
    /// </summary>
    public QueryOptions? Options { get; set; }
}

/// <summary>
/// AI 查询响应
/// </summary>
public class AIQueryResponse
{
    /// <summary>
    /// 原始查询
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// 查询模式
    /// </summary>
    public QueryMode Mode { get; set; }

    /// <summary>
    /// 组装好的上下文
    /// </summary>
    public string Context { get; set; } = string.Empty;

    /// <summary>
    /// 生成的 Prompt
    /// </summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>
    /// 匹配的分段列表
    /// </summary>
    public List<ChunkResult> Chunks { get; set; } = new();

    /// <summary>
    /// 相关文件列表
    /// </summary>
    public List<FileSummary> Files { get; set; } = new();

    /// <summary>
    /// 统计信息
    /// </summary>
    public SearchStats Stats { get; set; } = new();

    /// <summary>
    /// 是否有更多结果
    /// </summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// 建议
    /// </summary>
    public string? Suggestion { get; set; }
}

/// <summary>
/// 分段结果
/// </summary>
public class ChunkResult
{
    public string RefId { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? Title { get; set; }
    public string Content { get; set; } = string.Empty;
    public float Score { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? HeadingPath { get; set; }
    public string ObsidianLink { get; set; } = string.Empty;
}

/// <summary>
/// 文件摘要
/// </summary>
public class FileSummary
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Title { get; set; }
    public int ChunkCount { get; set; }
}

/// <summary>
/// 搜索统计
/// </summary>
public class SearchStats
{
    public int TotalMatches { get; set; }
    public long DurationMs { get; set; }
    public int EstimatedTokens { get; set; }
}

/// <summary>
/// 搜索过滤条件
/// </summary>
public class SearchFilter
{
    public List<string>? Tags { get; set; }
    public List<string>? Folders { get; set; }
    public DateRange? DateRange { get; set; }
}

/// <summary>
/// 日期范围
/// </summary>
public class DateRange
{
    public DateTime? Start { get; set; }
    public DateTime? End { get; set; }
}

/// <summary>
/// 查询选项
/// </summary>
public class QueryOptions
{
    public bool IncludeRecent { get; set; } = true;
    public bool DebiasPopularity { get; set; } = true;
}

/// <summary>
/// 深入查询请求
/// </summary>
public class DrillDownRequest
{
    public string Query { get; set; } = string.Empty;
    public List<string> RefIds { get; set; } = new();
    public int ExpandContext { get; set; } = 2;
}

/// <summary>
/// 深入查询响应
/// </summary>
public class DrillDownResponse
{
    public List<ChunkResult> ExpandedChunks { get; set; } = new();
    public string FullContext { get; set; } = string.Empty;
}
