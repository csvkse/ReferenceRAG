using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Telemetry;

/// <summary>
/// 模型运行时遥测与状态机提供者接口
/// </summary>
public interface IModelRuntimeTelemetryProvider
{
    /// <summary>
    /// 获取当前模型的生命周期状态机快照
    /// </summary>
    ModelLifecycleSnapshot GetLifecycleSnapshot();

    /// <summary>
    /// 获取当前底层活跃的适配器实例（若尚未加载则可能为 null）
    /// </summary>
    IEmbeddingBackendAdapter? CurrentAdapter { get; }

    /// <summary>
    /// 获取当前生效的配置选项
    /// </summary>
    EmbeddingOptions CurrentOptions { get; }
}
