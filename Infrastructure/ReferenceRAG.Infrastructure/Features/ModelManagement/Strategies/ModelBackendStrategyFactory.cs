using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Rerank;
using LLama.Native;
using ReferenceRAG.Core.Features.HardwareAcceleration;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;

namespace ReferenceRAG.Core.Features.ModelManagement.Strategies;

/// <summary>
/// GGUF 后端解析策略
/// </summary>
public sealed class GgufBackendStrategy : IBackendResolutionStrategy
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("GgufBackendStrategy");
    private readonly IGpuOffloadCalculator _offloadCalculator;

    public string StrategyName => "llama";

    public GgufBackendStrategy(IGpuOffloadCalculator? offloadCalculator = null)
    {
        _offloadCalculator = offloadCalculator ?? new GpuOffloadCalculator(new HardwareDetector());
    }

    public bool CanHandle(string modelPath, string? mode = null)
    {
        if (string.Equals(mode, "llama", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "gguf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrEmpty(modelPath)) return false;

        if (modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Directory.Exists(modelPath))
        {
            return Directory.GetFiles(modelPath, "*.gguf", SearchOption.AllDirectories).Length > 0;
        }

        return false;
    }

    public async Task<IEmbeddingBackendAdapter> CreateEmbeddingAdapterAsync(
        string modelPath,
        string modelName,
        EmbeddingConfig config,
        IGpuMemoryManager? memoryManager = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = ResolveGgufFile(modelPath);
        var contextSize = config.MaxSequenceLength > 0 ? config.MaxSequenceLength : 8192;

        // 结合 GPU 环境与模型大小动态计算最优 GPU Offload 层数
        var offloadPlan = _offloadCalculator.CalculateOffload(
            resolvedPath,
            useGpu: config.Acceleration.IsEnabled,
            contextLength: contextSize);

        _logger.LogInformation("[GgufStrategy] 嵌入模型 GGUF 动态 Offload 规划: {Rationale}", offloadPlan.Rationale);

        var adapter = new LlamaEmbeddingAdapter(
            resolvedPath,
            modelName,
            LLamaPoolingType.Mean,
            offloadPlan.GpuLayers,
            contextSize);

        await adapter.InitializeAsync(cancellationToken);
        return adapter;
    }

    public async Task<IRerankBackendAdapter> CreateRerankAdapterAsync(
        string modelPath,
        string modelName,
        RerankConfig config,
        IGpuMemoryManager? memoryManager = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = ResolveGgufFile(modelPath);

        // 结合 GPU 环境与模型大小动态计算最优 GPU Offload 层数
        var offloadPlan = _offloadCalculator.CalculateOffload(
            resolvedPath,
            useGpu: config.Acceleration.IsEnabled,
            contextLength: 4096);

        _logger.LogInformation("[GgufStrategy] 重排模型 GGUF 动态 Offload 规划: {Rationale}", offloadPlan.Rationale);

        var arch = GgufArchitectureDetector.DetectArchitecture(resolvedPath, modelName);
        _logger.LogInformation("[GgufStrategy] 重排模型 GGUF 检测到架构阵营: {Arch} (Model: {Name})", arch, modelName);

        // 若为非 Causal 架构（如 BERT/RoBERTa/BCE 等传统 Cross-Encoder），检测同级/子级目录是否存在官方 model.onnx
        if (arch != "causal")
        {
            var dir = File.Exists(resolvedPath) ? Path.GetDirectoryName(resolvedPath) : resolvedPath;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                var onnxCandidate = Path.Combine(dir, "model.onnx");
                if (!File.Exists(onnxCandidate))
                {
                    var subOnnx = Directory.GetFiles(dir, "model.onnx", SearchOption.AllDirectories);
                    if (subOnnx.Length > 0) onnxCandidate = subOnnx[0];
                }

                if (File.Exists(onnxCandidate))
                {
                    _logger.LogInformation("[GgufStrategy] 检测到非 Causal Cross-Encoder 模型且同目录存在官方 ONNX ({Path})，自动升配至 OnnxRerankAdapter 执行真实高精重排，避免 llama.cpp 算子缺失", onnxCandidate);
                    var onnxOptions = new ReferenceRAG.Core.Services.Rerank.RerankOptions
                    {
                        ModelPath = onnxCandidate,
                        ModelName = modelName,
                        Acceleration = config.Acceleration
                    };
                    var onnxAdapter = new OnnxRerankAdapter(onnxOptions, memoryManager);
                    try
                    {
                        await onnxAdapter.InitializeAsync(cancellationToken);
                        return onnxAdapter;
                    }
                    catch (Exception onnxEx)
                    {
                        _logger.LogWarning(onnxEx, "[GgufStrategy] 尝试升配加载 ONNX 异常，降级回退处理");
                        onnxAdapter.Dispose();
                    }
                }
            }
        }

        IRerankBackendAdapter adapter = arch == "causal"
            ? new LlamaCausalLogitRerankAdapter(resolvedPath, modelName, offloadPlan.GpuLayers, 4096)
            : new LlamaRerankAdapter(resolvedPath, modelName, offloadPlan.GpuLayers, 4096);

        try
        {
            await adapter.InitializeAsync(cancellationToken);
            return adapter;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GgufStrategy] GGUF 重排适配器初始化失败，启用 SafeFallbackRerankAdapter 平滑保底");
            adapter.Dispose();
            var fallback = new SafeFallbackRerankAdapter(resolvedPath, modelName, ex.Message);
            await fallback.InitializeAsync(cancellationToken);
            return fallback;
        }
    }

    private static string ResolveGgufFile(string modelPath)
    {
        if (File.Exists(modelPath) && modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            return modelPath;
        }

        if (Directory.Exists(modelPath))
        {
            var files = Directory.GetFiles(modelPath, "*.gguf", SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                return files[0];
            }
        }

        return modelPath;
    }
}

