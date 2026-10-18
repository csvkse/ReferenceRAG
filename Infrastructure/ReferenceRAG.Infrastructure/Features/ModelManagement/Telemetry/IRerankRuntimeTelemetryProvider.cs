namespace ReferenceRAG.Core.Features.ModelManagement.Telemetry;

/// <summary>
/// 重排模型运行时遥测与状态提供者接口
/// </summary>
public interface IRerankRuntimeTelemetryProvider
{
    /// <summary>
    /// 获取当前重排模型的生命周期状态机快照
    /// </summary>
    ModelLifecycleSnapshot GetLifecycleSnapshot();

    /// <summary>
    /// 当前模型名称
    /// </summary>
    string ModelName { get; }

    /// <summary>
    /// 当前物理模型路径
    /// </summary>
    string ModelPath { get; }

    /// <summary>
    /// 是否启用 CUDA 加速
    /// </summary>
    bool UseCuda { get; }

    /// <summary>
    /// GPU 设备 ID
    /// </summary>
    int DeviceId { get; }
}
