using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>源 ↔ 知识库关联领域事件类型（P2，进程内同步派发，不引入事件总线/MediatR）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceLinkEventKind
{
    /// <summary>源路径新挂载到某知识库（EnsureConnectorAsync 返回 created=true）。</summary>
    SourceLinked,

    /// <summary>连接器被解绑 / 删除。</summary>
    ConnectorDetached,

    /// <summary>源被物理删除（非解绑）。</summary>
    SourceRemoved
}

/// <summary>
/// 关联事件载荷。事件在写事务完成后同步发布，处理器异常不会回滚写操作、也不会阻断其他处理器。
/// </summary>
public sealed record SourceLinkEvent
{
    public SourceLinkEventKind Kind { get; init; }
    public string Path { get; init; } = string.Empty;
    public string? SourceName { get; init; }
    public string? KnowledgeBaseId { get; init; }
    public string? KnowledgeBaseName { get; init; }
    public string? ConnectorId { get; init; }
    public string? Detail { get; init; }
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>事件处理器。约定：只做缓存失效、状态标记、日志等轻量动作，不得再发起重的索引写入。</summary>
public interface ISourceLinkEventHandler
{
    void Handle(SourceLinkEvent evt);
}

/// <summary>进程内事件发布器，同时保留最近事件供可观测性接口读取。</summary>
public interface ISourceLinkEventPublisher
{
    void Publish(SourceLinkEvent evt);
    IReadOnlyList<SourceLinkEvent> Recent(int take = 50);
}

public sealed class SourceLinkEventPublisher : ISourceLinkEventPublisher
{
    private const int Capacity = 200;
    private readonly IReadOnlyList<ISourceLinkEventHandler> _handlers;
    private readonly ILogger<SourceLinkEventPublisher>? _logger;
    private readonly ConcurrentQueue<SourceLinkEvent> _recent = new();
    private int _count;

    public SourceLinkEventPublisher(
        IEnumerable<ISourceLinkEventHandler> handlers,
        ILogger<SourceLinkEventPublisher>? logger = null)
    {
        _handlers = handlers?.ToArray() ?? Array.Empty<ISourceLinkEventHandler>();
        _logger = logger;
    }

    public void Publish(SourceLinkEvent evt)
    {
        if (evt is null) return;

        // 先留痕：即使某个处理器抛异常，事件本身仍可观测。
        _recent.Enqueue(evt);
        if (Interlocked.Increment(ref _count) > Capacity)
        {
            while (_count > Capacity && _recent.TryDequeue(out _))
                Interlocked.Decrement(ref _count);
        }

        foreach (var handler in _handlers)
        {
            try
            {
                handler.Handle(evt);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[SourceLinkEvent] 处理器 {Handler} 处理 {Kind} 异常（不影响写操作）",
                    handler.GetType().Name, evt.Kind);
            }
        }
    }

    public IReadOnlyList<SourceLinkEvent> Recent(int take = 50)
    {
        if (take <= 0) take = 50;
        if (take > Capacity) take = Capacity;
        return _recent.ToArray().Reverse().Take(take).ToArray();
    }
}
