using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Detection;

/// <summary>
/// 硬件设备探测器
/// 通过 WMI (Win32_VideoController)、nvidia-smi 及系统信息探测 GPU 与运行时状态
/// </summary>
public class HardwareDetector : IHardwareDetector
{
    private readonly ILogger<HardwareDetector>? _logger;
    private readonly object _lock = new();
    private HardwareContext? _cachedContext;

    public HardwareDetector(ILogger<HardwareDetector>? logger = null)
    {
        _logger = logger;
    }

    public HardwareContext GetContext(bool forceRefresh = false)
    {
        if (!forceRefresh && _cachedContext != null)
        {
            return _cachedContext;
        }

        lock (_lock)
        {
            if (!forceRefresh && _cachedContext != null)
            {
                return _cachedContext;
            }

            _cachedContext = DetectHardware();
            return _cachedContext;
        }
    }

    private HardwareContext DetectHardware()
    {
        var gpuList = new List<GpuDeviceInfo>();
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var hasDx12 = isWindows && Environment.OSVersion.Version.Major >= 10;

        if (isWindows)
        {
            try
            {
                gpuList.AddRange(DetectGpuDevicesViaWmi());
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[HardwareDetector] WMI GPU 探测失败，尝试回退其他方式");
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                gpuList.AddRange(DetectLinuxGpuDevices());
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[HardwareDetector] Linux DRM GPU 探测失败");
            }
        }

        // 若检测到 NVIDIA 显卡或尚未找到显卡，尝试通过 nvidia-smi 补充更精确的显存与驱动信息
        var nvidiaGpu = gpuList.FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia);
        if (nvidiaGpu != null || gpuList.Count == 0)
        {
            var smiInfo = ProbeNvidiaSmi();
            if (smiInfo != null)
            {
                if (nvidiaGpu != null)
                {
                    // 替换为更准确的显存与驱动数据（解决 WMI 4GB uint32 溢出截断）
                    gpuList.Remove(nvidiaGpu);
                    gpuList.Insert(0, nvidiaGpu with
                    {
                        Name = !string.IsNullOrWhiteSpace(smiInfo.Name) ? smiInfo.Name : nvidiaGpu.Name,
                        TotalVramBytes = smiInfo.TotalVramBytes ?? nvidiaGpu.TotalVramBytes,
                        DriverVersion = smiInfo.DriverVersion ?? nvidiaGpu.DriverVersion,
                        IsPrimary = true
                    });
                }
                else
                {
                    gpuList.Insert(0, smiInfo with { IsPrimary = true });
                }
            }
        }

        // 过滤虚拟显卡（若存在物理独立/集成显卡）
        var physicalGpus = gpuList.Where(g => !IsVirtualOrBasicAdapter(g.Name)).ToList();
        var effectiveGpus = physicalGpus.Count > 0 ? physicalGpus : gpuList;

        // 选定主 GPU（NVIDIA > AMD > Intel > Other）
        var primary = effectiveGpus
            .OrderByDescending(g => GetVendorPreference(g.Vendor))
            .ThenByDescending(g => g.TotalVramBytes ?? 0)
            .FirstOrDefault();

        var hasNvidia = effectiveGpus.Any(g => g.Vendor == GpuVendor.Nvidia);

        // 检查便携式目录是否存在
        var portableCudaPath = GetPortableCudaDirectory();
        var hasPortable = Directory.Exists(portableCudaPath) &&
                          File.Exists(Path.Combine(portableCudaPath, "cublasLt64_12.dll"));

        var context = new HardwareContext
        {
            GpuDevices = effectiveGpus,
            PrimaryGpu = primary,
            HasNvidiaGpu = hasNvidia,
            HasDirectX12 = hasDx12,
            HasPortableCuda = hasPortable,
            IsCuda12Available = false // 由扫描器判定
        };

        _logger?.LogInformation("[HardwareDetector] 硬件探测完成: 主GPU={PrimaryGpu}, 厂商={Vendor}, VRAM={Vram}GB, DX12={Dx12}",
            primary?.Name ?? "未检测到独立显卡",
            primary?.Vendor.ToString() ?? "Unknown",
            primary?.TotalVramGb ?? 0,
            hasDx12);

        return context;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<GpuDeviceInfo> DetectGpuDevicesViaWmi()
    {
        var result = new List<GpuDeviceInfo>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion, PNPDeviceID FROM Win32_VideoController");
        using var collection = searcher.Get();

        foreach (var obj in collection)
        {
            var name = obj["Name"]?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;

            var pnpId = obj["PNPDeviceID"]?.ToString()?.Trim();
            var driverVer = obj["DriverVersion"]?.ToString()?.Trim();
            ulong? ramBytes = null;

            if (obj["AdapterRAM"] != null && ulong.TryParse(obj["AdapterRAM"].ToString(), out var ramVal) && ramVal > 0)
            {
                ramBytes = ramVal;
            }

            var vendor = ParseVendor(name, pnpId);
            result.Add(new GpuDeviceInfo
            {
                Name = name,
                Vendor = vendor,
                DriverVersion = driverVer,
                TotalVramBytes = ramBytes,
                PnpDeviceId = pnpId
            });
        }

        return result;
    }

