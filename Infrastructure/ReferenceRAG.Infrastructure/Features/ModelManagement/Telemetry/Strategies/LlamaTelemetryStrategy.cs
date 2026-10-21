using System.Text.RegularExpressions;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Telemetry.Strategies;

/// <summary>
/// GGUF / LLamaSharp (llama.cpp) 底层引擎遥测提取策略
/// </summary>
public class LlamaTelemetryStrategy : IModelTelemetryStrategy
{
    private static readonly Regex QuantizationRegex = new(
        @"(q\d+_[a-z0-9_]+|fp16|fp32|bf16|iq\d+_[a-z0-9_]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public bool CanHandle(string? backendType, string? modelPath)
    {
        if (string.Equals(backendType, "llama", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(modelPath) && modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public (ModelHardwareTelemetry Hardware, ModelConfigTelemetry Config) Extract(
        IEmbeddingBackendAdapter? adapter,
        EmbeddingOptions options,
        HardwareContext hardwareContext,
        ModelInfo? modelMetadata)
    {
        var llama = adapter as LlamaEmbeddingAdapter;
        var modelPath = !string.IsNullOrEmpty(llama?.ModelPath) ? llama.ModelPath : options.ModelPath;
        var modelName = !string.IsNullOrEmpty(llama?.ModelName) ? llama.ModelName : options.ModelName;

        var gpuLayers = llama?.GpuLayers ?? (options.UseCuda ? -1 : 0);
        var isGpuMode = gpuLayers != 0 && (hardwareContext.HasNvidiaGpu || hardwareContext.PrimaryGpu != null);

        var totalVramGb = hardwareContext.PrimaryGpu?.TotalVramGb;
        var gpuName = hardwareContext.PrimaryGpu?.Name ?? (hardwareContext.HasNvidiaGpu ? "NVIDIA GPU" : "GPU");

        string executionDevice;
        if (isGpuMode)
        {
            if (hardwareContext.HasNvidiaGpu)
                executionDevice = $"NVIDIA CUDA 12 ({gpuName})";
            else if (hardwareContext.PrimaryGpu?.Vendor == GpuVendor.Intel)
                executionDevice = $"Intel Vulkan GPU ({gpuName})";
            else if (hardwareContext.PrimaryGpu?.Vendor == GpuVendor.Amd)
                executionDevice = $"AMD Vulkan GPU ({gpuName})";
            else
                executionDevice = $"Vulkan GPU ({gpuName})";
        }
        else
        {
            executionDevice = "CPU (llama.cpp 原生加速)";
        }

        var hardware = new ModelHardwareTelemetry
        {
            IsGpuMode = isGpuMode,
            PrimaryGpuName = gpuName,
            TotalVramGb = totalVramGb,
            DriverVersion = hardwareContext.PrimaryGpu?.DriverVersion,
            IsCudaAvailable = hardwareContext.IsCuda12Available,
            GpuLayers = gpuLayers,
            IsFullOffload = gpuLayers == -1 || (gpuLayers >= 28),
            ExecutionDevice = executionDevice,
            OffloadDescription = isGpuMode
                ? (gpuLayers == -1 ? "全量卸载到 GPU" : $"GPU 加速 (卸载 {gpuLayers} 层)")
                : "纯 CPU 计算 (0 层 GPU Offload)"
        };

        // 识别量化类型
        var quant = modelMetadata?.QuantizationType;
        if (string.IsNullOrEmpty(quant) && !string.IsNullOrEmpty(modelPath))
        {
            var match = QuantizationRegex.Match(Path.GetFileName(modelPath));
            if (match.Success)
            {
                quant = match.Value.ToUpperInvariant();
            }
        }
        if (string.IsNullOrEmpty(quant))
        {
            quant = "GGUF";
        }

        // 文件大小
        long fileBytes = 0;
        try
        {
            if (File.Exists(modelPath))
            {
                fileBytes = new FileInfo(modelPath).Length;
            }
            else if (modelMetadata != null && modelMetadata.ModelSizeBytes > 0)
            {
                fileBytes = modelMetadata.ModelSizeBytes;
            }
        }
        catch { }

        var dim = llama?.Dimension ?? modelMetadata?.Dimension ?? 1024;
        var ctx = llama?.MaxSequenceLength ?? (options.MaxSequenceLength > 0 ? options.MaxSequenceLength : 8192);

        var config = new ModelConfigTelemetry
        {
            ModelName = modelName,
            DisplayName = modelMetadata?.DisplayName ?? Path.GetFileNameWithoutExtension(modelPath),
            BackendType = "llama",
            BackendDisplayName = "LLamaSharp (llama.cpp GGUF 原生)",
            Dimension = dim,
            ContextSize = ctx,
            BatchSize = options.BatchSize > 0 ? options.BatchSize : 32,
            PoolingType = llama?.PoolingType.ToString() ?? "Mean",
            Quantization = quant,
            ModelSizeBytes = fileBytes,
            ModelSizeFriendly = FormatBytes(fileBytes),
            SupportsAsymmetricEncoding = llama?.SupportsAsymmetricEncoding ?? true,
            IsSimulationMode = llama?.IsSimulationMode ?? false
        };

        return (hardware, config);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < units.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.##} {units[i]}";
    }
}
