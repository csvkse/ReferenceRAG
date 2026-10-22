using ReferenceRAG.Core.Features.HardwareAcceleration.Models;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Probing;

/// <summary>
/// 硬件加速探测服务接口
/// 采用子进程沙箱隔离策略，杜绝原生 C++ 驱动/动态库崩溃 (SIGSEGV/SIGABRT) 影响 Web/桌面主服务
/// </summary>
public interface IHardwareAccelerationProbeService
{
    /// <summary>
    /// 获取当前系统的硬件加速支持就绪状态（支持沙箱隔离与熔断降级）
    /// </summary>
    /// <param name="configuredPath">自定义 CUDA 库搜索路径</param>
    /// <param name="deviceId">目标 GPU 设备编号</param>
    /// <param name="forceRefresh">是否绕过本地缓存强制重新探测</param>
    HardwareAccelerationStatusResult GetStatus(string? configuredPath = null, int deviceId = 0, bool forceRefresh = false);

    /// <summary>
    /// 获取 CUDA 可用性（向后兼容视图）
    /// </summary>
    CudaAvailability GetCudaAvailability(string? configuredPath = null, int deviceId = 0);
}