    private static GpuDeviceInfo? ProbeNvidiaSmi()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                return null;
            }

            if (process.ExitCode != 0) return null;

            var output = process.StandardOutput.ReadLine();
            if (string.IsNullOrWhiteSpace(output)) return null;

            // 输出格式示例: "NVIDIA GeForce RTX 4060 Laptop GPU, 8188, 610.62"
            var parts = output.Split(',', StringSplitOptions.TrimEntries);
            var name = parts.Length > 0 ? parts[0] : "NVIDIA GPU";
            ulong? vramBytes = null;
            if (parts.Length > 1 && double.TryParse(parts[1], out var mb))
            {
                vramBytes = (ulong)(mb * 1024 * 1024);
            }
            var driver = parts.Length > 2 ? parts[2] : null;

            return new GpuDeviceInfo
            {
                Name = name,
                Vendor = GpuVendor.Nvidia,
                DriverVersion = driver,
                TotalVramBytes = vramBytes,
                IsPrimary = true
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<GpuDeviceInfo> DetectLinuxGpuDevices()
    {
        var result = new List<GpuDeviceInfo>();
        try
        {
            const string drmPath = "/sys/class/drm";
            if (Directory.Exists(drmPath))
            {
                foreach (var cardDir in Directory.GetDirectories(drmPath, "card[0-9]*"))
                {
                    var deviceDir = Path.Combine(cardDir, "device");
                    if (!Directory.Exists(deviceDir)) continue;

                    var vendorFile = Path.Combine(deviceDir, "vendor");
                    if (File.Exists(vendorFile))
                    {
                        var vendorIdHex = File.ReadAllText(vendorFile).Trim();
                        var vendor = ParseLinuxVendorId(vendorIdHex);
                        if (vendor == GpuVendor.Unknown) continue;

                        var name = vendor switch
                        {
                            GpuVendor.Nvidia => "NVIDIA GPU",
                            GpuVendor.Amd => "AMD Radeon GPU",
                            GpuVendor.Intel => "Intel Graphics / Arc GPU",
                            _ => "Linux GPU"
                        };

                        result.Add(new GpuDeviceInfo
                        {
                            Name = name,
                            Vendor = vendor,
                            PnpDeviceId = Path.GetFileName(cardDir)
                        });
                    }
                }
            }
        }
        catch { }

        return result;
    }

    private static GpuVendor ParseLinuxVendorId(string vendorIdHex)
    {
        if (vendorIdHex.Equals("0x10de", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Nvidia;
        if (vendorIdHex.Equals("0x1002", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Amd;
        if (vendorIdHex.Equals("0x8086", StringComparison.OrdinalIgnoreCase)) return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }

    private static GpuVendor ParseVendor(string name, string? pnpId)
    {
        var upperPnp = pnpId?.ToUpperInvariant() ?? "";
        var upperName = name.ToUpperInvariant();

        if (upperPnp.Contains("VEN_10DE") || upperName.Contains("NVIDIA") || upperName.Contains("GEFORCE") || upperName.Contains("RTX"))
            return GpuVendor.Nvidia;

        if (upperPnp.Contains("VEN_1002") || upperName.Contains("AMD") || upperName.Contains("RADEON"))
            return GpuVendor.Amd;

        if (upperPnp.Contains("VEN_8086") || upperName.Contains("INTEL") || upperName.Contains("IRIS") || upperName.Contains("ARC"))
            return GpuVendor.Intel;

        if (upperPnp.Contains("VEN_5143") || upperName.Contains("QUALCOMM") || upperName.Contains("ADRENO"))
            return GpuVendor.Qualcomm;

        if (upperName.Contains("MICROSOFT"))
            return GpuVendor.Microsoft;

        return GpuVendor.Unknown;
    }

    private static bool IsVirtualOrBasicAdapter(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper.Contains("BASIC DISPLAY") ||
               upper.Contains("VIRTUAL") ||
               upper.Contains("RDPDD") ||
               upper.Contains("GAMEVIEWER");
    }

    private static int GetVendorPreference(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => 100,
        GpuVendor.Amd => 80,
        GpuVendor.Intel => 60,
        GpuVendor.Qualcomm => 40,
        _ => 0
    };

    public static string GetPortableCudaDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda");
    }
}
