using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// GPU 显存管理器
/// 特性：
/// 1. 计数器机制：推理中不释放，推理结束后检查延迟释放
/// 2. 后台监控：定时检查显存，智能触发释放
/// 3. 冲突避免：释放操作只在推理间隙执行
/// </summary>
public class GpuMemoryManager : IGpuMemoryManager, IHostedService, IDisposable
{
    private readonly ILogger<GpuMemoryManager>? _logger;
    private readonly ConcurrentDictionary<string, SessionRef> _sessions = new();
    private readonly ConcurrentDictionary<int, GpuMemoryInfo> _memoryCache = new();

    // 配置
    private readonly TimeSpan _monitorInterval = TimeSpan.FromMinutes(5);
    private readonly long _shrinkThresholdMB = 500;        // 空闲显存 < 500MB 触发
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(10);  // 空闲超时
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromSeconds(30); // 显存缓存过期

    private Timer? _monitorTimer;
    private bool _disposed;

    public GpuMemoryManager(ILogger<GpuMemoryManager>? logger = null)
    {
        _logger = logger;
    }

    #region Session 注册管理

    public void Register(string name, Func<Microsoft.ML.OnnxRuntime.InferenceSession?> getSession, int deviceId = 0)
    {
        var sessionRef = new SessionRef(name, getSession, deviceId, _logger);
        _sessions[name] = sessionRef;
        _logger?.LogInformation("[GpuMemoryManager] 注册 Session: {Name}, DeviceId={DeviceId}", name, deviceId);
    }

    public void Unregister(string name)
    {
        if (_sessions.TryRemove(name, out var session))
        {
            session.Dispose();
            _logger?.LogInformation("[GpuMemoryManager] 注销 Session: {Name}", name);
        }
    }

    #endregion

    #region 推理计数（核心冲突避免机制）

    public void EnterInference(string name)
    {
        if (_sessions.TryGetValue(name, out var session))
        {
            session.EnterInference();
        }
    }

    public void ExitInference(string name)
    {
        if (_sessions.TryGetValue(name, out var session))
        {
            session.ExitInference();
        }
    }

    #endregion

    #region 显存查询

    public GpuMemoryInfo GetMemoryInfo(int deviceId = 0)
    {
        // 检查缓存
        if (_memoryCache.TryGetValue(deviceId, out var cached))
        {
            if (DateTime.UtcNow - cached.Timestamp < _cacheExpiry)
                return cached;
        }

        // 查询 nvidia-smi
        var info = QueryNvidiaSmi(deviceId);
        _memoryCache[deviceId] = info;
        return info;
    }

    private GpuMemoryInfo QueryNvidiaSmi(int deviceId)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = $"--query-gpu=memory.free,memory.used,memory.total,utilization.gpu,temperature.gpu --format=csv,noheader,nounits -i {deviceId}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return GpuMemoryInfo.Unknown(deviceId);

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var parts = output.Trim().Split(", ");
            if (parts.Length >= 5)
            {
                return new GpuMemoryInfo
                {
                    DeviceId = deviceId,
                    FreeMB = long.Parse(parts[0]),
                    UsedMB = long.Parse(parts[1]),
                    TotalMB = long.Parse(parts[2]),
                    GpuUtilization = int.Parse(parts[3]),
                    Temperature = int.Parse(parts[4]),
                    Timestamp = DateTime.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[GpuMemoryManager] nvidia-smi 查询失败");
        }

        return GpuMemoryInfo.Unknown(deviceId);
    }

    #endregion

    #region 释放逻辑

    public async Task ShrinkAsync(string? name = null)
    {
        if (name != null)
        {
            // 释放指定 Session
            if (_sessions.TryGetValue(name, out var session))
            {
                session.RequestShrink();
            }
        }
        else
        {
            // 释放所有
            foreach (var session in _sessions.Values)
            {
                session.RequestShrink();
            }
        }

        await Task.CompletedTask;
    }

    #endregion

