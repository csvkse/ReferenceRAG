using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Strategies;

/// <summary>
/// 便携式 / 按需下载 CUDA 运行时执行策略（优先级 80）
/// 当系统未安装 CUDA Toolkit 时，使用解压在 ./runtimes/cuda 目录中的绿色微型运行时
/// </summary>
public class PortableCudaStrategy : IExecutionStrategy
{
    private readonly IEnvironmentInjector _injector;
    private readonly ILogger<PortableCudaStrategy>? _logger;

    public string Name => "PortableCuda";
    public int Priority => 80;

    public PortableCudaStrategy(IEnvironmentInjector injector, ILogger<PortableCudaStrategy>? logger = null)
    {
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        _logger = logger;
    }

    public bool IsSupported(HardwareContext context)
    {
        return context.HasNvidiaGpu;
    }

    public Task<StrategyValidationResult> ValidateAndPrepareAsync(HardwareContext context, int deviceId = 0, string? configuredPath = null)
    {
        var portableDir = HardwareDetector.GetPortableCudaDirectory();
        if (!Directory.Exists(portableDir) ||
            !File.Exists(Path.Combine(portableDir, "cublasLt64_12.dll")) ||
            !File.Exists(Path.Combine(portableDir, "cublas64_12.dll")))
        {
            return Task.FromResult(StrategyValidationResult.Failure(
                Name,
                $"便携式 CUDA 运行库未就绪 (目标路径: {portableDir})",
                context.PrimaryGpu?.Name));
        }

        try
        {
            var injected = _injector.InjectDirectories(new[] { portableDir });

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
                        "便携式 CUDA 执行提供者挂载失败",
                        context.PrimaryGpu?.Name));
                }
            }

            var gpuName = context.PrimaryGpu?.Name ?? $"NVIDIA GPU {deviceId}";
            _logger?.LogInformation("[PortableCudaStrategy] 便携式 CUDA 运行库加载成功: 设备={GpuName}", gpuName);

            return Task.FromResult(StrategyValidationResult.Success(
                Name,
                gpuName,
                injected,
                $"已激活便携式 CUDA 12 加速 ({gpuName})"));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[PortableCudaStrategy] 便携式 CUDA 加载失败");
            return Task.FromResult(StrategyValidationResult.Failure(
                Name,
                $"便携式 CUDA 加载异常: {ex.Message}",
                context.PrimaryGpu?.Name));
        }
    }

    public void ConfigureOnnxSession(SessionOptions options, int deviceId = 0)
    {
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
