using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Indexing.Strategies;

/// <summary>
/// 自适应处置策略：结合上下文环境在吸收与丢弃间动态决策
/// </summary>
public class AdaptiveIngestionStrategy : IChunkIngestionStrategy
{
    private readonly AbsorbToAdjacentStrategy _absorbStrategy = new();
    private readonly DiscardNoiseStrategy _discardStrategy = new();

    public IngestionActionType ActionType => IngestionActionType.Accept; // 自适应调度

    public ChunkStrategyResult Apply(
        ChunkRecord currentChunk,
        int currentIndex,
        IReadOnlyList<ChunkRecord> allOriginalChunks,
        List<ChunkRecord> acceptedChunksSoFar)
    {
        // 若有相邻已准入分块或紧随分块，优先吸收以保全行号
        if (acceptedChunksSoFar.Count > 0 || currentIndex + 1 < allOriginalChunks.Count)
        {
            return _absorbStrategy.Apply(currentChunk, currentIndex, allOriginalChunks, acceptedChunksSoFar);
        }

        return _discardStrategy.Apply(currentChunk, currentIndex, allOriginalChunks, acceptedChunksSoFar);
    }
}
