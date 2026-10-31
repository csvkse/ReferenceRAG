using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.ModelManagement.Runtime;

/// <summary>
/// 组合租约（将模型借用与资源预留凭证合二为一）
/// </summary>
public sealed class CompositeModelLease<TClient> : IModelLease<TClient> where TClient : class
{
    private readonly IModelLease<TClient> _innerLease;
    private readonly IResourceReservation? _reservation;
    private int _isDisposed;

    public TClient Client => _innerLease.Client;
    public ModelExecutionIdentity Identity => _innerLease.Identity;

    public CompositeModelLease(IModelLease<TClient> innerLease, IResourceReservation? reservation = null)
    {
        _innerLease = innerLease ?? throw new ArgumentNullException(nameof(innerLease));
        _reservation = reservation;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _innerLease.Dispose();
            _reservation?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            await _innerLease.DisposeAsync();
            if (_reservation != null)
            {
                await _reservation.DisposeAsync();
            }
        }
    }
}

/// <summary>
/// 统一模型路由器实现（协调资源调度器、运行时状态机与适配器实例）
/// </summary>
public sealed class ModelRouter : IModelRouter, IDisposable
{
    private readonly IResourceScheduler _resourceScheduler;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ILogger<ModelRouter>? _logger;
    private readonly ConcurrentDictionary<string, object> _stateMachines = new();
    private readonly ConcurrentDictionary<string, Func<Task<object>>> _factories = new();
    private bool _disposed;

    public ModelRouter(
        IResourceScheduler resourceScheduler,
        ILoggerFactory? loggerFactory = null,
        ILogger<ModelRouter>? logger = null)
    {
        _resourceScheduler = resourceScheduler ?? throw new ArgumentNullException(nameof(resourceScheduler));
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// 注册特定模型的适配器工厂
    /// </summary>
    public void RegisterAdapterFactory<TClient>(string modelId, Func<Task<TClient>> factory) where TClient : class, IDisposable
    {
        _factories[modelId] = async () => (object)await factory();
    }

    public async ValueTask<IModelLease<TClient>> AcquireLeaseAsync<TClient>(
        ModelSelection selection,
        WorkloadContext workload,
        CancellationToken ct = default) where TClient : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var modelId = selection.ModelId;
        var identity = new ModelExecutionIdentity(
            ModelId: modelId,
            RemoteModelName: modelId,
            ProviderId: "local",
            Backend: "runtime",
            Device: "auto"
        );

        // 默认预留 1GB 稳态显存估算
        var demand = new ResourceDemand(
            PeakLoadVramBytes: 1536L * 1024 * 1024,
            SteadyVramBytes: 1024L * 1024 * 1024,
            SystemRamBytes: 512L * 1024 * 1024
        );

        IResourceReservation? reservation = null;
        try
        {
            reservation = await _resourceScheduler.ReserveAsync(demand, workload, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[ModelRouter] 预留显存失败: {ModelId}", modelId);
            throw;
        }

        var smObj = _stateMachines.GetOrAdd(modelId, id =>
        {
            var logger = _loggerFactory?.CreateLogger($"ModelRuntime_{id}");
            return new ModelRuntimeStateMachine<IDisposableClientWrapper<TClient>>(
                id, identity, logger);
        });

        // 内部适配器加载与借用
        if (smObj is ModelRuntimeStateMachine<IDisposableClientWrapper<TClient>> typedSm)
        {
            await typedSm.EnsureLoadedAsync(async () =>
            {
                if (_factories.TryGetValue(modelId, out var factory))
                {
                    var raw = await factory();
                    return new DisposableClientWrapper<TClient>((TClient)raw);
                }
                throw new InvalidOperationException($"未注册模型适配器工厂: {modelId}");
            }, ct);

            var innerLease = typedSm.AcquireLease();
            var unwrappedLease = new UnwrappedModelLease<TClient>(innerLease);
            return new CompositeModelLease<TClient>(unwrappedLease, reservation);
        }

        throw new InvalidOperationException($"模型运行时类型不匹配: {modelId}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var obj in _stateMachines.Values)
        {
            if (obj is IDisposable d)
            {
                try { d.Dispose(); } catch { }
            }
        }
        _stateMachines.Clear();
        _disposed = true;
    }
}

public interface IDisposableClientWrapper<out TClient> : IDisposable where TClient : class
{
    TClient Client { get; }
}

internal sealed class DisposableClientWrapper<TClient> : IDisposableClientWrapper<TClient> where TClient : class
{
    public TClient Client { get; }

    public DisposableClientWrapper(TClient client)
    {
        Client = client;
    }

    public void Dispose()
    {
        if (Client is IDisposable d)
        {
            d.Dispose();
        }
    }
}

internal sealed class UnwrappedModelLease<TClient> : IModelLease<TClient> where TClient : class
{
    private readonly IModelLease<IDisposableClientWrapper<TClient>> _wrapped;

    public TClient Client => _wrapped.Client.Client;
    public ModelExecutionIdentity Identity => _wrapped.Identity;

    public UnwrappedModelLease(IModelLease<IDisposableClientWrapper<TClient>> wrapped)
    {
        _wrapped = wrapped;
    }

    public void Dispose() => _wrapped.Dispose();
    public ValueTask DisposeAsync() => _wrapped.DisposeAsync();
}
