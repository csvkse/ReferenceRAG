using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.Strategies;

/// <summary>
/// 高精度紧凑型自适应分段策略
/// 专为 FAQ、配置字典、API 文档、精准事实问答优化，降低窗口尺寸，注意力高度聚焦
/// </summary>
public class PrecisionDenseAdaptiveStrategy : IAdaptiveChunkingStrategy
{
    public string StrategyType => "PrecisionDense";
    public string DisplayName => "高精度紧凑型";

    public AdaptiveChunkingParameters ResolveParameters(ChunkingOptions options, ModelEmbeddingProfile profile)
    {
        int targetTokens;
        if (profile.SupportsMultiPageContext)
        {
            targetTokens = 500;
        }
        else
        {
            targetTokens = Math.Clamp((int)(profile.PhysicalMaxTokens * 0.45f), 100, 260);
        }

        int maxTokens = Math.Min(profile.PhysicalMaxTokens - 32, targetTokens + 30);
        int minTokens = Math.Max(25, (int)(targetTokens * 0.15f));
        int overlapTokens = Math.Max(10, (int)(targetTokens * 0.06f)); // 低重叠率 6%

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
