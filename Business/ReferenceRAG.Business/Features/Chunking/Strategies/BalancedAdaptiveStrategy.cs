using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.Strategies;

/// <summary>
/// 通用平衡型自适应分段策略
/// 严格锚定模型注意力黄金区间，兼顾实体精确定位与上下文完整性，兼顾存储空间优化
/// </summary>
public class BalancedAdaptiveStrategy : IAdaptiveChunkingStrategy
{
    public string StrategyType => "Balanced";
    public string DisplayName => "通用平衡型 (默认推荐)";

    public AdaptiveChunkingParameters ResolveParameters(ChunkingOptions options, ModelEmbeddingProfile profile)
    {
        float utilRatio = (options.TargetWindowUtilizationPercent > 0 ? options.TargetWindowUtilizationPercent : 70) / 100.0f;
        float overlapRatio = (options.AdaptiveOverlapPercent >= 0 ? options.AdaptiveOverlapPercent : 10) / 100.0f;

        int targetTokens;
        if (profile.SupportsMultiPageContext)
        {
            // 8K 长文本大模型：动态扩充至 1200 ~ 1800 tokens，大幅减少切片数 70%+
            targetTokens = Math.Clamp((int)(profile.PhysicalMaxTokens * 0.20f * (utilRatio / 0.70f)), 800, 2048);
        }
        else
        {
            // 512 物理模型：黄金区间 320 ~ 380 tokens
            targetTokens = Math.Clamp((int)(profile.PhysicalMaxTokens * utilRatio), 120, profile.PhysicalMaxTokens - 32);
        }

        int maxTokens = Math.Min(profile.PhysicalMaxTokens - 16, targetTokens + 40);
        int minTokens = Math.Max(30, (int)(targetTokens * 0.15f));
        int overlapTokens = Math.Max(10, (int)(targetTokens * overlapRatio));

        return new AdaptiveChunkingParameters
        {
            StrategyName = DisplayName,
            TargetTokens = targetTokens,
            MaxTokens = maxTokens,
            MinTokens = minTokens,
            OverlapTokens = overlapTokens,
            PreserveHeadings = options.PreserveHeadings,
            PreserveCodeBlocks = options.PreserveCodeBlocks,
            PreserveTables = options.PreserveTables,
            CodeOverlapRatio = 0.0f
        };
    }
}
