using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Indexing.Strategies;

/// <summary>
/// 纯噪声彻底丢弃策略：阻断无效数据进入向量库与 BM25 索引
/// </summary>
public class DiscardNoiseStrategy : IChunkIngestionStrategy
{
    public IngestionActionType ActionType => IngestionActionType.Discard;

    public ChunkStrategyResult Apply(
        ChunkRecord currentChunk,
        int currentIndex,
        IReadOnlyList<ChunkRecord> allOriginalChunks,
        List<ChunkRecord> acceptedChunksSoFar)
    {
        return ChunkStrategyResult.Discarded(
            $"彻底丢弃噪声分块 #{currentIndex} (行 {currentChunk.StartLine}-{currentChunk.EndLine})");
    }
}
