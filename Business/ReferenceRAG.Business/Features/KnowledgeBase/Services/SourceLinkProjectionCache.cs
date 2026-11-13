namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>连接器在源管理读模型中的轻量投影（缓存单元，避免每次 GetAll 做 N+1 catalog 遍历）。</summary>
public sealed record ConnectorProjection(
    string KnowledgeBaseId,
    string KnowledgeBaseName,
    string ConnectorId,
    string TargetPath,
    bool Enabled,
    string? FilePattern,
    DateTime UpdatedAt);

/// <summary>
/// 源管理投影缓存。读模型是「config.Sources + connectors」的聚合，缓存的是后者；
/// 任何关联写操作通过领域事件显式失效，并附带 30s TTL 兜底外部直接改库的情况。
/// </summary>
public interface ISourceLinkProjectionCache
{
    Task<IReadOnlyList<ConnectorProjection>> GetOrLoadAsync(
        Func<CancellationToken, Task<IReadOnlyList<ConnectorProjection>>> loader,
        CancellationToken ct = default);

    void Invalidate(string? path = null);

    long Version { get; }
}

public sealed class SourceLinkProjectionCache : ISourceLinkProjectionCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ConnectorProjection>? _snapshot;
    private DateTime _loadedAtUtc;
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public async Task<IReadOnlyList<ConnectorProjection>> GetOrLoadAsync(
        Func<CancellationToken, Task<IReadOnlyList<ConnectorProjection>>> loader,
        CancellationToken ct = default)
    {
        var hit = TryHit();
        if (hit != null) return hit;

        await _gate.WaitAsync(ct);
        try
        {
            hit = TryHit();
            if (hit != null) return hit;

            var fresh = await loader(ct);
            _snapshot = fresh;
            _loadedAtUtc = DateTime.UtcNow;
            Interlocked.Increment(ref _version);
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<ConnectorProjection>? TryHit()
        => _snapshot != null && DateTime.UtcNow - _loadedAtUtc < Ttl ? _snapshot : null;

    public void Invalidate(string? path = null)
    {
        // 首版采用全量失效：连接器数量通常在百级，全量重建成本低且不会漏掉跨 KB 关联。
        _snapshot = null;
        _loadedAtUtc = DateTime.MinValue;
        Interlocked.Increment(ref _version);
    }
}

/// <summary>
/// SourceLinked / ConnectorDetached / SourceRemoved 的订阅者：失效源管理投影缓存。
/// 对应 remaining-plan §1.1「失效源管理缓存、刷新 KB 连接器数」。
/// </summary>
public sealed class SourceProjectionCacheInvalidationHandler : ISourceLinkEventHandler
{
    private readonly ISourceLinkProjectionCache _cache;
    private readonly ILogger<SourceProjectionCacheInvalidationHandler>? _logger;

    public SourceProjectionCacheInvalidationHandler(
        ISourceLinkProjectionCache cache,
        ILogger<SourceProjectionCacheInvalidationHandler>? logger = null)
    {
        _cache = cache;
        _logger = logger;
    }

    public void Handle(SourceLinkEvent evt)
    {
        _cache.Invalidate(evt.Path);
        _logger?.LogDebug(
            "[SourceLinkEvent] {Kind} -> 源管理投影已失效 (v{Version}, path={Path})",
            evt.Kind, _cache.Version, evt.Path);
    }
}
