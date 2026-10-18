using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.Strategies;

/// <summary>
/// 深度长文叙事型自适应分段策略
/// 专为小说、长篇论文、会议纪要优化，最大化释放模型长上下文能力，分块数减少 60%~75%，空间大幅瘦身
/// </summary>
public class DeepNarrativeAdaptiveStrategy : IAdaptiveChunkingStrategy
{
    public string StrategyType => "DeepNarrative";
    public string DisplayName => "深度长文叙事型";

    public AdaptiveChunkingParameters ResolveParameters(ChunkingOptions options, ModelEmbeddingProfile profile)
    {
        int targetTokens;
        if (profile.SupportsMultiPageContext)
        {
            // 长文本大模型：目标提升至 2500~3500 tokens，巨幅精简向量库
            targetTokens = Math.Clamp((int)(profile.PhysicalMaxTokens * 0.40f), 1500, 3500);
        }
        else
        {
            // 512 模型：最大化利用上限 (~420 tokens)
            targetTokens = Math.Clamp((int)(profile.PhysicalMaxTokens * 0.82f), 300, 430);
        }

        int maxTokens = Math.Min(profile.PhysicalMaxTokens - 16, targetTokens + 32);
        int minTokens = Math.Max(50, (int)(targetTokens * 0.15f));
        int overlapTokens = Math.Max(20, (int)(targetTokens * 0.15f)); // 15% 充分重叠保障小说叙事不脱节

        return new AdaptiveChunkingParameters
        {
            StrategyName = DisplayName,
            TargetTokens = targetTokens,
            MaxTokens = maxTokens,
            MinTokens = minTokens,
            OverlapTokens = overlapTokens,
            PreserveHeadings = true,
            PreserveCodeBlocks = true,
            PreserveTables = true,
            CodeOverlapRatio = 0.0f
        };
    }
}
