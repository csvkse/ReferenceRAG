namespace ReferenceRAG.Core.Services;

/// <summary>
/// 查询统计摘要。
/// </summary>
public class QueryStatsSummary
{
    public long TotalQueries { get; set; }
    public double AvgDurationMs { get; set; }
    public long MaxDurationMs { get; set; }
    public long MinDurationMs { get; set; }
    public double AvgResultCount { get; set; }
}

/// <summary>
/// 单条查询记录。
/// </summary>
public class QueryStatRecord
{
    public long Id { get; set; }
    public string QueryText { get; set; } = "";
    public long DurationMs { get; set; }
    public int ResultCount { get; set; }
    public string Sources { get; set; } = "";
    public string Mode { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
