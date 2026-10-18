using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.StateMachine;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Strategies;

/// <summary>
/// 模型下载与准备策略接口 (Strategy Pattern)
/// </summary>
public interface IModelDownloadStrategy
{
    /// <summary>
    /// 策略唯一名称 (如 "DirectOnnx", "DirectGguf", "CommunityMirror", "PyTorchExport")
    /// </summary>
    string StrategyName { get; }

    /// <summary>
    /// 优先级序号 (数值越小优先级越高)
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// 判断当前模型清单是否适配此策略
    /// </summary>
    bool CanHandle(ModelManifest manifest, DownloadContext context);

    /// <summary>
    /// 执行具体的下载、校验与必要转换流
    /// </summary>
    Task ExecuteAsync(
        ModelManifest manifest,
        DownloadContext context,
        DownloadStateMachine stateMachine,
        CancellationToken cancellationToken = default);
}
