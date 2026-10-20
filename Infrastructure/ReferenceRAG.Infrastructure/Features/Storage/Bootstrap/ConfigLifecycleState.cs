namespace ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;

/// <summary>
/// 配置文件生命周期状态
/// </summary>
public enum ConfigLifecycleState
{
    /// <summary>
    /// 未初始化
    /// </summary>
    Uninitialized,

    /// <summary>
    /// 正在检查文件存在性与状态
    /// </summary>
    Checking,

    /// <summary>
    /// 配置文件不存在
    /// </summary>
    Missing,

    /// <summary>
    /// 正在从种子策略创建初始配置
    /// </summary>
    Seeding,

    /// <summary>
    /// 正在校验已有配置文件的合法性
    /// </summary>
    Validating,

    /// <summary>
    /// 配置文件损坏（0 字节或非有效 JSON）
    /// </summary>
    Corrupted,

    /// <summary>
    /// 正在自愈（自动备份残损文件并清理）
    /// </summary>
    Healing,

    /// <summary>
    /// 正在执行版本迁移升级
    /// </summary>
    Migrating,

    /// <summary>
    /// 配置文件已就绪且可用
    /// </summary>
    Ready,

    /// <summary>
    /// 引导失败（如磁盘不可写、无权限等致命异常）
    /// </summary>
    Failed
}
