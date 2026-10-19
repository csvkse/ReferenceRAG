using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// 支持动态应用最新配置的向量嵌入模型目标
/// </summary>
public interface IModelConfigurableTarget
{
    /// <summary>
    /// 应用最新嵌入模型配置并触发状态机热更新
    /// </summary>
    Task<bool> ApplyConfigurationAsync(EmbeddingConfig config, CancellationToken cancellationToken = default);
}

/// <summary>
/// 支持动态应用最新配置的重排模型目标
/// </summary>
public interface IRerankConfigurableTarget
{
    /// <summary>
    /// 应用最新重排模型配置并触发状态机热更新
    /// </summary>
    Task<bool> ApplyConfigurationAsync(RerankConfig config, CancellationToken cancellationToken = default);
}
