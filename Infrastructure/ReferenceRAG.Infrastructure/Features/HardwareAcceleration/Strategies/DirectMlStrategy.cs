using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Strategies;

/// <summary>
/// DirectML 全平台通用 GPU 加速策略（优先级 60）
/// 基于 Windows DirectX 12，提供跨 AMD / Intel / NVIDIA / 高通骁龙显卡的硬件加速能力
/// </summary>
public class DirectMlStrategy : IExecutionStrategy
{
    private readonly ILogger<DirectMlStrategy>? _logger;

    public string Name => "DirectML";
    public int Priority => 60;

    public DirectMlStrategy(ILogger<DirectMlStrategy>? logger = null)
    {
        _logger = logger;
    }

    public bool IsSupported(HardwareContext context)
    {
        return context.HasDirectX12 && context.PrimaryGpu != null;
    }

    public Task<StrategyValidationResult> ValidateAndPrepareAsync(HardwareContext context, int deviceId = 0, string? configuredPath = null)
    {
        try
        {
            // 检查当前程序集中是否包含 DirectML 执行提供者扩展方法
            var dmlMethod = typeof(SessionOptions).GetMethod("AppendExecutionProvider_DML", new[] { typeof(int) });
            if (dmlMethod == null)
            {
                return Task.FromResult(StrategyValidationResult.Failure(
                    Name,
                    "当前构建未启用 DirectML 原生模块（已就绪，可在 Phase 3 独立插件包中挂载）",
                    context.PrimaryGpu?.Name));
            }

            using (var testOptions = new SessionOptions())
            {
                dmlMethod.Invoke(testOptions, new object[] { deviceId });
            }

            var gpuName = context.PrimaryGpu?.Name ?? $"GPU {deviceId}";
            _logger?.LogInformation("[DirectMlStrategy] DirectML 硬件加速校验通过: 设备={GpuName}", gpuName);

            return Task.FromResult(StrategyValidationResult.Success(
                Name,
                gpuName,
                null,
                $"已激活 DirectML 跨平台硬件加速 ({gpuName})"));
        }
        catch (Exception ex)
        {
            var innerMsg = ex is TargetInvocationException tie ? tie.InnerException?.Message ?? ex.Message : ex.Message;
            _logger?.LogDebug(ex, "[DirectMlStrategy] DirectML 校验未通过: {Msg}", innerMsg);
            return Task.FromResult(StrategyValidationResult.Failure(
                Name,
                $"DirectML 加速不可用: {innerMsg}",
                context.PrimaryGpu?.Name));
        }
    }

    public void ConfigureOnnxSession(SessionOptions options, int deviceId = 0)
    {
        var dmlMethod = typeof(SessionOptions).GetMethod("AppendExecutionProvider_DML", new[] { typeof(int) });
        if (dmlMethod != null)
        {
            options.EnableMemoryPattern = false;
            dmlMethod.Invoke(options, new object[] { deviceId });
            options.AppendExecutionProvider_CPU();
        }
        else
        {
            options.AppendExecutionProvider_CPU();
        }
    }
}
