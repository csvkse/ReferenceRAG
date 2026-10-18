using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Core.Features.ModelManagement.StateMachine;

/// <summary>
/// 模型生命周期强类型状态机（管理模型加载、借用、热重载与安全排空）
/// </summary>
/// <typeparam name="TAdapter">适配器类型</typeparam>
public sealed class ModelLifecycleStateMachine<TAdapter> : IAsyncDisposable, IDisposable
    where TAdapter : class, IDisposable
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("ModelLifecycleStateMachine");

    private readonly object _stateSyncLock = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly string _serviceName;
    private readonly IGpuMemoryManager? _memoryManager;

    private TAdapter? _currentAdapter;
    private ModelState _state = ModelState.Unloaded;
    private int _activeLeases;
    private Exception? _lastError;
    private bool _disposed;
    private readonly Timer? _idleTimer;
    private TimeSpan? _idleTimeout;
    private Func<bool>? _canUnloadPredicate;
    private DateTime _lastInferenceTime = DateTime.UtcNow;

    /// <summary>
    /// 当前挂载的适配器实例（未加载时为 null）
    /// </summary>
    public TAdapter? CurrentAdapter => _currentAdapter;

    /// <summary>
    /// 模型空闲无请求滑动过期时间（超时自动调用 UnloadAsync 卸载显存/内存；null 或 <=0 表示常驻不卸载）
    /// </summary>
    public TimeSpan? IdleTimeout
    {
        get
        {
            lock (_stateSyncLock)
            {
                return _idleTimeout;
            }
        }
        set
        {
            lock (_stateSyncLock)
            {
                _idleTimeout = value;
                if (_idleTimeout.HasValue && _idleTimeout.Value > TimeSpan.Zero)
                {
                    RestartIdleTimerLocked();
                }
                else
                {
                    CancelIdleTimerLocked();
                }
            }
        }
    }

    /// <summary>
    /// 当前生命周期状态
    /// </summary>
    public ModelState State
    {
        get
        {
            lock (_stateSyncLock)
            {
                return _state;
            }
        }
        private set
        {
            lock (_stateSyncLock)
            {
                _state = value;
            }
        }
    }

    /// <summary>
    /// 模型是否就绪且可供推理
    /// </summary>
    public bool IsReady => State == ModelState.Ready || State == ModelState.Inferring;

    /// <summary>
    /// 最近一次发生的故障或异常
    /// </summary>
    public Exception? LastError => _lastError;

    /// <summary>
    /// 当前活跃借用数
    /// </summary>
    public int ActiveLeases => Volatile.Read(ref _activeLeases);

    /// <summary>
    /// 构造状态机
    /// </summary>
    public ModelLifecycleStateMachine(
        string serviceName,
        IGpuMemoryManager? memoryManager = null,
        TimeSpan? idleTimeout = null,
        Func<bool>? canUnloadPredicate = null)
    {
        _serviceName = serviceName ?? "ModelService";
        _memoryManager = memoryManager;
        _idleTimeout = idleTimeout;
        _canUnloadPredicate = canUnloadPredicate;
        _idleTimer = new Timer(OnIdleTimerFired, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 动态更新模型驻留与滑动过期策略
    /// </summary>
    public void UpdateRetentionPolicy(TimeSpan? idleTimeout, Func<bool>? canUnloadPredicate = null)
    {
        lock (_stateSyncLock)
        {
            _idleTimeout = idleTimeout;
            if (canUnloadPredicate != null)
                _canUnloadPredicate = canUnloadPredicate;

            if (!_idleTimeout.HasValue || _idleTimeout.Value <= TimeSpan.Zero || (_canUnloadPredicate != null && !_canUnloadPredicate()))
            {
                CancelIdleTimerLocked();
            }
            else if (_state == ModelState.Ready && _activeLeases == 0)
            {
                RestartIdleTimerLocked();
            }
        }
    }

    /// <summary>
    /// 重置滑动过期计时器（例如窗口刚最小化到托盘时，重新开始完整滑动窗口计时）
    /// </summary>
    public void ResetIdleTimer()
    {
        lock (_stateSyncLock)
        {
            _lastInferenceTime = DateTime.UtcNow;
            if (_canUnloadPredicate != null && !_canUnloadPredicate())
            {
                CancelIdleTimerLocked();
            }
            else
            {
                RestartIdleTimerLocked();
            }
        }
    }

    /// <summary>
    /// 取消滑动过期计时器（例如窗口从托盘恢复显示时，保持常驻不卸载）
    /// </summary>
    public void CancelIdleTimer()
    {
        lock (_stateSyncLock)
        {
            CancelIdleTimerLocked();
        }
    }

    /// <summary>
    /// 借用模型进行前向推理（引用计数与租约机制）
    /// </summary>
    public ModelLease<TAdapter> AcquireLease()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateSyncLock)
        {
            CancelIdleTimerLocked();

            if (_state != ModelState.Ready && _state != ModelState.Inferring)
            {
                throw new InvalidOperationException(
                    $"[StateMachine] 服务 {_serviceName} 当前状态为 {_state}，无法执行推理。错误上下文: {_lastError?.Message}");
            }

            Interlocked.Increment(ref _activeLeases);
            _state = ModelState.Inferring;

            return new ModelLease<TAdapter>(_currentAdapter!, OnLeaseReleased);
        }
    }

    /// <summary>
    /// 租约释放回调（归还借用并重置滑动过期基准时间）
    /// </summary>
    private void OnLeaseReleased()
    {
        lock (_stateSyncLock)
        {
            var remaining = Interlocked.Decrement(ref _activeLeases);
            if (remaining <= 0)
            {
                _activeLeases = 0;
                _lastInferenceTime = DateTime.UtcNow;
                if (_state == ModelState.Inferring)
                {
                    _state = ModelState.Ready;
                    RestartIdleTimerLocked();
                }
            }
        }
    }

    /// <summary>
    /// 执行一次带租约保护与 GPU 显存协调的前向推理
    /// </summary>
    public async Task<TResult> ExecuteInferenceAsync<TResult>(
        Func<TAdapter, Task<TResult>> inferenceAction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _memoryManager?.EnterInference(_serviceName);

        using var lease = AcquireLease();
        try
        {
            return await inferenceAction(lease.Adapter);
        }
        finally
        {
            _memoryManager?.ExitInference(_serviceName);
        }
    }

    /// <summary>
    /// 两阶段事务性热重载（Two-Phase Commit with Safe Rollback）
    /// </summary>
    /// <param name="adapterFactory">新模型适配器工厂方法</param>
    /// <param name="cancellationToken">取消标记</param>
    public async Task<bool> TwoPhaseReloadAsync(
        Func<Task<TAdapter>> adapterFactory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleGate.WaitAsync(cancellationToken);

        try
        {
            _logger.LogInformation("[StateMachine] 服务 {Service} 开始两阶段模型重载...", _serviceName);
            TAdapter? oldAdapter;
            lock (_stateSyncLock)
            {
                oldAdapter = _currentAdapter;
                _state = ModelState.Loading;
                _lastError = null;
                CancelIdleTimerLocked();
            }

            TAdapter newAdapter;
            try
            {
                // Phase 1 (Prepare): 影子槽位创建并初始化新模型
                newAdapter = await adapterFactory();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[StateMachine] 服务 {Service} 准备阶段加载新模型失败，执行安全回滚", _serviceName);
                lock (_stateSyncLock)
                {
                    _lastError = ex;
                    if (oldAdapter != null)
                    {
                        _state = ModelState.Ready;
                        _lastInferenceTime = DateTime.UtcNow;
                        RestartIdleTimerLocked();
                        _logger.LogInformation("[StateMachine] 成功回滚到前序模型，业务检索零中断");
                    }
                    else
                    {
                        _state = ModelState.Faulted;
                    }
                }
                return false;
            }

            // Phase 2 (Commit): 排空旧模型在途借用
            lock (_stateSyncLock)
            {
                _state = ModelState.Unloading;
            }
            _logger.LogInformation("[StateMachine] 正在排空服务 {Service} 的在途借用 (活跃数: {Leases})...", _serviceName, ActiveLeases);

            var spinCount = 0;
            while (ActiveLeases > 0 && spinCount < 100)
            {
                await Task.Delay(50, cancellationToken);
                spinCount++;
            }

            // 原子替换
            lock (_stateSyncLock)
            {
                _currentAdapter = newAdapter;
                _state = ModelState.Ready;
                _lastInferenceTime = DateTime.UtcNow;
                RestartIdleTimerLocked();
            }

            // 释放旧适配器
            if (oldAdapter != null)
            {
                try
                {
                    oldAdapter.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StateMachine] 释放旧模型资源时发生告警");
                }
            }

            _logger.LogInformation("[StateMachine] 服务 {Service} 模型热重载提交完成，状态置为 Ready", _serviceName);
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// 安全卸载模型并释放非托管显存/内存
    /// </summary>
    public async Task UnloadAsync()
    {
        lock (_stateSyncLock)
        {
            CancelIdleTimerLocked();
        }

        if (_disposed) return;
        await _lifecycleGate.WaitAsync();

        try
        {
            lock (_stateSyncLock)
            {
                if (_currentAdapter == null && _state == ModelState.Unloaded) return;
                _state = ModelState.Unloading;
            }

            _logger.LogInformation("[StateMachine] 正在安全卸载服务 {Service} 模型...", _serviceName);

            var spinCount = 0;
            while (ActiveLeases > 0 && spinCount < 100)
            {
                await Task.Delay(50);
                spinCount++;
            }

            if (ActiveLeases > 0)
            {
                _logger.LogWarning(
                    "[StateMachine] 服务 {Service} 排空等待超时且仍有在途活跃推理借用 ({Leases})，立即终止本次卸载以避免原生指针冲突崩溃",
                    _serviceName,
                    ActiveLeases);

                lock (_stateSyncLock)
                {
                    _state = ModelState.Ready;
                    _lastInferenceTime = DateTime.UtcNow;
                    RestartIdleTimerLocked();
                }
                return;
            }

            TAdapter? adapterToDispose;
            lock (_stateSyncLock)
            {
                adapterToDispose = _currentAdapter;
                _currentAdapter = null;
                _state = ModelState.Unloaded;
            }

            if (adapterToDispose != null)
            {
                try
                {
                    adapterToDispose.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StateMachine] 卸载模型资源时发生告警");
                }
            }

            _logger.LogInformation("[StateMachine] 服务 {Service} 模型已卸载，状态置为 Unloaded", _serviceName);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void RestartIdleTimerLocked()
    {
        if (_disposed) return;
        if (_canUnloadPredicate != null && !_canUnloadPredicate())
        {
            CancelIdleTimerLocked();
            return;
        }
        if (_idleTimeout.HasValue && _idleTimeout.Value > TimeSpan.Zero && _currentAdapter != null && _state == ModelState.Ready && _activeLeases == 0)
        {
            try
            {
                _idleTimer?.Change(_idleTimeout.Value, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) { }
        }
    }

    private void CancelIdleTimerLocked()
    {
        try
        {
            _idleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException) { }
    }

    private void OnIdleTimerFired(object? state)
    {
        if (_disposed) return;

        _ = Task.Run(async () =>
        {
            if (_disposed) return;

            bool shouldUnload = false;
            lock (_stateSyncLock)
            {
                if (_disposed) return;
                var timeout = _idleTimeout;
                if (!timeout.HasValue || timeout.Value <= TimeSpan.Zero) return;

                // 0. 策略判定：如果当前环境不允许卸载（例如仅托盘生效但窗口当前在前台打开），保持常驻
                if (_canUnloadPredicate != null && !_canUnloadPredicate())
                {
                    _logger.LogDebug("[StateMachine] 服务 {Service} 空闲到期，但策略判定当前禁止卸载，保持常驻", _serviceName);
                    CancelIdleTimerLocked();
                    return;
                }

                // 1. 看门狗补偿：如果在 30 秒到达时恰好有在途借用或正在推理，绝不丢弃定时器！延后复检
                if (_activeLeases > 0 || _state == ModelState.Inferring)
                {
                    var retryDelay = timeout.Value < TimeSpan.FromSeconds(10) ? timeout.Value : TimeSpan.FromSeconds(10);
                    _logger.LogDebug("[StateMachine] 服务 {Service} 到期但正在推理中 (Leases={Leases})，看门狗将在 {Seconds:F1} 秒后补偿检查", _serviceName, _activeLeases, retryDelay.TotalSeconds);
                    try { _idleTimer?.Change(retryDelay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
                    return;
                }

                // 2. 检查从上次推理借用结束至今是否真实达到完整超时阈值
                var idleDuration = DateTime.UtcNow - _lastInferenceTime;
                if (_state == ModelState.Ready && idleDuration >= timeout.Value)
                {
                    shouldUnload = true;
                }
                else if (_state == ModelState.Ready)
                {
                    // 尚未达到完整空闲时长（中途有短时借用已归还），补偿剩余时间
                    var remaining = timeout.Value - idleDuration;
                    var delay = remaining > TimeSpan.Zero ? remaining : (timeout.Value < TimeSpan.FromSeconds(5) ? timeout.Value : TimeSpan.FromSeconds(5));
                    try { _idleTimer?.Change(delay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
                    return;
                }
            }

            if (shouldUnload)
            {
                _logger.LogInformation(
                    "[StateMachine] 服务 {Service} 空闲超过 {Seconds:F0} 秒无请求，触发滑动过期自动卸载显存/内存...",
                    _serviceName,
                    _idleTimeout?.TotalSeconds ?? 0);

                try
                {
                    await UnloadAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[StateMachine] 服务 {Service} 滑动过期自动卸载异常", _serviceName);
                    lock (_stateSyncLock)
                    {
                        if (!_disposed && _state == ModelState.Ready)
                        {
                            try { _idleTimer?.Change(TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
                        }
                    }
                }
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_stateSyncLock)
        {
            CancelIdleTimerLocked();
            try { _idleTimer?.Dispose(); } catch { }
        }

        UnloadAsync().GetAwaiter().GetResult();
        _lifecycleGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_stateSyncLock)
        {
            CancelIdleTimerLocked();
            try { _idleTimer?.Dispose(); } catch { }
        }

        await UnloadAsync();
        _lifecycleGate.Dispose();
    }
}
