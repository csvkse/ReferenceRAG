using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.Strategies;

/// <summary>
/// 代码与结构优先型自适应分段策略
/// 专为源码工程、技术设计规范、开发文档优化，语法树优先级绝对高于字符长度限制，代码块 0% 重叠
/// </summary>
public class CodeStructureFirstAdaptiveStrategy : IAdaptiveChunkingStrategy
{
    public string StrategyType => "CodeFirst";
    public string DisplayName => "代码与结构优先型";

    public AdaptiveChunkingParameters ResolveParameters(ChunkingOptions options, ModelEmbeddingProfile profile)
    {
        int targetTokens;
        if (profile.SupportsMultiPageContext)
        {
            targetTokens = 1200;
        }
        else
        {
            targetTokens = Math.Clamp((int)(profile.PhysicalMaxTokens * 0.72f), 280, 380);
        }

        int maxTokens = Math.Min(profile.PhysicalMaxTokens - 24, targetTokens + 48);
        int minTokens = Math.Max(20, (int)(targetTokens * 0.10f));
        int overlapTokens = 0; // 默认零重叠，避免代码语法在切口处破坏

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
