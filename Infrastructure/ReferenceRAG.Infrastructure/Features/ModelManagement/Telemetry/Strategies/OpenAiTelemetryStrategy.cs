using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Telemetry.Strategies;

/// <summary>
/// OpenAI 兼容云端 / 远程 API 底层引擎遥测提取策略
/// </summary>
public class OpenAiTelemetryStrategy : IModelTelemetryStrategy
{
    public bool CanHandle(string? backendType, string? modelPath)
    {
        if (string.Equals(backendType, "openai", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(modelPath) && (modelPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || modelPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    public (ModelHardwareTelemetry Hardware, ModelConfigTelemetry Config) Extract(
        IEmbeddingBackendAdapter? adapter,
        EmbeddingOptions options,
        HardwareContext hardwareContext,
        ModelInfo? modelMetadata)
    {
        var modelName = !string.IsNullOrEmpty(adapter?.ModelName) ? adapter.ModelName : options.ModelName;

        var hardware = new ModelHardwareTelemetry
        {
            IsGpuMode = false,
            PrimaryGpuName = null,
            TotalVramGb = null,
            DriverVersion = null,
            IsCudaAvailable = hardwareContext.IsCuda12Available,
            GpuLayers = 0,
            IsFullOffload = false,
            ExecutionDevice = "Cloud Remote API (OpenAI 协议服务)",
            OffloadDescription = "远程服务端托管计算 (无本地 GPU 占用)"
        };

        var dim = adapter?.Dimension ?? modelMetadata?.Dimension ?? 1536;
        var ctx = adapter?.MaxSequenceLength ?? (options.MaxSequenceLength > 0 ? options.MaxSequenceLength : 8192);

        var config = new ModelConfigTelemetry
        {
            ModelName = modelName,
            DisplayName = modelMetadata?.DisplayName ?? modelName,
            BackendType = "openai",
            BackendDisplayName = "OpenAI 兼容 REST API",
            Dimension = dim,
            ContextSize = ctx,
            BatchSize = options.BatchSize > 0 ? options.BatchSize : 32,
            PoolingType = "Remote Hosted",
            Quantization = "Remote",
            ModelSizeBytes = 0,
            ModelSizeFriendly = "云端托管",
            SupportsAsymmetricEncoding = false,
            IsSimulationMode = false
        };

        return (hardware, config);
    }
}
