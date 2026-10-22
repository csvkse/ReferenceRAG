using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace ReferenceRAG.Core.Features.HardwareAcceleration;

/// <summary>
/// ONNX Runtime 执行提供程序 (Execution Provider) 反射解耦适配器
/// 消除 Microsoft.ML.OnnxRuntime.Gpu / DirectML / OpenVINO / Cpu 多包互斥导致的编译冲突
/// </summary>
public static class OnnxExecutionProviderHelper
{
    private static readonly ILogger _logger = Core.Helpers.StaticLogger.GetLogger("OnnxExecutionProviderHelper");
    private static bool _resolverInitialized = false;
    private static readonly object _resolverLock = new();

    /// <summary>
    /// 诊断信息：记录最后一次解析或绑定的原生库路径
    /// </summary>
    public static string? LastLoadedOnnxPath { get; private set; }

    /// <summary>
    /// 诊断信息：最后一次绑定的原生库是否检测到 OpenVINO 导出函数
    /// </summary>
    public static bool LastLoadedHasOpenVinoExport { get; private set; }

    static OnnxExecutionProviderHelper()
    {
        InitializeNativeResolver();
    }

    /// <summary>
    /// 主动初始化原生库解析器。可在应用程序启动之初显式调用，确保先于任何 ONNX Runtime 类型初始化
    /// </summary>
    public static void InitializeNativeResolver()
    {
        if (_resolverInitialized) return;
        lock (_resolverLock)
        {
            if (_resolverInitialized) return;
            try
            {
                // 为 Microsoft.ML.OnnxRuntime 程序集注册原生库解析器，确保在 Linux/Windows 容器或发布环境中
                // 优先直接绑定本地 runtimes 目录下的 libonnxruntime / onnxruntime.dll，
                // 彻底杜绝全局环境标准 CPU 库抢占导致硬件加速入口缺失 (如 OrtSessionOptionsAppendExecutionProvider_OpenVINO)
                NativeLibrary.SetDllImportResolver(typeof(SessionOptions).Assembly, ResolveOnnxNativeLibrary);
                _logger.LogInformation("ONNX Runtime 原生库解析器已成功注册");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "配置 ONNX Runtime 原生库解析器被跳过 (可能已注册)");
            }
            finally
            {
                _resolverInitialized = true;
            }
        }
    }

    private static IntPtr ResolveOnnxNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "onnxruntime", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var baseDir = AppContext.BaseDirectory;
        var currentDir = Directory.GetCurrentDirectory();

        var candidatePaths = new List<string>();

        if (isWindows)
        {
            candidatePaths.Add(Path.Combine(baseDir, "runtimes", "win-x64", "native", "onnxruntime.dll"));
            candidatePaths.Add(Path.Combine(baseDir, "onnxruntime.dll"));
            candidatePaths.Add(Path.Combine(currentDir, "onnxruntime.dll"));
        }
        else
        {
            // Linux: 优先检查 runtimes/linux-x64/native 目录
            var nativeDir = Path.Combine(baseDir, "runtimes", "linux-x64", "native");
            if (Directory.Exists(nativeDir))
            {
                // 先预热加载 OpenVINO 依赖组件，避免链式符号未解析
                PreloadLinuxOpenVinoDependencies(nativeDir);

                candidatePaths.Add(Path.Combine(nativeDir, "libonnxruntime.so"));
                candidatePaths.Add(Path.Combine(nativeDir, "libonnxruntime.so.1"));
                try
                {
                    candidatePaths.AddRange(Directory.GetFiles(nativeDir, "libonnxruntime.so.*"));
                }
                catch { }
            }

            // 根目录候选
            candidatePaths.Add(Path.Combine(baseDir, "libonnxruntime.so"));
            candidatePaths.Add(Path.Combine(baseDir, "libonnxruntime.so.1"));
            try
            {
                candidatePaths.AddRange(Directory.GetFiles(baseDir, "libonnxruntime.so.*"));
            }
            catch { }

            candidatePaths.Add(Path.Combine(currentDir, "runtimes", "linux-x64", "native", "libonnxruntime.so"));
            candidatePaths.Add(Path.Combine(currentDir, "libonnxruntime.so"));
        }

        IntPtr firstWorkingHandle = IntPtr.Zero;
        string? firstWorkingPath = null;

        foreach (var path in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
            {
                // 检查是否包含 OpenVINO 入口
                var hasVinoExport = NativeLibrary.TryGetExport(handle, "OrtSessionOptionsAppendExecutionProvider_OpenVINO", out _);

                if (HasOpenVino)
                {
                    if (hasVinoExport)
                    {
                        LastLoadedOnnxPath = path;
                        LastLoadedHasOpenVinoExport = true;
                        _logger.LogInformation("ONNX 原生库解析器成功绑定 OpenVINO 硬件加速库: {Path}", path);
                        return handle;
                    }
                    else
                    {
                        _logger.LogWarning("候选 ONNX 原生库未导出 OpenVINO 入口 (可能是 CPU 标准库): {Path}", path);
                        if (firstWorkingHandle == IntPtr.Zero)
                        {
                            firstWorkingHandle = handle;
                            firstWorkingPath = path;
                        }
                    }
                }
                else
                {
                    LastLoadedOnnxPath = path;
                    LastLoadedHasOpenVinoExport = hasVinoExport;
                    return handle;
                }
            }
        }

        if (firstWorkingHandle != IntPtr.Zero)
        {
            LastLoadedOnnxPath = firstWorkingPath;
            LastLoadedHasOpenVinoExport = false;
            _logger.LogWarning("未找到包含 OpenVINO 入口的原生库，回退至首个可用原生库: {Path}", firstWorkingPath);
            return firstWorkingHandle;
        }

        return IntPtr.Zero;
    }

    private static void PreloadLinuxOpenVinoDependencies(string dir)
    {
        var depFiles = new[]
        {
            "libtbb.so.12",
            "libtbb.so",
            "libopenvino.so",
            "libopenvino_c.so",
            "libopenvino_intel_gpu_plugin.so",
            "libopenvino_intel_cpu_plugin.so"
        };

        foreach (var dep in depFiles)
        {
            var p = Path.Combine(dir, dep);
            if (File.Exists(p))
            {
                try
                {
                    NativeLibrary.TryLoad(p, out _);
                }
                catch { }
            }
        }
    }

    private static readonly MethodInfo? CudaMethod =
        typeof(SessionOptions).GetMethod("AppendExecutionProvider_CUDA", new[] { typeof(int) });

    private static readonly MethodInfo? DmlMethod =
        typeof(SessionOptions).GetMethod("AppendExecutionProvider_DML", new[] { typeof(int) });

    private static readonly MethodInfo? OpenVinoMethod =
        typeof(SessionOptions).GetMethod("AppendExecutionProvider_OpenVINO", new[] { typeof(string) });

    /// <summary>
    /// 当前构建运行时是否包含 CUDA 原生执行提供者
    /// </summary>
    public static bool HasCuda => CudaMethod != null;

    /// <summary>
    /// 当前构建运行时是否包含 DirectML 原生执行提供者
    /// </summary>
    public static bool HasDirectMl => DmlMethod != null;

    /// <summary>
    /// 当前构建运行时是否包含 OpenVINO 原生执行提供者
    /// </summary>
    public static bool HasOpenVino => OpenVinoMethod != null;

    /// <summary>
    /// 尝试向 SessionOptions 追加 CUDA 执行提供者
    /// </summary>
    public static bool TryAppendCuda(SessionOptions options, int deviceId = 0)
    {
        if (CudaMethod == null) return false;
        try
        {
            CudaMethod.Invoke(options, new object[] { deviceId });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试向 SessionOptions 追加 DirectML 执行提供者
    /// </summary>
    public static bool TryAppendDirectMl(SessionOptions options, int deviceId = 0)
    {
        if (DmlMethod == null) return false;
        try
        {
            DmlMethod.Invoke(options, new object[] { deviceId });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试向 SessionOptions 追加 OpenVINO 执行提供者
    /// </summary>
    public static bool TryAppendOpenVino(SessionOptions options, string deviceType = "GPU")
    {
        return TryAppendOpenVino(options, deviceType, out _);
    }

    /// <summary>
    /// 尝试向 SessionOptions 追加 OpenVINO 执行提供者并返回诊断原因
    /// </summary>
    public static bool TryAppendOpenVino(SessionOptions options, string deviceType, out string? failureReason)
    {
        failureReason = null;
        if (OpenVinoMethod == null)
        {
            failureReason = "当前构建未包含 OpenVINO 模块";
            return false;
        }

        // 兼容不同的 OpenVINO 设备标识与精度字符串 (如 GPU, GPU_FP16, GPU_FP32, AUTO:GPU,CPU)
        var targets = new[] { deviceType, "GPU", "GPU_FP16", "GPU_FP32", "AUTO:GPU,CPU", "AUTO", "" };
        Exception? lastEx = null;

        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                OpenVinoMethod.Invoke(options, new object[] { target });
                return true;
            }
            catch (Exception ex)
            {
                var innermost = ex;
                while (innermost.InnerException != null) innermost = innermost.InnerException;
                lastEx = innermost;
            }
        }

        if (lastEx is EntryPointNotFoundException)
        {
            var loaded = LastLoadedOnnxPath ?? "默认系统路径";
            failureReason = $"OpenVINO 原生入口未就绪 (当前加载: {loaded}, 缺少 OrtSessionOptionsAppendExecutionProvider_OpenVINO)";
        }
        else
        {
            failureReason = lastEx?.Message ?? "未检测到支持的 Intel GPU 设备";
        }
        return false;
    }

    /// <summary>
    /// 智能配置 ONNX 会话硬件加速：优先按构建支持的最高性能硬件执行，平滑回退至 DirectML / CPU
    /// </summary>
    public static void ConfigureHardwareAcceleration(
        SessionOptions sessionOptions,
        bool preferGpu,
        int deviceId,
        ILogger? logger = null)
    {
        sessionOptions.EnableMemoryPattern = false;

        if (preferGpu)
        {
            if (TryAppendCuda(sessionOptions, deviceId))
            {
                sessionOptions.AppendExecutionProvider_CPU();
                logger?.LogInformation("已挂载 ONNX Runtime CUDA 硬件加速 (设备: {DeviceId})", deviceId);
                return;
            }

            if (TryAppendDirectMl(sessionOptions, deviceId))
            {
                sessionOptions.AppendExecutionProvider_CPU();
                logger?.LogInformation("已挂载 ONNX Runtime DirectML 跨平台硬件加速 (设备: {DeviceId})", deviceId);
                return;
            }

            if (TryAppendOpenVino(sessionOptions, "GPU"))
            {
                sessionOptions.AppendExecutionProvider_CPU();
                logger?.LogInformation("已挂载 ONNX Runtime OpenVINO 硬件加速 (GPU)");
                return;
            }

            logger?.LogWarning("未检测到可用的 GPU 原生执行提供者，回退至 CPU 多核加速");
            sessionOptions.AppendExecutionProvider_CPU();
        }
        else
        {
            // 如果未强制指定 CUDA，但当前运行时内置了 DirectML（如通用版桌面），优先启用 DirectML
            if (TryAppendDirectMl(sessionOptions, deviceId))
            {
                sessionOptions.AppendExecutionProvider_CPU();
                logger?.LogInformation("通用桌面端自动激活 DirectML 硬件加速 (设备: {DeviceId})", deviceId);
                return;
            }

            sessionOptions.AppendExecutionProvider_CPU();
        }
    }

    /// <summary>
    /// 根据硬件加速配置装配 ONNX Runtime 会话执行提供者
    /// </summary>
    public static void ConfigureHardwareAcceleration(
        SessionOptions sessionOptions,
        ReferenceRAG.Core.Models.HardwareAccelerationConfig? config,
        ILogger? logger = null)
    {
        sessionOptions.EnableMemoryPattern = false;

        var backend = config?.Backend ?? ReferenceRAG.Core.Models.AccelerationBackend.Auto;
        var deviceId = config?.DeviceId ?? 0;
        var fallbackToCpu = config?.FallbackToCpuOnFailure ?? true;

        switch (backend)
        {
            case ReferenceRAG.Core.Models.AccelerationBackend.Cpu:
                sessionOptions.AppendExecutionProvider_CPU();
                logger?.LogInformation("已配置纯 CPU 确定性推理执行提供者");
                break;

            case ReferenceRAG.Core.Models.AccelerationBackend.Cuda:
                if (TryAppendCuda(sessionOptions, deviceId))
                {
                    sessionOptions.AppendExecutionProvider_CPU();
                    logger?.LogInformation("使用 NVIDIA CUDA GPU 硬件加速 (设备: {DeviceId})", deviceId);
                }
                else if (fallbackToCpu)
                {
                    sessionOptions.AppendExecutionProvider_CPU();
                    logger?.LogWarning("CUDA 执行提供者不可用，已自动平滑降级至 CPU (设备: {DeviceId})", deviceId);
                }
                else
                {
                    throw new InvalidOperationException($"无法挂载 CUDA 执行提供者 (设备: {deviceId}) 且已关闭 CPU 降级");
                }
                break;

            case ReferenceRAG.Core.Models.AccelerationBackend.DirectML:
                if (TryAppendDirectMl(sessionOptions, deviceId))
                {
                    sessionOptions.AppendExecutionProvider_CPU();
                    logger?.LogInformation("使用 DirectML 跨平台硬件加速 (设备: {DeviceId})", deviceId);
                }
                else if (fallbackToCpu)
                {
                    sessionOptions.AppendExecutionProvider_CPU();
                    logger?.LogWarning("DirectML 执行提供者不可用，已自动平滑降级至 CPU (设备: {DeviceId})", deviceId);
                }
                else
                {
                    throw new InvalidOperationException($"无法挂载 DirectML 执行提供者 (设备: {deviceId}) 且已关闭 CPU 降级");
                }
                break;

            case ReferenceRAG.Core.Models.AccelerationBackend.OpenVino:
                if (TryAppendOpenVino(sessionOptions, "GPU"))
                {
                    sessionOptions.AppendExecutionProvider_CPU();
                    logger?.LogInformation("使用 OpenVINO 硬件加速 (GPU)");
                }
                else if (fallbackToCpu)
                {
                    sessionOptions.AppendExecutionProvider_CPU();
                    logger?.LogWarning("OpenVINO 执行提供者不可用，已自动平滑降级至 CPU");
                }
                else
                {
                    throw new InvalidOperationException("无法挂载 OpenVINO 执行提供者 且已关闭 CPU 降级");
                }
                break;

            case ReferenceRAG.Core.Models.AccelerationBackend.Auto:
            default:
                // 自动感知协商策略：依次尝试 CUDA -> DirectML -> OpenVINO -> CPU
                if (TryAppendCuda(sessionOptions, deviceId))
                {
                    sessionOptions.AppendExecutionProvider_CPU();
                    logger?.LogInformation("自适应协商成功：挂载 CUDA 硬件加速 (设备: {DeviceId})", deviceId);
                    return;
                }

                if (TryAppendDirectMl(sessionOptions, deviceId))
                {
                    sessionOptions.AppendExecutionProvider_CPU();
                    logger?.LogInformation("自适应协商成功：挂载 DirectML 跨平台硬件加速 (设备: {DeviceId})", deviceId);
                    return;
                }

                if (TryAppendOpenVino(sessionOptions, "GPU"))
                {
                    sessionOptions.AppendExecutionProvider_CPU();
                    logger?.LogInformation("自适应协商成功：挂载 OpenVINO 硬件加速 (GPU)");
                    return;
                }

                logger?.LogInformation("自适应协商就绪：未探测到原生 GPU 执行提供者，使用 CPU 多核计算");
                sessionOptions.AppendExecutionProvider_CPU();
                break;
        }
    }
}
