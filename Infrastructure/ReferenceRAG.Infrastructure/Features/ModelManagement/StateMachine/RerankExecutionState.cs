namespace ReferenceRAG.Core.Features.ModelManagement.StateMachine;

/// <summary>
/// 重排执行上下文内层状态机状态
/// </summary>
public enum RerankExecutionState
{
    /// <summary>
    /// 闲置状态（上下文准备就绪，等待新重排请求）
    /// </summary>
    Idle,

    /// <summary>
    /// Query 前缀预热与 KV-Cache 锁定中
    /// </summary>
    PrefixIngested,

    /// <summary>
    /// 候选文档多序列批量解码中
    /// </summary>
    BatchDecoding,

    /// <summary>
    /// Logits 提取与归一化打分中
    /// </summary>
    Scoring,

    /// <summary>
    /// 自适应降级状态（检测到 NaN/算子异常，触发平滑保底策略）
    /// </summary>
    Degraded,

    /// <summary>
    /// 故障终态
    /// </summary>
    Faulted
}
