using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Features.ModelManagement.StateMachine;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry.Strategies;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.ModelManagement.Telemetry;

/// <summary>
/// 模型遥测协调器服务实现 (Facade + Strategy Pattern)
/// </summary>
public class ModelTelemetryService : IModelTelemetryService
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("ModelTelemetryService");

    private readonly IEmbeddingService _embeddingService;
    private readonly IRerankService? _rerankService;
    private readonly IHardwareDetector _hardwareDetector;
    private readonly IModelManager _modelManager;
    private readonly IReadOnlyList<IModelTelemetryStrategy> _strategies;

    public ModelTelemetryService(
        IEmbeddingService embeddingService,
        IHardwareDetector hardwareDetector,
        IModelManager modelManager,
        IEnumerable<IModelTelemetryStrategy>? strategies = null,
        IRerankService? rerankService = null)
    {
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        _hardwareDetector = hardwareDetector ?? throw new ArgumentNullException(nameof(hardwareDetector));
        _modelManager = modelManager ?? throw new ArgumentNullException(nameof(modelManager));
        _rerankService = rerankService;

        // 如果未通过容器注入策略集合，初始化默认策略流水线
        _strategies = strategies?.ToList() ?? new List<IModelTelemetryStrategy>
        {
            new LlamaTelemetryStrategy(),
            new OnnxTelemetryStrategy(),
            new OpenAiTelemetryStrategy()
        };
    }

    public ModelRuntimeTelemetry GetCurrentEmbeddingTelemetry()
    {
        ModelLifecycleSnapshot snapshot;
        IEmbeddingBackendAdapter? adapter = null;
        EmbeddingOptions options;

        if (_embeddingService is IModelRuntimeTelemetryProvider provider)
        {
            snapshot = provider.GetLifecycleSnapshot();
            adapter = provider.CurrentAdapter;
            options = provider.CurrentOptions;
        }
        else
        {
            snapshot = new ModelLifecycleSnapshot
            {
                State = ModelState.Ready,
                IsReady = true,
                StateDescription = "就绪",
                ActiveLeases = 0
            };
            options = new EmbeddingOptions
            {
                ModelName = _embeddingService.ModelName,
                MaxSequenceLength = 512
            };
        }

        var hardwareContext = _hardwareDetector.GetContext();
        var modelMetadata = _modelManager.GetCurrentModel();

        var backendType = adapter?.BackendType;
        var modelPath = !string.IsNullOrEmpty(adapter?.ModelPath) ? adapter.ModelPath : options.ModelPath;

        // 根据策略模式选取适配器遥测策略
        var strategy = _strategies.FirstOrDefault(s => s.CanHandle(backendType, modelPath))
                       ?? _strategies.FirstOrDefault(s => s.CanHandle(null, modelMetadata?.LocalPath))
                       ?? (modelPath?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true
                           ? (IModelTelemetryStrategy?)_strategies.OfType<LlamaTelemetryStrategy>().FirstOrDefault()
                           : (IModelTelemetryStrategy?)_strategies.OfType<OnnxTelemetryStrategy>().FirstOrDefault())
                       ?? _strategies.First();

        var (hardware, config) = strategy.Extract(adapter, options, hardwareContext, modelMetadata);

        return new ModelRuntimeTelemetry
        {
            StateMachine = snapshot,
            Hardware = hardware,
            Configuration = config,
            Timestamp = DateTime.UtcNow
        };
    }

    public ModelRuntimeTelemetry GetCurrentRerankTelemetry()
    {
        ModelLifecycleSnapshot snapshot;
        string modelName = string.Empty;
        string modelPath = string.Empty;
        bool useCuda = false;

        if (_rerankService is IRerankRuntimeTelemetryProvider provider)
        {
            snapshot = provider.GetLifecycleSnapshot();
            modelName = provider.ModelName;
            modelPath = provider.ModelPath;
            useCuda = provider.UseCuda;
        }
        else if (_rerankService != null)
        {
            snapshot = new ModelLifecycleSnapshot
            {
                State = _rerankService.IsLoaded ? ModelState.Ready : ModelState.Unloaded,
                IsReady = _rerankService.IsLoaded,
                StateDescription = _rerankService.IsLoaded ? "就绪" : "未加载",
                ActiveLeases = 0
            };
            modelName = _rerankService.ModelName;
        }
        else
        {
            snapshot = new ModelLifecycleSnapshot
            {
                State = ModelState.Unloaded,
                IsReady = false,
                StateDescription = "未配置重排模型"
            };
        }

        var hardwareContext = _hardwareDetector.GetContext();
        var rerankMetadata = _modelManager.GetCurrentRerankModel();

        var isGpu = useCuda && (hardwareContext.IsCuda12Available || hardwareContext.PrimaryGpu != null);
        var gpuName = hardwareContext.PrimaryGpu?.Name ?? "NVIDIA GPU";
        var executionDevice = isGpu
            ? (hardwareContext.PrimaryGpu?.Name != null ? $"{hardwareContext.PrimaryGpu.Name} (CUDA 12)" : "GPU (CUDA)")
            : "CPU";

        var isGguf = modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
        var quantization = ModelManager.ExtractQuantizationFromFileName(modelPath) ?? (isGguf ? "GGUF" : "FP32");

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
            else if (rerankMetadata != null && rerankMetadata.ModelSizeBytes > 0)
            {
                fileBytes = rerankMetadata.ModelSizeBytes;
            }
        }
        catch { }

        return new ModelRuntimeTelemetry
        {
            StateMachine = snapshot,
            Hardware = new ModelHardwareTelemetry
            {
                IsGpuMode = isGpu,
                ExecutionDevice = executionDevice,
                PrimaryGpuName = isGpu ? gpuName : null,
                TotalVramGb = isGpu ? hardwareContext.PrimaryGpu?.TotalVramGb : null,
                IsCudaAvailable = hardwareContext.IsCuda12Available,
                DriverVersion = hardwareContext.PrimaryGpu?.DriverVersion,
                GpuLayers = isGpu ? -1 : 0,
                IsFullOffload = isGpu,
                OffloadDescription = isGpu ? "CUDA Execution Provider (全量加速)" : "CPU Execution Provider"
            },
            Configuration = new ModelConfigTelemetry
            {
                BackendType = isGguf ? "llama_cpp" : "onnx",
                BackendDisplayName = isGguf ? "LLamaSharp GGUF (Native)" : "ONNX Runtime (Cross-Encoder)",
                ModelName = !string.IsNullOrEmpty(modelName) ? modelName : (rerankMetadata?.Name ?? string.Empty),
                DisplayName = rerankMetadata?.DisplayName ?? modelName,
                Dimension = rerankMetadata?.Dimension ?? 768,
                ContextSize = rerankMetadata?.MaxSequenceLength ?? 512,
                BatchSize = 1,
                PoolingType = "Cross-Encoder Classification",
                Quantization = quantization,
                ModelSizeBytes = fileBytes,
                ModelSizeFriendly = FormatBytes(fileBytes)
            },
            Timestamp = DateTime.UtcNow
        };
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
