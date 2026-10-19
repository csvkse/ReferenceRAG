using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Strategies;

/// <summary>
/// 本地原生 CUDA 12 执行策略（优先级 100）
/// 通过自动探查系统已安装的 CUDA 12 或本地 AI 环境并动态注入，获得极限加速性能
/// </summary>
public class NativeCudaStrategy : IExecutionStrategy
{
    private readonly ICudaEnvironmentScanner _scanner;
    private readonly ILogger<NativeCudaStrategy>? _logger;

    public string Name => "NativeCuda";
    public int Priority => 100;

    public NativeCudaStrategy(ICudaEnvironmentScanner scanner, ILogger<NativeCudaStrategy>? logger = null)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _logger = logger;
    }

    public bool IsSupported(HardwareContext context)
    {
        // 只要检测到 NVIDIA 显卡或已明确找到 CUDA 路径即支持
        return context.HasNvidiaGpu || !string.IsNullOrWhiteSpace(context.DetectedCudaPath);
    }

    public Task<StrategyValidationResult> ValidateAndPrepareAsync(HardwareContext context, int deviceId = 0, string? configuredPath = null)
    {
        try
        {
            // 1. 扫描并注入环境变量
            var scanResult = _scanner.ScanAndInject(configuredPath);
            if (!scanResult.Found)
            {
                return Task.FromResult(StrategyValidationResult.Failure(
                    Name,
                    scanResult.ErrorMessage ?? "系统未找到匹配的 CUDA 12 (cublasLt64_12.dll) 运行库",
                    context.PrimaryGpu?.Name));
            }

            // 2. 校验当前构建是否包含 CUDA 原生模块并能成功挂载
            if (!OnnxExecutionProviderHelper.HasCuda)
            {
                return Task.FromResult(StrategyValidationResult.Failure(
                    Name,
                    "当前构建未包含 CUDA 原生执行模块（可选用 NVIDIA 专属满血版构建）",
                    context.PrimaryGpu?.Name));
            }

            using (var testOptions = new SessionOptions())
            {
                if (!OnnxExecutionProviderHelper.TryAppendCuda(testOptions, deviceId))
                {
                    return Task.FromResult(StrategyValidationResult.Failure(
                        Name,
                        "CUDA 执行提供者挂载失败",
                        context.PrimaryGpu?.Name));
                }
            }

            var gpuName = context.PrimaryGpu?.Name ?? $"NVIDIA GPU {deviceId}";
            _logger?.LogInformation("[NativeCudaStrategy] CUDA 12 硬件加速校验成功: 设备={GpuName}, 库路径={Path}",
                gpuName, scanResult.MainDllDirectory);

            return Task.FromResult(StrategyValidationResult.Success(
                Name,
                gpuName,
                scanResult.InjectedPaths,
                $"已自动激活 NVIDIA CUDA 12 硬件加速 ({gpuName})"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[NativeCudaStrategy] CUDA 运行环境校验失败，将平滑降级");
            return Task.FromResult(StrategyValidationResult.Failure(
                Name,
                $"CUDA 加载失败: {ex.Message}",
                context.PrimaryGpu?.Name));
        }
    }

    public void ConfigureOnnxSession(SessionOptions options, int deviceId = 0)
    {
        // 始终关闭内存模式以支持动态 batch size（消除逐条推理退化）
        options.EnableMemoryPattern = false;
        if (OnnxExecutionProviderHelper.TryAppendCuda(options, deviceId))
        {
            options.AppendExecutionProvider_CPU();
        }
        else
        {
            options.AppendExecutionProvider_CPU();
        }
    }
}
