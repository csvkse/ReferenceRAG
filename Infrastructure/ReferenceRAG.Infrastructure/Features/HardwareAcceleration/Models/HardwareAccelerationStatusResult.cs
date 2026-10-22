using System.Collections.Generic;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Models;

/// <summary>
/// 全平台硬件加速状态模型
/// </summary>
public class HardwareAccelerationStatusResult
{
    /// <summary>
    /// 探测到的所有显卡设备列表
    /// </summary>
    public List<GpuDeviceInfo> DetectedGpus { get; set; } = new();

    /// <summary>
    /// 当前主显卡设备信息
    /// </summary>
    public GpuDeviceInfo? PrimaryGpu { get; set; }

    /// <summary>
    /// 推荐使用的后端标识（Auto, Cuda, DirectML, OpenVino, Rocm, Cpu）
    /// </summary>
    public string RecommendedBackend { get; set; } = "Auto";

    /// <summary>
    /// 当前生效激活的后端标识
    /// </summary>
    public string ActiveBackend { get; set; } = "Cpu";

    /// <summary>
    /// 当前后端就绪或回退状态诊断原因
    /// </summary>
    public string? ActiveBackendReason { get; set; }

    /// <summary>
    /// 是否存在 NVIDIA 显卡
    /// </summary>
    public bool HasNvidiaGpu { get; set; }

    /// <summary>
    /// 是否支持 DirectX 12 (Windows 10/11)
    /// </summary>
    public bool HasDirectX12 { get; set; }

    /// <summary>
    /// 可用硬件加速后端清单
    /// </summary>
    public List<AvailableBackendInfo> AvailableBackends { get; set; } = new();

    /// <summary>
    /// 综合诊断消息
    /// </summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// 硬件后端详情
/// </summary>
public class AvailableBackendInfo
{
    /// <summary>
    /// 后端标识: Auto, Cuda, DirectML, Vulkan, Rocm, OpenVino, Cpu
    /// </summary>
    public string Backend { get; set; } = "";

    /// <summary>
    /// 显示名称
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 当前系统与构建环境是否支持
    /// </summary>
    public bool Supported { get; set; }

    /// <summary>
    /// 状态说明或未就绪原因
    /// </summary>
    public string? Reason { get; set; }
}

/// <summary>
/// CUDA/GPU 可用性状态（向后兼容接口模型）
/// </summary>
public class CudaAvailability
{
    /// <summary>
    /// CUDA 是否可用
    /// </summary>
    public bool IsAvailable { get; set; }

    /// <summary>
    /// 状态消息
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// 检测到的显卡设备名称
    /// </summary>
    public string? GpuName { get; set; }

    /// <summary>
    /// 加速策略名称
    /// </summary>
    public string? Strategy { get; set; }

    /// <summary>
    /// 探测到的 CUDA DLL 所在主路径
    /// </summary>
    public string? DetectedPath { get; set; }

    /// <summary>
    /// 是否存在本地便携式运行库
    /// </summary>
    public bool HasPortableCuda { get; set; }
}
