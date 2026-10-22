using System;
using System.Collections.Generic;
using System.Text.Json;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;

namespace ReferenceRAG.Core.Features.HardwareAcceleration.Probing;

/// <summary>
/// 硬件加速探测沙箱工作者（由独立 CLI 子进程或降级进程内调用）
/// </summary>
public static class HardwareAccelerationProbeWorker
{
    public const string JsonStartDelimiter = "__HARDWARE_PROBE_JSON_START__";
    public const string JsonEndDelimiter = "__HARDWARE_PROBE_JSON_END__";

    /// <summary>
    /// CLI 子进程入口点：由 dotnet ReferenceRAG.WebHost.dll --probe-hardware 调用
    /// </summary>
    public static int RunCli(string[] args)
    {
        try
        {
            string? customPath = null;
            int deviceId = 0;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--cuda-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    customPath = args[i + 1];
                }
                else if (string.Equals(args[i], "--device-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var id))
                {
                    deviceId = id;
                }
            }

            var result = ExecuteProbe(customPath, deviceId);
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            Console.WriteLine(JsonStartDelimiter);
            Console.WriteLine(json);
            Console.WriteLine(JsonEndDelimiter);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HardwareAccelerationProbeWorker] 探测执行失败: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
    }

    /// <summary>
    /// 执行核心硬件探测流程（收集 GPU 设备、扫描 CUDA 库、尝试挂载各 ONNX 原生提供者）
    /// </summary>
    public static HardwareAccelerationStatusResult ExecuteProbe(string? configuredPath = null, int deviceId = 0)
    {
        HardwareContext hwContext;
        try
        {
            var detector = new HardwareDetector();
            hwContext = detector.GetContext();
        }
        catch
        {
            hwContext = new HardwareContext();
        }
        var primaryGpu = hwContext.PrimaryGpu;

        CudaEnvironmentScanResult scanResult;
        try
        {
            var scanner = new CudaEnvironmentScanner(new Win32EnvironmentInjector());
            scanResult = scanner.ScanAndInject(configuredPath);
        }
        catch
        {
            scanResult = new CudaEnvironmentScanResult { Found = false };
        }

        bool hasCuda = false;
        bool hasDirectMl = false;
        try
        {
            hasCuda = OnnxExecutionProviderHelper.HasCuda;
            hasDirectMl = OnnxExecutionProviderHelper.HasDirectMl;
        }
        catch { }

        var availableBackends = new List<AvailableBackendInfo>
        {
            new()
            {
                Backend = "Auto",
                Name = "自动感知协商 (智能推荐)",
                Supported = true,
                Reason = "自适应探查最佳可用算力，发生故障时自动平滑降级"
            }
        };

        // 1. CUDA
        bool cudaSupported = false;
        string cudaReason;
        if (!hwContext.HasNvidiaGpu)
        {
            cudaReason = "未检测到支持的 NVIDIA GPU";
        }
        else if (!scanResult.Found && !hwContext.HasPortableCuda)
        {
            cudaReason = $"检测到 {primaryGpu?.Name}，但系统未找到匹配的 CUDA 12 动态库 (cublasLt64_12.dll)";
        }
        else if (!hasCuda)
        {
            cudaReason = "当前构建未包含 CUDA 原生执行模块 (可选用 NVIDIA 专属满血版)";
        }
        else
        {
            try
            {
                using var testOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
                testOptions.GraphOptimizationLevel = Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_DISABLE_ALL;
                if (OnnxExecutionProviderHelper.TryAppendCuda(testOptions, deviceId))
                {
                    cudaSupported = true;
                    cudaReason = $"已就绪 (检测到 {primaryGpu?.Name ?? "NVIDIA GPU"}，已挂载 CUDA 12)";
                }
                else
                {
                    cudaReason = "CUDA 提供者加载失败，请检查驱动版本 (>=528.33)";
                }
            }
            catch (Exception ex)
            {
                cudaReason = $"CUDA 初始化异常: {ex.Message}";
            }
        }

        availableBackends.Add(new AvailableBackendInfo
        {
            Backend = "Cuda",
            Name = "NVIDIA CUDA (满血版)",
            Supported = cudaSupported,
            Reason = cudaReason
        });

        // 2. DirectML (Windows DirectX 12)
        bool dmlSupported = false;
        string dmlReason;
        if (!hwContext.HasDirectX12)
        {
            dmlReason = "仅支持 Windows 10/11 DirectX 12 环境";
        }
        else if (primaryGpu == null)
        {
            dmlReason = "未检测到支持的图形加速卡";
        }
        else
        {
            try
            {
                using var testOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
                testOptions.GraphOptimizationLevel = Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_DISABLE_ALL;
                if (OnnxExecutionProviderHelper.TryAppendDirectMl(testOptions, deviceId))
                {
                    dmlSupported = true;
                    dmlReason = $"已就绪 (基于 DirectX 12 跨平台通用算力: {primaryGpu.Name})";
                }
                else if (hasDirectMl)
                {
                    dmlSupported = true;
                    dmlReason = "已就绪 (DirectML 原生模块已集成)";
                }
                else
                {
                    dmlReason = "当前构建未包含 DirectML 执行提供者 (可选用通用版构建)";
                }
            }
            catch (Exception ex)
            {
                dmlReason = $"DirectML 初始化异常: {ex.Message}";
            }
        }

        availableBackends.Add(new AvailableBackendInfo
        {
            Backend = "DirectML",
            Name = "通用 DirectML (AMD/Intel/通用版)",
            Supported = dmlSupported,
            Reason = dmlReason
        });

        // 3. OpenVINO (Intel Arc 独显 / 酷睿 Iris Xe & UHD 核显)
        bool openVinoSupported = false;
        string openVinoReason;
        if (OnnxExecutionProviderHelper.HasOpenVino)
        {
            try
            {
                using var testOptions = new Microsoft.ML.OnnxRuntime.SessionOptions();
                testOptions.GraphOptimizationLevel = Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_DISABLE_ALL;
                if (OnnxExecutionProviderHelper.TryAppendOpenVino(testOptions, "GPU", out var vinoErr))
                {
                    openVinoSupported = true;
                    openVinoReason = "已就绪 (Intel GPU / 酷睿核显 OpenVINO 原生硬件加速)";
                }
                else
                {
                    openVinoReason = $"OpenVINO 硬件加速暂未就绪 ({vinoErr})";
                }
            }
            catch (Exception ex)
            {
                var innermost = ex;
                while (innermost.InnerException != null) innermost = innermost.InnerException;
                openVinoReason = $"OpenVINO 初始化异常: {innermost.Message}";
            }
        }
        else
        {
            openVinoReason = "当前构建未包含 OpenVINO 模块 (可选用 Intel 专属版镜像)";
        }

        availableBackends.Add(new AvailableBackendInfo
        {
            Backend = "OpenVino",
            Name = "Intel OpenVINO (Arc独显 / 酷睿核显)",
            Supported = openVinoSupported,
            Reason = openVinoReason
        });

        // 4. CPU (纯 CPU 确定性推理)
        availableBackends.Add(new AvailableBackendInfo
        {
            Backend = "Cpu",
            Name = "纯 CPU 运算",
            Supported = true,
            Reason = "多核 CPU 确定性推理 (无显卡依赖与低能耗)"
        });

        string recommended = "Auto";
        string active = cudaSupported ? "Cuda"
            : (openVinoSupported ? "OpenVino"
            : (dmlSupported ? "DirectML" : "Cpu"));

        string activeReason = active switch
        {
            "Cuda" => cudaReason,
            "OpenVino" => openVinoReason,
            "DirectML" => dmlReason,
            _ => primaryGpu?.Vendor switch
            {
                GpuVendor.Intel => !string.IsNullOrEmpty(openVinoReason) ? openVinoReason : "多核 CPU 确定性推理",
                GpuVendor.Amd => "Linux 环境 AMD 核显/APU 采用多核 CPU 向量计算 (对话大模型仍支持 Vulkan 核显加速)",
                _ => "多核 CPU 确定性推理"
            }
        };

        return new HardwareAccelerationStatusResult
        {
            DetectedGpus = new List<GpuDeviceInfo>(hwContext.GpuDevices),
            PrimaryGpu = primaryGpu,
            RecommendedBackend = recommended,
            ActiveBackend = active,
            ActiveBackendReason = activeReason,
            HasNvidiaGpu = hwContext.HasNvidiaGpu,
            HasDirectX12 = hwContext.HasDirectX12,
            AvailableBackends = availableBackends,
            Message = $"已匹配最佳加速方案: {active} ({activeReason})"
        };
    }
}
