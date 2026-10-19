using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace ReferenceRAG.Core.Features.HardwareAcceleration;

/// <summary>
/// ONNX Runtime 执行提供程序 (Execution Provider) 反射解耦适配器
/// 消除 Microsoft.ML.OnnxRuntime.Gpu / DirectML / OpenVINO / Cpu 多包互斥导致的编译冲突
/// </summary>
public static class OnnxExecutionProviderHelper
{
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
        if (OpenVinoMethod == null) return false;
        try
        {
            OpenVinoMethod.Invoke(options, new object[] { deviceType });
            return true;
        }
        catch
        {
            return false;
        }
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
