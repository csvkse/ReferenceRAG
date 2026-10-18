namespace ReferenceRAG.Core.Features.ModelManagement.StateMachine;

/// <summary>
/// 模型生命周期状态
/// </summary>
public enum ModelState
{
    /// <summary>
    /// 未加载（内存中无权重与会话）
    /// </summary>
    Unloaded,

    /// <summary>
    /// 加载中（正在初始化权重、显存分配与健康探针）
    /// </summary>
    Loading,

    /// <summary>
    /// 就绪（已加载完毕，随时可以处理推理请求）
    /// </summary>
    Ready,

    /// <summary>
    /// 推理中（当前存在活跃的租约借用进行前向计算）
    /// </summary>
    Inferring,

    /// <summary>
    /// 卸载中（正在排空在途请求并释放非托管显存/句柄）
    /// </summary>
    Unloading,

    /// <summary>
    /// 故障/异常（加载失败、显存不足或发生严重运行时错误）
    /// </summary>
    Faulted,

    /// <summary>
    /// 已销毁（生命周期终态）
    /// </summary>
    Disposed
}
