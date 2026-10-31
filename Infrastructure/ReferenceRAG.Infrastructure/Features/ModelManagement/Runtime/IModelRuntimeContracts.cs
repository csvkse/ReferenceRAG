using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.ModelManagement.Runtime;

/// <summary>
/// 实际执行模型身份记录
/// </summary>
public sealed record ModelExecutionIdentity(
    string ModelId,
    string RemoteModelName,
    string ProviderId,
    string Backend,
    string Device,
    string? Fingerprint = null);

/// <summary>
/// 模型租约对象（调用端 RAII 自动释放借用）
/// </summary>
public interface IModelLease<out TClient> : IAsyncDisposable, IDisposable
{
    TClient Client { get; }
    ModelExecutionIdentity Identity { get; }
}

/// <summary>
/// 资源预留凭证
/// </summary>
public interface IResourceReservation : IAsyncDisposable, IDisposable
{
    ResourceDemand Demand { get; }
    DateTime ReservedAt { get; }
}

/// <summary>
/// 资源调度器契约（负责显存账本、峰值预留、并发限制）
/// </summary>
public interface IResourceScheduler
{
    ValueTask<IResourceReservation> ReserveAsync(
        ResourceDemand demand,
        WorkloadContext workload,
        CancellationToken ct = default);

    long GetAvailableVramBytes(int deviceId = 0);
}

/// <summary>
/// 统一模型路由器契约
/// </summary>
public interface IModelRouter
{
    ValueTask<IModelLease<TClient>> AcquireLeaseAsync<TClient>(
        ModelSelection selection,
        WorkloadContext workload,
        CancellationToken ct = default) where TClient : class;
}
