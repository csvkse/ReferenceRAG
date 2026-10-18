namespace ReferenceRAG.Core.Features.ModelManagement.StateMachine;

/// <summary>
/// 模型推理访问租约（用于引用计数与安全热切换排空）
/// </summary>
/// <typeparam name="TAdapter">适配器类型</typeparam>
public sealed class ModelLease<TAdapter> : IDisposable where TAdapter : class
{
    private readonly Action _onRelease;
    private int _isDisposed;

    /// <summary>
    /// 获取当前租用的模型适配器实例
    /// </summary>
    public TAdapter Adapter { get; }

    internal ModelLease(TAdapter adapter, Action onRelease)
    {
        Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _onRelease = onRelease ?? throw new ArgumentNullException(nameof(onRelease));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
        {
            _onRelease();
        }
    }
}
