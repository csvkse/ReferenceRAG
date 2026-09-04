namespace ReferenceRAG.Service.Hubs;

/// <summary>
/// 索引开始事件
/// </summary>
public class IndexStartedEvent
{
    public string IndexId { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public DateTime StartTime { get; set; }
}

/// <summary>
/// 索引进度事件
/// </summary>
public class IndexProgressEvent
{
    public string IndexId { get; set; } = string.Empty;
    public int ProcessedFiles { get; set; }
    public int TotalFiles { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public double ProgressPercent => TotalFiles > 0 ? (double)ProcessedFiles / TotalFiles * 100 : 0;
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// 索引完成事件
/// </summary>
public class IndexCompletedEvent
{
    public string IndexId { get; set; } = string.Empty;
    public int TotalFiles { get; set; }
    public int TotalChunks { get; set; }
    public int TotalVectors { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTime CompletedAt { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// 文件变更事件
/// </summary>
public class FileChangedEvent
{
    public string FilePath { get; set; } = string.Empty;
    public string ChangeType { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
