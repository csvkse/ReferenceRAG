using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Rerank;

namespace ReferenceRAG.Core.Extensions;

public static class ModelManagementExtensions
{
    public static IServiceCollection AddModelManagement(this IServiceCollection services)
    {
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
            var cfg = sp.GetRequiredService<ConfigManager>().Load();
            var memoryManager = sp.GetService<IGpuMemoryManager>();

            if (cfg.Embedding.Mode == "openai")
                return new OpenAIEmbeddingService(cfg.Embedding);

            return new EmbeddingService(new EmbeddingOptions
            {
                ModelPath = cfg.Embedding.ModelPath,
                ModelName = cfg.Embedding.ModelName,
                MaxSequenceLength = cfg.Embedding.MaxSequenceLength,
                BatchSize = cfg.Embedding.BatchSize,
                UseCuda = cfg.Embedding.UseCuda,
                CudaDeviceId = cfg.Embedding.CudaDeviceId,
                CudaLibraryPath = cfg.Embedding.CudaLibraryPath
            }, memoryManager);
        });

        services.AddSingleton<IRerankService>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>().Load();
            var memoryManager = sp.GetService<IGpuMemoryManager>();
            var rerankConfig = cfg.Rerank;
            string modelPath = rerankConfig.ModelPath ?? string.Empty;

            if (string.IsNullOrEmpty(modelPath))
            {
                var cfgModelsPath = cfg.ModelsRootPath
                    ?? Path.Combine(cfg.DataPath ?? "data", "models");
                var targetName = !string.IsNullOrEmpty(rerankConfig.CurrentModel)
                    ? rerankConfig.CurrentModel
                    : rerankConfig.ModelName;
                modelPath = Path.Combine(cfgModelsPath, "Reranker", targetName, "model.onnx");
                if (!File.Exists(modelPath))
                    modelPath = Path.Combine(cfgModelsPath, targetName, "model.onnx");
            }

            if (rerankConfig.Mode == "openai")
                return new OpenAIRerankService(rerankConfig);

            return new OnnxRerankService(new RerankOptions
            {
                ModelPath = modelPath,
                ModelName = rerankConfig.ModelName,
                UseCuda = rerankConfig.UseCuda,
                CudaDeviceId = rerankConfig.CudaDeviceId,
                CudaLibraryPath = cfg.Embedding.CudaLibraryPath
            }, memoryManager);
        });

        return services;
    }
}
