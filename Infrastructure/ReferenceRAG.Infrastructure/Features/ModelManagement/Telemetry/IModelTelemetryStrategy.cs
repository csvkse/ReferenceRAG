using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Telemetry;

/// <summary>
/// 模型遥测提取适配器策略接口 (Strategy Pattern)
/// </summary>
public interface IModelTelemetryStrategy
{
    /// <summary>
    /// 判断当前策略是否匹配指定的后端类型与模型路径
    /// </summary>
    bool CanHandle(string? backendType, string? modelPath);

    /// <summary>
    /// 针对特定引擎适配器提取硬件加速遥测与性能运行配置
    /// </summary>
    (ModelHardwareTelemetry Hardware, ModelConfigTelemetry Config) Extract(
        IEmbeddingBackendAdapter? adapter,
        EmbeddingOptions options,
        HardwareContext hardwareContext,
        ModelInfo? modelMetadata);
}
