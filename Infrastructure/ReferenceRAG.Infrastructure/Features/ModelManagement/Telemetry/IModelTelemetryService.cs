namespace ReferenceRAG.Core.Features.ModelManagement.Telemetry;

/// <summary>
/// 模型遥测协调服务接口
/// </summary>
public interface IModelTelemetryService
{
    /// <summary>
    /// 获取当前向量嵌入模型的全量运行时遥测快照（状态机、GPU硬件与性能配置）
    /// </summary>
    ModelRuntimeTelemetry GetCurrentEmbeddingTelemetry();

    /// <summary>
    /// 获取当前重排模型的全量运行时遥测快照（状态机、GPU硬件与性能配置）
    /// </summary>
    ModelRuntimeTelemetry GetCurrentRerankTelemetry();
}
