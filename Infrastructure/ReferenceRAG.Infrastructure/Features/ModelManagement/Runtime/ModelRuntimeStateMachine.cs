using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.ModelManagement.Runtime;

/// <summary>
/// 模型租约实现（带原子引用计数与安全释放）
/// </summary>
public sealed class ModelRuntimeLease<TAdapter> : IModelLease<TAdapter> where TAdapter : class
{
    private readonly Action _onRelease;
    private int _isDisposed;

    public TAdapter Client { get; }
    public ModelExecutionIdentity Identity { get; }

    internal ModelRuntimeLease(TAdapter client, ModelExecutionIdentity identity, Action onRelease)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _onRelease = onRelease ?? throw new ArgumentNullException(nameof(onRelease));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _onRelease();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 健壮的模型运行时生命周期状态机（支持安全排空、单飞加载与零原生指针挂起）
/// </summary>
public sealed class ModelRuntimeStateMachine<TAdapter> : IAsyncDisposable, IDisposable
    where TAdapter : class, IDisposable
{
    private readonly string _name;
    private readonly ModelExecutionIdentity _identity;
    private readonly ILogger? _logger;
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    private TAdapter? _adapter;
    private ModelRuntimeState _state = ModelRuntimeState.Unloaded;
    private int _activeLeases;
    private Exception? _lastError;
    private bool _disposed;

    public string Name => _name;
    public ModelExecutionIdentity Identity => _identity;
    public ModelRuntimeState State { get { lock (_stateLock) return _state; } }
    public int ActiveLeases => Volatile.Read(ref _activeLeases);
    public Exception? LastError => _lastError;

    public ModelRuntimeStateMachine(
        string name,
        ModelExecutionIdentity identity,
        ILogger? logger = null)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _logger = logger;
    }

    /// <summary>
    /// 加载模型实例（Single-flight 互斥保证）
    /// </summary>
    public async Task<TAdapter> EnsureLoadedAsync(
        Func<Task<TAdapter>> adapterFactory,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_adapter != null && _state is ModelRuntimeState.Ready or ModelRuntimeState.Inferring)
            {
                return _adapter;
            }
        }

        await _lifecycleGate.WaitAsync(ct);
        try
        {
            lock (_stateLock)
            {
                if (_adapter != null && _state is ModelRuntimeState.Ready or ModelRuntimeState.Inferring)
                {
                    return _adapter;
                }
                _state = ModelRuntimeState.Loading;
                _lastError = null;
            }

            _logger?.LogInformation("[ModelRuntimeStateMachine] 开始加载模型运行时: {Name}", _name);
            TAdapter newAdapter;
            try
            {
                newAdapter = await adapterFactory();
            }
            catch (Exception ex)
            {
                lock (_stateLock)
                {
                    _state = ModelRuntimeState.Faulted;
                    _lastError = ex;
                }
                _logger?.LogError(ex, "[ModelRuntimeStateMachine] 模型运行时加载失败: {Name}", _name);
                throw;
            }

            lock (_stateLock)
            {
                _adapter = newAdapter;
                _state = ModelRuntimeState.Ready;
            }
            _logger?.LogInformation("[ModelRuntimeStateMachine] 模型运行时就绪: {Name}", _name);
            return newAdapter;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 借用模型租约
    /// </summary>
    public IModelLease<TAdapter> AcquireLease()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_state != ModelRuntimeState.Ready && _state != ModelRuntimeState.Inferring)
            {
                throw new InvalidOperationException(
                    $"[ModelRuntimeStateMachine] 实例 {_name} 当前状态为 {_state}，无法分配租约。上次错误: {_lastError?.Message}");
            }

            Interlocked.Increment(ref _activeLeases);
            _state = ModelRuntimeState.Inferring;

            return new ModelRuntimeLease<TAdapter>(_adapter!, _identity, OnLeaseReleased);
        }
    }

    private void OnLeaseReleased()
    {
        lock (_stateLock)
        {
            var remaining = Interlocked.Decrement(ref _activeLeases);
            if (remaining <= 0)
            {
                _activeLeases = 0;
                if (_state == ModelRuntimeState.Inferring)
                {
                    _state = ModelRuntimeState.Ready;
                }
            }
        }
    }

    /// <summary>
    /// 安全排空与卸载（超时严格终止卸载，绝不销毁仍在使用的非托管指针）
    /// </summary>
    public async Task<bool> UnloadAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_disposed) return true;

        await _lifecycleGate.WaitAsync(ct);
        try
        {
            lock (_stateLock)
            {
                if (_adapter == null || _state == ModelRuntimeState.Unloaded) return true;
                _state = ModelRuntimeState.Draining;
            }

            _logger?.LogInformation("[ModelRuntimeStateMachine] 模型 {Name} 进入 Draining，等待 {Leases} 个活跃借用排空...",
                _name, ActiveLeases);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (ActiveLeases > 0 && sw.Elapsed < timeout)
            {
                await Task.Delay(50, ct);
            }

            if (ActiveLeases > 0)
            {
                _logger?.LogWarning(
                    "[ModelRuntimeStateMachine] 模型 {Name} 排空超时，仍有 {Leases} 借用在途，中止卸载以保护内存安全",
                    _name, ActiveLeases);

                lock (_stateLock)
                {
                    _state = ModelRuntimeState.Ready;
                }
                return false;
            }

            TAdapter? toDispose;
            lock (_stateLock)
            {
                toDispose = _adapter;
                _adapter = null;
                _state = ModelRuntimeState.Unloaded;
            }

            if (toDispose != null)
            {
                try
                {
                    toDispose.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "[ModelRuntimeStateMachine] 释放模型非托管资源时发生异常");
                }
            }

            _logger?.LogInformation("[ModelRuntimeStateMachine] 模型 {Name} 安全卸载成功", _name);
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _lifecycleGate.Dispose();
        _adapter?.Dispose();
        _adapter = null;
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