    #region 后台监控服务

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("[GpuMemoryManager] 启动后台监控，间隔: {Interval}", _monitorInterval);
        _monitorTimer = new Timer(MonitorCallback, null, _monitorInterval, _monitorInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _monitorTimer?.Dispose();
        _logger?.LogInformation("[GpuMemoryManager] 停止后台监控");
        return Task.CompletedTask;
    }

    private void MonitorCallback(object? state)
    {
        try
        {
            _ = AutoShrinkAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[GpuMemoryManager] 自动监控异常");
        }
    }

    private async Task AutoShrinkAsync()
    {
        foreach (var (name, session) in _sessions)
        {
            var memInfo = GetMemoryInfo(session.DeviceId);

            // 策略 1: 空闲显存低于阈值
            if (memInfo.FreeMB < _shrinkThresholdMB && memInfo.FreeMB > 0)
            {
                _logger?.LogWarning(
                    "[GpuMemoryManager] 显存不足，触发释放: {Name}, Free={FreeMB}MB < Threshold={ThresholdMB}MB",
                    name, memInfo.FreeMB, _shrinkThresholdMB);
                session.RequestShrink();
                continue;
            }
        }

        await Task.CompletedTask;
    }

    #endregion

    #region 状态查询

    public IReadOnlyDictionary<string, SessionState> GetSessionStates()
    {
        return _sessions.ToDictionary(
            kvp => kvp.Key,
            kvp => new SessionState
            {
                Name = kvp.Value.Name,
                DeviceId = kvp.Value.DeviceId,
                IsActive = kvp.Value.ActiveInferenceCount > 0,
                ActiveInferenceCount = kvp.Value.ActiveInferenceCount,
                LastActivityTime = kvp.Value.LastActivityTime,
                HasPendingShrink = kvp.Value.HasPendingShrink
            });
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitorTimer?.Dispose();

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }

    #region 内部类: SessionRef

    /// <summary>
    /// Session 引用，实现计数器延迟释放机制
    /// </summary>
    private class SessionRef : IDisposable
    {
        private readonly Func<Microsoft.ML.OnnxRuntime.InferenceSession?> _getSession;
        private readonly ILogger? _logger;
        private readonly object _lock = new();
        private int _activeCount;
        private bool _pendingShrink;
        private DateTime _lastActivityTime = DateTime.UtcNow;
        private bool _disposed;

        public string Name { get; }
        public int DeviceId { get; }
        public Microsoft.ML.OnnxRuntime.InferenceSession? Session => _getSession();

        public int ActiveInferenceCount => _activeCount;
        public DateTime LastActivityTime => _lastActivityTime;
        public TimeSpan IdleTime => DateTime.UtcNow - _lastActivityTime;
        public bool HasPendingShrink => _pendingShrink;

        public SessionRef(string name, Func<Microsoft.ML.OnnxRuntime.InferenceSession?> getSession, int deviceId, ILogger? logger)
        {
            Name = name;
            _getSession = getSession;
            DeviceId = deviceId;
            _logger = logger;
        }

        /// <summary>
        /// 进入推理（增加计数）
        /// </summary>
        public void EnterInference()
        {
            lock (_lock)
            {
                _activeCount++;
                _lastActivityTime = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// 退出推理（减少计数，检查延迟释放）
        /// </summary>
        public void ExitInference()
        {
            lock (_lock)
            {
                _activeCount--;
                _lastActivityTime = DateTime.UtcNow;

                // 所有推理完成，检查是否有待执行的释放
                if (_activeCount == 0 && _pendingShrink)
                {
                    _pendingShrink = false;
                    DoShrink();
                }
            }
        }

        /// <summary>
        /// 请求释放（无推理时立即执行，否则延迟）
        /// </summary>
        public void RequestShrink()
        {
            lock (_lock)
            {
                if (_activeCount == 0)
                {
                    DoShrink();
                }
                else
                {
                    _pendingShrink = true;
                    _logger?.LogInformation("[GpuMemoryManager] {Name}: 推理进行中({Count}个)，延迟释放", Name, _activeCount);
                }
            }
        }

        private void DoShrink()
        {
            _logger?.LogInformation("[GpuMemoryManager] {Name}: 执行显存释放", Name);

            // GC 释放（推荐，安全）
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    #endregion
}
