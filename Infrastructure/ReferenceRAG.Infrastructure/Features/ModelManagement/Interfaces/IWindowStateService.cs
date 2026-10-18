namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// 窗口与托盘生命周期状态服务
/// </summary>
public interface IWindowStateService
{
    /// <summary>
    /// 当前是否处于最小化到托盘（或后台无界面运行）状态
    /// </summary>
    bool IsMinimizedToTray { get; }

    /// <summary>
    /// 托盘/后台状态变更事件（参数为 true 表示已最小化到托盘/后台，false 表示窗口已在前台打开）
    /// </summary>
    event EventHandler<bool>? MinimizedToTrayChanged;

    /// <summary>
    /// 更新窗口最小化到托盘状态
    /// </summary>
    void SetMinimizedToTray(bool isMinimized);
}
