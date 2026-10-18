using System;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 窗口与托盘生命周期状态实现
/// </summary>
public sealed class WindowStateService : IWindowStateService
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("WindowStateService");
    private volatile bool _isMinimizedToTray;

    public bool IsMinimizedToTray => _isMinimizedToTray;

    public event EventHandler<bool>? MinimizedToTrayChanged;

    public void SetMinimizedToTray(bool isMinimized)
    {
        if (_isMinimizedToTray != isMinimized)
        {
            _isMinimizedToTray = isMinimized;
            _logger.LogInformation("[WindowState] 窗口托盘状态变更: IsMinimizedToTray = {State}", isMinimized);
            try
            {
                MinimizedToTrayChanged?.Invoke(this, isMinimized);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WindowState] 触发 MinimizedToTrayChanged 回调时异常");
            }
        }
    }
}
