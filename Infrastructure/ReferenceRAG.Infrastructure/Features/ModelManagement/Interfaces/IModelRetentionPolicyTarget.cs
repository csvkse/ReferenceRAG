using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// 支持模型驻留与滑动过期策略动态热更新的目标接口
/// </summary>
public interface IModelRetentionPolicyTarget
{
    /// <summary>
    /// 动态热更新模型驻留与滑动过期策略
    /// </summary>
    void UpdateRetentionPolicy(ModelRetentionStrategy strategy, int idleTimeoutSeconds, bool unloadOnlyWhenMinimizedToTray);

    /// <summary>
    /// 从完整配置动态热更新模型驻留策略
    /// </summary>
    void UpdateRetentionPolicy(ObsidianRagConfig config);
}