/// <summary>
/// ONNX Runtime 后端解析策略
/// </summary>
public sealed class OnnxBackendStrategy : IBackendResolutionStrategy
{
    public string StrategyName => "onnx";

    public bool CanHandle(string modelPath, string? mode = null)
    {
        if (string.Equals(mode, "onnx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrEmpty(modelPath)) return false;

        if (modelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Directory.Exists(modelPath))
        {
            return Directory.GetFiles(modelPath, "*.onnx", SearchOption.AllDirectories).Length > 0;
        }

        return false;
    }

    public async Task<IEmbeddingBackendAdapter> CreateEmbeddingAdapterAsync(
        string modelPath,
        string modelName,
        EmbeddingConfig config,
        IGpuMemoryManager? memoryManager = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = ResolveOnnxFile(modelPath);

        var options = new EmbeddingOptions
        {
            ModelPath = resolvedPath,
            ModelName = modelName,
            MaxSequenceLength = config.MaxSequenceLength,
            BatchSize = config.BatchSize,
            Acceleration = config.Acceleration
        };

        var adapter = new OnnxEmbeddingAdapter(options, memoryManager);
        await adapter.InitializeAsync(cancellationToken);
        return adapter;
    }

    public async Task<IRerankBackendAdapter> CreateRerankAdapterAsync(
        string modelPath,
        string modelName,
        RerankConfig config,
        IGpuMemoryManager? memoryManager = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = ResolveOnnxFile(modelPath);

        var options = new RerankOptions
        {
            ModelPath = resolvedPath,
            ModelName = modelName,
            Acceleration = config.Acceleration
        };

        var adapter = new OnnxRerankAdapter(options, memoryManager);
        await adapter.InitializeAsync(cancellationToken);
        return adapter;
    }

    private static string ResolveOnnxFile(string modelPath)
    {
        if (File.Exists(modelPath) && modelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            return modelPath;
        }

        if (Directory.Exists(modelPath))
        {
            var modelOnnx = Path.Combine(modelPath, "model.onnx");
            if (File.Exists(modelOnnx)) return modelOnnx;

            var files = Directory.GetFiles(modelPath, "*.onnx", SearchOption.AllDirectories);
            if (files.Length > 0) return files[0];
        }

        return modelPath;
    }
}

/// <summary>
/// OpenAI 兼容 REST API 后端解析策略
/// </summary>
public sealed class OpenAiBackendStrategy : IBackendResolutionStrategy
{
    public string StrategyName => "openai";

    public bool CanHandle(string modelPath, string? mode = null)
    {
        if (string.Equals(mode, "openai", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(modelPath) &&
            (modelPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             modelPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    public async Task<IEmbeddingBackendAdapter> CreateEmbeddingAdapterAsync(
        string modelPath,
        string modelName,
        EmbeddingConfig config,
        IGpuMemoryManager? memoryManager = null,
        CancellationToken cancellationToken = default)
    {
        var adapter = new OpenAiEmbeddingAdapter(config);
        await adapter.InitializeAsync(cancellationToken);
        return adapter;
    }

    public async Task<IRerankBackendAdapter> CreateRerankAdapterAsync(
        string modelPath,
        string modelName,
        RerankConfig config,
        IGpuMemoryManager? memoryManager = null,
        CancellationToken cancellationToken = default)
    {
        var adapter = new OpenAiRerankAdapter(config);
        await adapter.InitializeAsync(cancellationToken);
        return adapter;
    }
}

/// <summary>
/// 模型后端策略工厂管理器
/// </summary>
public static class ModelBackendStrategyFactory
{
    private static readonly List<IBackendResolutionStrategy> _strategies = new()
    {
        new OpenAiBackendStrategy(),
        new GgufBackendStrategy(),
        new OnnxBackendStrategy()
    };

    /// <summary>
    /// 根据路径和模式选择最匹配的策略
    /// </summary>
    public static IBackendResolutionStrategy ResolveStrategy(string modelPath, string? mode = null)
    {
        foreach (var strategy in _strategies)
        {
            if (strategy.CanHandle(modelPath, mode))
            {
                return strategy;
            }
        }

        // 默认回退 ONNX 策略
        return _strategies.OfType<OnnxBackendStrategy>().First();
    }
}
