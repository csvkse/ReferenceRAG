using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.Search.Fusion;

/// <summary>
/// 跨知识库多路召回融合策略契约
/// </summary>
public interface ISearchFusionStrategy
{
    ScoreKind ScoreKind { get; }

    /// <summary>
    /// 对来自不同知识库的召回快照进行去重与融合排序
    /// </summary>
    IReadOnlyList<RecallItem> Fuse(IReadOnlyList<RecallSnapshot> snapshots, int topK);
}
