using Microsoft.ML.OnnxRuntime;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Strategies;

/// <summary>
/// CPU 保底执行策略（优先级 10）
/// 当所有硬件加速均不可用时，确保服务仍能确定性正常运行
/// </summary>
public class CpuStrategy : IExecutionStrategy
{
    public string Name => "CPU";
    public int Priority => 10;

    public bool IsSupported(HardwareContext context) => true;

    public Task<StrategyValidationResult> ValidateAndPrepareAsync(HardwareContext context, int deviceId = 0, string? configuredPath = null)
    {
        return Task.FromResult(StrategyValidationResult.Success(
            Name,
            null,
            null,
            "使用 CPU 运行模式"));
    }

    public void ConfigureOnnxSession(SessionOptions options, int deviceId = 0)
    {
        options.AppendExecutionProvider_CPU();
    }
}
