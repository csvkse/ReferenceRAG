using Microsoft.ML.OnnxRuntime;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Strategies;

/// <summary>
/// 硬件执行策略接口
/// </summary>
public interface IExecutionStrategy
{
    /// <summary>
    /// 策略标识名称（NativeCuda, PortableCuda, DirectML, CPU 等）
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 策略仲裁优先级（值越大越优先）
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// 判断当前硬件环境是否支持该策略
    /// </summary>
    bool IsSupported(HardwareContext context);

    /// <summary>
    /// 准备运行环境（扫描、注入 DLL、校验可用性）
    /// </summary>
    Task<StrategyValidationResult> ValidateAndPrepareAsync(HardwareContext context, int deviceId = 0, string? configuredPath = null);

    /// <summary>
    /// 为 ONNX Runtime SessionOptions 应用该策略配置
    /// </summary>
    void ConfigureOnnxSession(SessionOptions options, int deviceId = 0);
}
