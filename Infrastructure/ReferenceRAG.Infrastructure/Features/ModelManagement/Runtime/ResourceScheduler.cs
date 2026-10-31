using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.ModelManagement.Runtime;

/// <summary>
/// 资源预留凭证实现
/// </summary>
public sealed class ResourceReservation : IResourceReservation
{
    private readonly ResourceScheduler _scheduler;
    private int _isDisposed;

    public ResourceDemand Demand { get; }
    public DateTime ReservedAt { get; }

    internal ResourceReservation(ResourceScheduler scheduler, ResourceDemand demand)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        Demand = demand ?? throw new ArgumentNullException(nameof(demand));
        ReservedAt = DateTime.UtcNow;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _scheduler.ReleaseReservation(Demand);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 资源调度器实现（管理设备显存/内存账本，峰值与稳态预算分离）
/// </summary>
public sealed class ResourceScheduler : IResourceScheduler
{
    private readonly long _totalVramBytes;
    private readonly long _safetyMarginBytes;
    private readonly ILogger<ResourceScheduler>? _logger;
    private readonly object _lock = new();

    private long _allocatedVramBytes;

    public long TotalVramBytes => _totalVramBytes;
    public long AllocatedVramBytes { get { lock (_lock) return _allocatedVramBytes; } }

    public ResourceScheduler(
        long totalVramBytes = 8L * 1024 * 1024 * 1024, // 默认 8GB
        long safetyMarginBytes = 512L * 1024 * 1024,    // 默认 512MB 安全余量
        ILogger<ResourceScheduler>? logger = null)
    {
        _totalVramBytes = totalVramBytes;
        _safetyMarginBytes = safetyMarginBytes;
        _logger = logger;
    }

    public ValueTask<IResourceReservation> ReserveAsync(
        ResourceDemand demand,
        WorkloadContext workload,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(demand);

        lock (_lock)
        {
            var usableVram = _totalVramBytes - _safetyMarginBytes;
            var requiredBytes = Math.Max(demand.PeakLoadVramBytes, demand.SteadyVramBytes);

            if (_allocatedVramBytes + requiredBytes > usableVram)
            {
                var available = Math.Max(0, usableVram - _allocatedVramBytes);
                _logger?.LogWarning("[ResourceScheduler] 资源不足: 需求 {Req} MB, 可用 {Avail} MB (总显存 {Total} MB)",
                    requiredBytes / 1024 / 1024, available / 1024 / 1024, _totalVramBytes / 1024 / 1024);

                throw new InvalidOperationException(
                    $"设备显存预算不足: 请求 {requiredBytes / 1024 / 1024} MB, 当前可用 {available / 1024 / 1024} MB");
            }

            _allocatedVramBytes += requiredBytes;
            _logger?.LogInformation("[ResourceScheduler] 成功预留显存 {Req} MB, 当前已分配: {Alloc} MB / {Total} MB",
                requiredBytes / 1024 / 1024, _allocatedVramBytes / 1024 / 1024, _totalVramBytes / 1024 / 1024);

            return ValueTask.FromResult<IResourceReservation>(new ResourceReservation(this, demand));
        }
    }

    internal void ReleaseReservation(ResourceDemand demand)
    {
        lock (_lock)
        {
            var requiredBytes = Math.Max(demand.PeakLoadVramBytes, demand.SteadyVramBytes);
            _allocatedVramBytes = Math.Max(0, _allocatedVramBytes - requiredBytes);
            _logger?.LogInformation("[ResourceScheduler] 释放显存预留 {Rel} MB, 剩余分配: {Alloc} MB",
                requiredBytes / 1024 / 1024, _allocatedVramBytes / 1024 / 1024);
        }
    }

    public long GetAvailableVramBytes(int deviceId = 0)
    {
        lock (_lock)
        {
            var usableVram = _totalVramBytes - _safetyMarginBytes;
            return Math.Max(0, usableVram - _allocatedVramBytes);
        }
    }
}
