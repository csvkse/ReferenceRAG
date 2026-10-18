using ReferenceRAG.Core.Features.HardwareAcceleration.Models;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Detection;

/// <summary>
/// 硬件设备检测接口
/// </summary>
public interface IHardwareDetector
{
    /// <summary>
    /// 获取当前系统的硬件加速上下文（带缓存）
    /// </summary>
    /// <param name="forceRefresh">是否强制刷新检测</param>
    HardwareContext GetContext(bool forceRefresh = false);
}
