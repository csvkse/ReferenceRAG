using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Indexing.Strategies;

/// <summary>
/// 相邻分块吸收策略：将无独立语义的孤立碎片吸收至紧邻分块，扩大行号跨度并保持上下文连续，阻断独立脏向量生成
/// </summary>
public class AbsorbToAdjacentStrategy : IChunkIngestionStrategy
{
    public IngestionActionType ActionType => IngestionActionType.AbsorbToAdjacent;

    public ChunkStrategyResult Apply(
        ChunkRecord currentChunk,
        int currentIndex,
        IReadOnlyList<ChunkRecord> allOriginalChunks,
        List<ChunkRecord> acceptedChunksSoFar)
    {
        // 优先向前合并（Forward Absorption）
        if (acceptedChunksSoFar.Count > 0)
        {
            var prev = acceptedChunksSoFar[^1];
            prev.Content = string.IsNullOrEmpty(prev.Content)
                ? currentChunk.Content
                : prev.Content + "\n\n" + currentChunk.Content;

            prev.EndLine = Math.Max(prev.EndLine, currentChunk.EndLine);
            if (currentChunk.EndColumn > 0)
            {
                prev.EndColumn = currentChunk.EndColumn;
            }

            return ChunkStrategyResult.Absorbed(prev, $"向前吸收至已准入分块 #{prev.ChunkIndex} (扩展行号至 {prev.EndLine})");
        }

        // 次优向后合并（Backward Absorption：针对文档开头碎片）
        if (currentIndex + 1 < allOriginalChunks.Count)
        {
            var next = allOriginalChunks[currentIndex + 1];
            next.Content = string.IsNullOrEmpty(next.Content)
                ? currentChunk.Content
                : currentChunk.Content + "\n\n" + next.Content;

            next.StartLine = Math.Min(currentChunk.StartLine, next.StartLine);

            return ChunkStrategyResult.Absorbed(next, $"向后吸收至候选分块 #{currentIndex + 1} (扩展起始行至 {next.StartLine})");
        }

        // 孤立无邻居分块，安全丢弃
        return ChunkStrategyResult.Discarded("无可用相邻分块，自动转换为安全丢弃");
    }
}
