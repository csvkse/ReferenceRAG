using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Tokenizers;
using ReferenceRAG.Core.Services.Rerank;
using ReferenceRAG.Core.Features.HardwareAcceleration;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry.Strategies;

namespace ReferenceRAG.Core.Extensions;

public static class ModelManagementExtensions
{
    public static IServiceCollection AddModelManagement(this IServiceCollection services)
    {
        // 注册硬件检测与自适应执行加速
        services.AddHardwareAcceleration();

        // 注册模型遥测策略与门面协调器
        services.AddSingleton<IModelTelemetryStrategy, LlamaTelemetryStrategy>();
        services.AddSingleton<IModelTelemetryStrategy, OnnxTelemetryStrategy>();
        services.AddSingleton<IModelTelemetryStrategy, OpenAiTelemetryStrategy>();
        services.AddSingleton<IModelTelemetryService, ModelTelemetryService>();

        // 注册窗口与托盘生命周期感知服务（单例）
        services.AddSingleton<IWindowStateService, WindowStateService>();

        // 注册 GPU 显存管理器（单例 + HostedService）
        services.AddSingleton<IGpuMemoryManager, GpuMemoryManager>();
        services.AddHostedService(sp => (GpuMemoryManager)sp.GetRequiredService<IGpuMemoryManager>());

        services.AddSingleton<IModelManager>(sp =>
        {
            var configManager = sp.GetRequiredService<ConfigManager>();
            var cfg = configManager.Load();
            var dataPath = cfg.DataPath ?? "data";
            string modelsPath;
            var modelsRootPath = cfg.ModelsRootPath;
            if (!string.IsNullOrEmpty(modelsRootPath) && Path.IsPathRooted(modelsRootPath))
                modelsPath = modelsRootPath;
            else
                modelsPath = Path.Combine(dataPath, modelsRootPath ?? "models");
            Console.WriteLine($"[ModelManager] 使用模型路径: {modelsPath}");
            return new ModelManager(modelsPath, configManager);
        });

        services.AddSingleton<IEmbeddingService>(sp =>
        {
            var configManager = sp.GetRequiredService<ConfigManager>();
            var cfg = configManager.Load();
            var memoryManager = sp.GetService<IGpuMemoryManager>();
            var windowState = sp.GetService<IWindowStateService>();

            var options = new EmbeddingOptions
            {
                ModelPath = cfg.Embedding.ModelPath,
                ModelName = cfg.Embedding.ModelName,
                MaxSequenceLength = cfg.Embedding.MaxSequenceLength,
                BatchSize = cfg.Embedding.BatchSize,
                UseCuda = cfg.Embedding.UseCuda,
                CudaDeviceId = cfg.Embedding.CudaDeviceId,
                CudaLibraryPath = cfg.Embedding.CudaLibraryPath,
                IdleTimeoutSeconds = cfg.ModelIdleTimeoutSeconds
            };
            return new LazyEmbeddingService(options, memoryManager, () => new EmbeddingService(options, null), windowState, configManager);
        });

        services.AddSingleton<IEmbeddingTokenCounter>(sp =>
        {
            return new DynamicEmbeddingTokenCounter(sp.GetRequiredService<ConfigManager>());
        });

        services.AddSingleton<IRerankService>(sp =>
        {
            var configManager = sp.GetRequiredService<ConfigManager>();
            var cfg = configManager.Load();
            var memoryManager = sp.GetService<IGpuMemoryManager>();
            var windowState = sp.GetService<IWindowStateService>();
            var rerankConfig = cfg.Rerank;
            string modelPath = rerankConfig.ModelPath ?? string.Empty;

            if (string.IsNullOrEmpty(modelPath))
            {
                var cfgModelsPath = cfg.ModelsRootPath
                    ?? Path.Combine(cfg.DataPath ?? "data", "models");
                var targetName = !string.IsNullOrEmpty(rerankConfig.CurrentModel)
                    ? rerankConfig.CurrentModel
                    : rerankConfig.ModelName;
                var rerankerDir = Path.Combine(cfgModelsPath, "Reranker", targetName);
                modelPath = Directory.Exists(rerankerDir) && Directory.GetFiles(rerankerDir, "*.gguf", SearchOption.AllDirectories).Length > 0
                    ? Directory.GetFiles(rerankerDir, "*.gguf", SearchOption.AllDirectories)[0]
                    : Path.Combine(rerankerDir, "model.onnx");
                if (!File.Exists(modelPath))
                    modelPath = Path.Combine(cfgModelsPath, targetName, "model.onnx");
            }

            var options = new RerankOptions
            {
                ModelPath = modelPath,
                ModelName = rerankConfig.ModelName,
                UseCuda = rerankConfig.UseCuda,
                CudaDeviceId = rerankConfig.CudaDeviceId,
                CudaLibraryPath = cfg.Embedding.CudaLibraryPath,
                IdleTimeoutSeconds = cfg.ModelIdleTimeoutSeconds
            };
            return new LazyRerankService(
                options.ModelName,
                () => new OnnxRerankService(options, null),
                memoryManager,
                options.UseCuda,
                options.CudaDeviceId,
                modelPath,
                cfg.ModelIdleTimeoutSeconds,
                windowState,
                configManager);
        });

        return services;
    }
}
