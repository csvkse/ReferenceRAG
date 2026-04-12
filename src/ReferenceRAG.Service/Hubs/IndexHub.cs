using Microsoft.AspNetCore.SignalR;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Service.Hubs;

/// <summary>
/// 索引状态 Hub - 实时推送索引进度
/// </summary>
public class IndexHub : Hub
{
    private readonly ILogger<IndexHub> _logger;

    public IndexHub(ILogger<IndexHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 加入索引监控组
    /// </summary>
    public async Task JoinIndexGroup()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "index-watchers");
        _logger.LogInformation("Client {ConnectionId} joined index group", Context.ConnectionId);
    }

    /// <summary>
    /// 离开索引监控组
    /// </summary>
    public async Task LeaveIndexGroup()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "index-watchers");
        _logger.LogInformation("Client {ConnectionId} left index group", Context.ConnectionId);
    }

    /// <summary>
    /// 广播索引开始事件
    /// </summary>
    public static async Task BroadcastIndexStarted(IHubContext<IndexHub> hubContext, IndexStartedEvent payload)
    {
        await hubContext.Clients.Group("index-watchers").SendAsync("IndexStarted", payload);
    }

    /// <summary>
    /// 广播索引进度事件
    /// </summary>
    public static async Task BroadcastIndexProgress(IHubContext<IndexHub> hubContext, IndexProgressEvent payload)
    {
        await hubContext.Clients.Group("index-watchers").SendAsync("IndexProgress", payload);
    }

    /// <summary>
    /// 广播索引完成事件
    /// </summary>
    public static async Task BroadcastIndexCompleted(IHubContext<IndexHub> hubContext, IndexCompletedEvent payload)
    {
        await hubContext.Clients.Group("index-watchers").SendAsync("IndexCompleted", payload);
    }

    /// <summary>
    /// 广播文件变更事件
    /// </summary>
    public static async Task BroadcastFileChanged(IHubContext<IndexHub> hubContext, FileChangedEvent payload)
    {
        await hubContext.Clients.Group("index-watchers").SendAsync("FileChanged", payload);
    }
}

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
