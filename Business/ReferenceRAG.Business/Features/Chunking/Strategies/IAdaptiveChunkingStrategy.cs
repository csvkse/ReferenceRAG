using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.Strategies;

/// <summary>
/// 自适应分段策略接口
/// </summary>
public interface IAdaptiveChunkingStrategy
{
    /// <summary>策略唯一标识（如 Balanced, PrecisionDense, DeepNarrative, CodeFirst）</summary>
    string StrategyType { get; }

    /// <summary>策略显示名称</summary>
    string DisplayName { get; }

    /// <summary>
    /// 根据当前分段选项与嵌入模型画像计算有效切分参数
    /// </summary>
    AdaptiveChunkingParameters ResolveParameters(ChunkingOptions options, ModelEmbeddingProfile profile);
}
