using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Probing;

/// <summary>
/// 硬件加速探测中枢服务
/// 架构设计：
/// 1. 进程级隔离沙箱：通过子进程执行硬件加速探测，原生 C++ / GPU 驱动崩溃（SIGSEGV/SIGABRT）被物理隔离在子进程内，Web 主进程永不崩溃。
/// 2. 状态机熔断器（Circuit Breaker）：当子进程因驱动或 ABI 致命错误异常退出后，自动转入 Degraded 状态，进入静默冷却期，杜绝无节制重试。
/// 3. 本地高速缓存：探测成功后缓存状态（默认 5 分钟），避免页面刷新或高频调用反复触发 GPU 探测开销。
/// </summary>
public class HardwareAccelerationProbeService : IHardwareAccelerationProbeService
{
    private readonly ILogger _logger;
    private readonly object _lock = new();

    private enum CircuitState
    {
        Closed,
        Degraded
    }

    private CircuitState _circuitState = CircuitState.Closed;
    private DateTime _lastTripTime = DateTime.MinValue;
    private readonly TimeSpan _circuitCooldown = TimeSpan.FromMinutes(3);

    private HardwareAccelerationStatusResult? _cachedStatus;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);

    public HardwareAccelerationProbeService(ILogger<HardwareAccelerationProbeService>? logger = null)
    {
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <inheritdoc />
    public HardwareAccelerationStatusResult GetStatus(string? configuredPath = null, int deviceId = 0, bool forceRefresh = false)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            // 1. 命中有效缓存且未强制刷新
            if (!forceRefresh && _cachedStatus != null && now < _cacheExpiry)
            {
                return _cachedStatus;
            }

            // 2. 检查熔断器状态：若处于降级状态且在冷却期内，直接返回安全的 CPU 兜底，避免重复引发底层驱动崩溃
            if (!forceRefresh && _circuitState == CircuitState.Degraded)
            {
                if (now - _lastTripTime < _circuitCooldown)
                {
                    _logger.LogDebug("[HardwareProbe] 探测熔断器处于降级保护中，快速返回 CPU 兜底状态");
                    return CreateSafeCpuFallback("硬件探针处于安全熔断保护期 (已平滑降级至 CPU 纯算力)");
                }

                _logger.LogInformation("[HardwareProbe] 熔断器冷却期已过，进入半开状态尝试探测硬件");
            }

            // 3. 执行沙箱探测（优先子进程沙箱）
            var result = ExecuteSandboxedProbe(configuredPath, deviceId);
            _cachedStatus = result;
            _cacheExpiry = now.Add(_cacheTtl);
            return result;
        }
    }

    /// <inheritdoc />
    public CudaAvailability GetCudaAvailability(string? configuredPath = null, int deviceId = 0)
    {
        var status = GetStatus(configuredPath, deviceId);
        var cudaBackend = status.AvailableBackends.Find(b => string.Equals(b.Backend, "Cuda", StringComparison.OrdinalIgnoreCase));
        var isAvailable = cudaBackend?.Supported == true;

        return new CudaAvailability
        {
            IsAvailable = isAvailable,
            Message = cudaBackend?.Reason ?? (isAvailable ? "CUDA 加速已就绪" : "CUDA 硬件加速暂不可用"),
            GpuName = status.PrimaryGpu?.Name,
            Strategy = isAvailable ? "Cuda" : "Cpu",
            DetectedPath = status.PrimaryGpu?.Vendor == GpuVendor.Nvidia ? configuredPath : null,
            HasPortableCuda = false
        };
    }

    private HardwareAccelerationStatusResult ExecuteSandboxedProbe(string? configuredPath, int deviceId)
    {
        try
        {
            var (exePath, argsPrefix) = ResolveProbeCommand();
            if (!string.IsNullOrEmpty(exePath))
            {
                return RunProbeSubprocess(exePath, argsPrefix, configuredPath, deviceId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HardwareProbe] 启动探测子进程失败，回退到进程内安全探针");
        }

        // 无法启动子进程时的进程内受保护调用
        return RunInProcessGuardedProbe(configuredPath, deviceId);
    }

    private HardwareAccelerationStatusResult RunProbeSubprocess(string exePath, string argsPrefix, string? configuredPath, int deviceId)
    {
        var args = $"{argsPrefix} --probe-hardware";
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            args += $" --cuda-path \"{configuredPath}\"";
        }
        if (deviceId != 0)
        {
            args += $" --device-id {deviceId}";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HardwareProbe] 执行子进程 Process.Start 失败: {FileName} {Arguments}", exePath, args);
            return RunInProcessGuardedProbe(configuredPath, deviceId);
        }

        // 等待最多 5 秒，避免探针挂起阻塞 HTTP 线程
        bool completed = process.WaitForExit(5000);
        if (!completed)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch { }

            TripCircuitBreaker("硬件加速探测子进程超时 (5000ms)，已强制终止并降级为 CPU 运算");
            return CreateSafeCpuFallback("硬件探测子进程超时，系统已安全降级为 CPU 运算");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        // 检查退出码：非 0 说明子进程异常终止或因 SIGSEGV/SIGABRT 被操作系统杀死
        if (process.ExitCode != 0)
        {
            var msg = $"硬件探测子进程异常退出 (ExitCode={process.ExitCode}, Stderr={stderr.Trim()})";
            TripCircuitBreaker(msg);
            return CreateSafeCpuFallback($"硬件驱动未就绪或原生库不兼容 (ExitCode={process.ExitCode})，已安全降级为 CPU 运算");
        }

        // 解析标准输出中的定界 JSON
        var startIdx = stdout.IndexOf(HardwareAccelerationProbeWorker.JsonStartDelimiter, StringComparison.Ordinal);
        var endIdx = stdout.IndexOf(HardwareAccelerationProbeWorker.JsonEndDelimiter, StringComparison.Ordinal);

        if (startIdx >= 0 && endIdx > startIdx)
        {
            var jsonStart = startIdx + HardwareAccelerationProbeWorker.JsonStartDelimiter.Length;
            var jsonStr = stdout.Substring(jsonStart, endIdx - jsonStart).Trim();

            try
            {
                var parsed = JsonSerializer.Deserialize<HardwareAccelerationStatusResult>(jsonStr, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (parsed != null)
                {
                    // 成功探测：闭合熔断器
                    _circuitState = CircuitState.Closed;
                    _logger.LogInformation("[HardwareProbe] 子进程探测成功完成，生效后端: {ActiveBackend} ({Reason})",
                        parsed.ActiveBackend, parsed.ActiveBackendReason);
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HardwareProbe] 解析子进程探针 JSON 失败: {Json}", jsonStr);
            }
        }

        TripCircuitBreaker("硬件探测子进程未返回有效输出定界体");
        return CreateSafeCpuFallback("硬件探测输出异常，已安全降级为 CPU 运算");
    }

    private HardwareAccelerationStatusResult RunInProcessGuardedProbe(string? configuredPath, int deviceId)
    {
        try
        {
            return HardwareAccelerationProbeWorker.ExecuteProbe(configuredPath, deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[HardwareProbe] 进程内硬件探测执行异常");
            return CreateSafeCpuFallback($"硬件探测异常: {ex.Message}，已安全降级为 CPU 运算");
        }
    }

    private void TripCircuitBreaker(string reason)
    {
        _circuitState = CircuitState.Degraded;
        _lastTripTime = DateTime.UtcNow;
        _logger.LogWarning("[HardwareProbe] 熔断器已触发降级状态: {Reason}，冷却保护期: {Cooldown}s",
            reason, _circuitCooldown.TotalSeconds);
    }

    private static (string? ExePath, string ArgsPrefix) ResolveProbeCommand()
    {
        var baseDir = AppContext.BaseDirectory;

        // 优先在同目录下寻找 WebHost 或 DesktopHost 的 dll
        var webHostDll = Path.Combine(baseDir, "ReferenceRAG.WebHost.dll");
        var desktopHostDll = Path.Combine(baseDir, "ReferenceRAG.DesktopHost.dll");

        var targetDll = File.Exists(webHostDll) ? webHostDll : (File.Exists(desktopHostDll) ? desktopHostDll : null);

        var currentProcess = Environment.ProcessPath;

        if (targetDll != null)
        {
            // 若当前进程是 dotnet 主机 (例如 /usr/share/dotnet/dotnet 或 dotnet.exe)
            if (!string.IsNullOrEmpty(currentProcess) &&
                (Path.GetFileNameWithoutExtension(currentProcess).Equals("dotnet", StringComparison.OrdinalIgnoreCase)))
            {
                return (currentProcess, $"\"{targetDll}\"");
            }

            // 检查系统 PATH 中的 dotnet
            return ("dotnet", $"\"{targetDll}\"");
        }

        // 单文件发布或自包含原生二进制场景
        if (!string.IsNullOrEmpty(currentProcess) && File.Exists(currentProcess))
        {
            return (currentProcess, "");
        }

        return (null, "");
    }

    private static HardwareAccelerationStatusResult CreateSafeCpuFallback(string reason)
    {
        return new HardwareAccelerationStatusResult
        {
            ActiveBackend = "Cpu",
            ActiveBackendReason = reason,
            RecommendedBackend = "Auto",
            Message = reason,
            AvailableBackends = new List<AvailableBackendInfo>
            {
                new() { Backend = "Auto", Name = "自动感知协商 (智能推荐)", Supported = true, Reason = "自适应探查最佳可用算力，发生故障时自动平滑降级" },
                new() { Backend = "Cpu", Name = "纯 CPU 运算", Supported = true, Reason = "多核 CPU 确定性推理 (无显卡依赖与低能耗)" }
            }
        };
    }
}
