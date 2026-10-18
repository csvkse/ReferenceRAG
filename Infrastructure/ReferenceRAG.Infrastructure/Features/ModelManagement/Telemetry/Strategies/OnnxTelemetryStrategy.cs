using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Telemetry.Strategies;

/// <summary>
/// ONNX Runtime 底层引擎遥测提取策略
/// </summary>
public class OnnxTelemetryStrategy : IModelTelemetryStrategy
{
    public bool CanHandle(string? backendType, string? modelPath)
    {
        if (string.Equals(backendType, "onnx", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(modelPath))
        {
            if (modelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) return true;
            if (Directory.Exists(modelPath)) return true;
        }
        return false;
    }

    public (ModelHardwareTelemetry Hardware, ModelConfigTelemetry Config) Extract(
        IEmbeddingBackendAdapter? adapter,
        EmbeddingOptions options,
        HardwareContext hardwareContext,
        ModelInfo? modelMetadata)
    {
        var modelPath = !string.IsNullOrEmpty(adapter?.ModelPath) ? adapter.ModelPath : options.ModelPath;
        var modelName = !string.IsNullOrEmpty(adapter?.ModelName) ? adapter.ModelName : options.ModelName;

        var isCudaRequested = options.UseCuda;
        var isGpuMode = isCudaRequested && hardwareContext.HasNvidiaGpu;

        var gpuName = hardwareContext.PrimaryGpu?.Name ?? "NVIDIA GPU";
        var totalVramGb = hardwareContext.PrimaryGpu?.TotalVramGb;

        var hardware = new ModelHardwareTelemetry
        {
            IsGpuMode = isGpuMode,
            PrimaryGpuName = isGpuMode ? gpuName : null,
            TotalVramGb = isGpuMode ? totalVramGb : null,
            DriverVersion = hardwareContext.PrimaryGpu?.DriverVersion,
            IsCudaAvailable = hardwareContext.IsCuda12Available,
            GpuLayers = isGpuMode ? -1 : 0,
            IsFullOffload = isGpuMode,
            ExecutionDevice = isGpuMode
                ? $"NVIDIA CUDA ({gpuName})"
                : "CPU (Microsoft ONNX Runtime)",
            OffloadDescription = isGpuMode
                ? "CUDA Execution Provider (全量加速)"
                : "CPU Execution Provider"
        };

        long fileBytes = 0;
        try
        {
            if (File.Exists(modelPath))
            {
                fileBytes = new FileInfo(modelPath).Length;
            }
            else if (Directory.Exists(modelPath))
            {
                var dir = new DirectoryInfo(modelPath);
                fileBytes = dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            else if (modelMetadata != null && modelMetadata.ModelSizeBytes > 0)
            {
                fileBytes = modelMetadata.ModelSizeBytes;
            }
        }
        catch { }

        var quant = modelMetadata?.QuantizationType;
        if (string.IsNullOrEmpty(quant))
        {
            quant = modelPath.Contains("quant", StringComparison.OrdinalIgnoreCase) ? "INT8" : "FP32";
        }

        var dim = adapter?.Dimension ?? modelMetadata?.Dimension ?? 0;
        var ctx = adapter?.MaxSequenceLength ?? (options.MaxSequenceLength > 0 ? options.MaxSequenceLength : 512);

        var config = new ModelConfigTelemetry
        {
            ModelName = modelName,
            DisplayName = modelMetadata?.DisplayName ?? Path.GetFileNameWithoutExtension(modelPath),
            BackendType = "onnx",
            BackendDisplayName = "ONNX Runtime (跨平台原生)",
            Dimension = dim,
            ContextSize = ctx,
            BatchSize = options.BatchSize > 0 ? options.BatchSize : 32,
            PoolingType = "CLS / Mean",
            Quantization = quant,
            ModelSizeBytes = fileBytes,
            ModelSizeFriendly = FormatBytes(fileBytes),
            SupportsAsymmetricEncoding = adapter?.SupportsAsymmetricEncoding ?? (options.AsymmetricEncoding != null),
            IsSimulationMode = adapter?.IsSimulationMode ?? false
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
