using System.Text.Json.Serialization;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Models;

/// <summary>
/// GPU 厂商类型
/// </summary>
public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel,
    Qualcomm,
    Microsoft
}

/// <summary>
/// 显卡设备详细信息
/// </summary>
public record GpuDeviceInfo
{
    public string Name { get; init; } = "";
    public GpuVendor Vendor { get; init; } = GpuVendor.Unknown;
    public string? DriverVersion { get; init; }
    public ulong? TotalVramBytes { get; init; }
    public string? PnpDeviceId { get; init; }
    public bool IsPrimary { get; init; }

    [JsonIgnore]
    public double TotalVramGb => TotalVramBytes.HasValue ? Math.Round((double)TotalVramBytes.Value / (1024 * 1024 * 1024), 2) : 0;
}

/// <summary>
/// 硬件环境上下文
/// </summary>
public record HardwareContext
{
    public IReadOnlyList<GpuDeviceInfo> GpuDevices { get; init; } = Array.Empty<GpuDeviceInfo>();
    public GpuDeviceInfo? PrimaryGpu { get; init; }
    public bool HasNvidiaGpu { get; init; }
    public bool HasDirectX12 { get; init; }
    public string? DetectedCudaPath { get; init; }
    public bool HasPortableCuda { get; init; }
    public bool IsCuda12Available { get; init; }
}

/// <summary>
/// CUDA 环境扫描结果
/// </summary>
public record CudaEnvironmentScanResult
{
    public bool Found { get; init; }
    public string? MainDllDirectory { get; init; }
    public string? CudaVersion { get; init; }
    public bool HasRequiredDlls { get; init; }
    public IReadOnlyList<string> InjectedPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ScannedLocations { get; init; } = Array.Empty<string>();
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 硬件加速策略验证与准备结果
/// </summary>
public record StrategyValidationResult
{
    public bool IsSuccess { get; init; }
    public string StrategyName { get; init; } = "";
    public string? GpuDeviceName { get; init; }
    public IReadOnlyList<string>? InjectedPaths { get; init; }
    public string? Message { get; init; }
    public string? ErrorMessage { get; init; }

    public static StrategyValidationResult Success(string strategyName, string? gpuName = null, IReadOnlyList<string>? paths = null, string? message = null)
        => new()
        {
            IsSuccess = true,
            StrategyName = strategyName,
            GpuDeviceName = gpuName,
            InjectedPaths = paths,
            Message = message ?? $"{strategyName} 准备就绪"
        };

    public static StrategyValidationResult Failure(string strategyName, string errorMessage, string? gpuName = null)
        => new()
        {
            IsSuccess = false,
            StrategyName = strategyName,
            GpuDeviceName = gpuName,
            ErrorMessage = errorMessage
        };
}
