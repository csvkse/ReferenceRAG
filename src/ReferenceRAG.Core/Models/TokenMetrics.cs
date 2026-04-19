namespace ReferenceRAG.Core.Models;

/// <summary>
/// Token 使用记录 - 单次操作的 Token 使用情况
/// </summary>
public class TokenMetrics
{
    /// <summary>
    /// 记录时间戳
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// 操作类型：Query（查询）、Index（索引）
    /// </summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Token 数量
    /// </summary>
    public int TokenCount { get; set; }

    /// <summary>
    /// 模型名称（可选）
    /// </summary>
    public string? ModelName { get; set; }
}

/// <summary>
/// Token 统计汇总 - 累计 Token 使用统计
/// </summary>
public class TokenStats
{
    /// <summary>
    /// 查询操作累计 Token 数
    /// </summary>
    public long TotalQueryTokens { get; set; }

    /// <summary>
    /// 索引操作累计 Token 数
    /// </summary>
    public long TotalIndexTokens { get; set; }

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// 总 Token 数
    /// </summary>
    public long TotalTokens => TotalQueryTokens + TotalIndexTokens;
}
