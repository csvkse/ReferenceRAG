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
