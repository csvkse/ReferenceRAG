using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.Adapters;

/// <summary>
/// 默认嵌入模型画像适配器
/// 自动识别常见 ONNX / API 嵌入模型的物理上下文限制与注意力黄金区间
/// </summary>
public class DefaultModelProfileAdapter : IModelProfileAdapter
{
    public bool CanHandle(string? modelName, string? mode)
    {
        // 作为默认回退适配器，可处理所有模型
        return true;
    }

    public ModelEmbeddingProfile GetProfile(EmbeddingConfig? config)
    {
        if (config == null)
        {
            return CreateStandard512Profile("default-model", 512);
        }

        var modelName = (config.ModelName ?? string.Empty).ToLowerInvariant();
        var isApi = string.Equals(config.Mode, "openai", StringComparison.OrdinalIgnoreCase);

        // 1. BGE-M3 (8192 tokens)
        if (modelName.Contains("bge-m3"))
        {
            return new ModelEmbeddingProfile
            {
                ModelName = config.ModelName ?? "bge-m3",
                PhysicalMaxTokens = 8192,
                SweetSpotMinTokens = 128,
                SweetSpotOptimalTokens = 1500,
                SweetSpotMaxTokens = 2048,
                DefaultOverlapRatio = 0.08f,
                SupportsMultiPageContext = true,
                VectorDimension = config.ApiDimension ?? 1024
            };
        }

        // 2. Jina Embeddings (8192 tokens)
        if (modelName.Contains("jina-embeddings") || modelName.Contains("jina-embed"))
        {
            return new ModelEmbeddingProfile
            {
                ModelName = config.ModelName ?? "jina-embeddings",
                PhysicalMaxTokens = 8192,
                SweetSpotMinTokens = 128,
                SweetSpotOptimalTokens = 1500,
                SweetSpotMaxTokens = 2048,
                DefaultOverlapRatio = 0.08f,
                SupportsMultiPageContext = true,
                VectorDimension = config.ApiDimension ?? 1024
            };
        }

        // 3. OpenAI text-embedding-3 / ada (8192 tokens)
        if (modelName.Contains("text-embedding-3") || modelName.Contains("text-embedding-ada"))
        {
            int dim = modelName.Contains("large") ? 3072 : 1536;
            return new ModelEmbeddingProfile
            {
                ModelName = config.ModelName ?? "openai-embedding",
                PhysicalMaxTokens = 8192,
                SweetSpotMinTokens = 100,
                SweetSpotOptimalTokens = 1200,
                SweetSpotMaxTokens = 1800,
                DefaultOverlapRatio = 0.08f,
                SupportsMultiPageContext = true,
                VectorDimension = config.ApiDimension ?? dim
            };
        }

        // 4. Nomic Embed (8192 tokens)
        if (modelName.Contains("nomic-embed"))
        {
            return new ModelEmbeddingProfile
            {
                ModelName = config.ModelName ?? "nomic-embed",
                PhysicalMaxTokens = 8192,
                SweetSpotMinTokens = 128,
                SweetSpotOptimalTokens = 1500,
                SweetSpotMaxTokens = 2048,
                DefaultOverlapRatio = 0.08f,
                SupportsMultiPageContext = true,
                VectorDimension = config.ApiDimension ?? 768
            };
        }

        // 5. MiniLM (256 或 512 tokens)
        if (modelName.Contains("minilm"))
        {
            int maxTokens = config.MaxSequenceLength > 0 ? config.MaxSequenceLength : 256;
            return new ModelEmbeddingProfile
            {
                ModelName = config.ModelName ?? "all-MiniLM",
                PhysicalMaxTokens = maxTokens,
                SweetSpotMinTokens = 30,
                SweetSpotOptimalTokens = Math.Max(64, (int)(maxTokens * 0.70)),
                SweetSpotMaxTokens = Math.Max(96, (int)(maxTokens * 0.85)),
                DefaultOverlapRatio = 0.10f,
                SupportsMultiPageContext = false,
                VectorDimension = 384
            };
        }

        // 6. BGE-Large (512 tokens, 1024d)
        if (modelName.Contains("bge-large"))
        {
            return new ModelEmbeddingProfile
            {
                ModelName = config.ModelName ?? "bge-large",
                PhysicalMaxTokens = 512,
                SweetSpotMinTokens = 60,
                SweetSpotOptimalTokens = 380,
                SweetSpotMaxTokens = 440,
                DefaultOverlapRatio = 0.10f,
                SupportsMultiPageContext = false,
                VectorDimension = 1024
            };
        }

        // 7. BGE-Base (512 tokens, 768d)
        if (modelName.Contains("bge-base"))
        {
            return new ModelEmbeddingProfile
            {
                ModelName = config.ModelName ?? "bge-base",
                PhysicalMaxTokens = 512,
                SweetSpotMinTokens = 50,
                SweetSpotOptimalTokens = 360,
                SweetSpotMaxTokens = 420,
                DefaultOverlapRatio = 0.10f,
                SupportsMultiPageContext = false,
                VectorDimension = 768
            };
        }

        // 8. BGE-Small (512 tokens, 512d)
        if (modelName.Contains("bge-small"))
        {
            return CreateStandard512Profile(config.ModelName ?? "bge-small", 512);
        }

        // 9. API 模式长文本通用检测
        if (isApi && (config.ApiMaxInputTokens >= 4096 || config.MaxSequenceLength >= 4096))
        {
            int maxTokens = Math.Max(config.ApiMaxInputTokens ?? 8192, config.MaxSequenceLength);
            return new ModelEmbeddingProfile
            {
                ModelName = config.ModelName ?? "api-long-context",
                PhysicalMaxTokens = maxTokens,
                SweetSpotMinTokens = 128,
                SweetSpotOptimalTokens = 1500,
                SweetSpotMaxTokens = 2048,
                DefaultOverlapRatio = 0.08f,
                SupportsMultiPageContext = true,
                VectorDimension = config.ApiDimension ?? 1024
            };
        }

        // 10. 通用动态回退计算
        int physicalLimit = config.MaxSequenceLength > 0 ? config.MaxSequenceLength : 512;
        int targetTokens = Math.Max(64, (int)(physicalLimit * 0.70));
        int minTokens = Math.Max(30, (int)(physicalLimit * 0.10));
        int maxTokensSafe = Math.Min(physicalLimit - 24, (int)(physicalLimit * 0.85));

        return new ModelEmbeddingProfile
        {
            ModelName = string.IsNullOrWhiteSpace(config.ModelName) ? "custom-embedding" : config.ModelName,
            PhysicalMaxTokens = physicalLimit,
            SweetSpotMinTokens = minTokens,
            SweetSpotOptimalTokens = targetTokens,
            SweetSpotMaxTokens = maxTokensSafe,
            DefaultOverlapRatio = 0.10f,
            SupportsMultiPageContext = physicalLimit >= 4096,
            VectorDimension = config.ApiDimension ?? (physicalLimit >= 1024 ? 1024 : 512)
        };
    }

    private static ModelEmbeddingProfile CreateStandard512Profile(string modelName, int dim)
    {
        return new ModelEmbeddingProfile
        {
            ModelName = modelName,
            PhysicalMaxTokens = 512,
            SweetSpotMinTokens = 40,
            SweetSpotOptimalTokens = 360,
            SweetSpotMaxTokens = 420,
            DefaultOverlapRatio = 0.10f,
            SupportsMultiPageContext = false,
            VectorDimension = dim
        };
    }
}
