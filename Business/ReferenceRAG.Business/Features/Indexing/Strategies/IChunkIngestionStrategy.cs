using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Indexing.Strategies;

/// <summary>
/// 处置策略执行结果
/// </summary>
public sealed record ChunkStrategyResult
{
    public bool ChunkRetained { get; init; }
    public ChunkRecord? MutatedAdjacentChunk { get; init; }
    public string OperationSummary { get; init; } = string.Empty;

    public static ChunkStrategyResult Retained(string summary = "保留合格分块") =>
        new() { ChunkRetained = true, OperationSummary = summary };

    public static ChunkStrategyResult Absorbed(ChunkRecord mutatedChunk, string summary) =>
        new() { ChunkRetained = false, MutatedAdjacentChunk = mutatedChunk, OperationSummary = summary };

    public static ChunkStrategyResult Discarded(string summary) =>
        new() { ChunkRetained = false, OperationSummary = summary };
}

/// <summary>
/// 分块入库处置策略接口
/// </summary>
public interface IChunkIngestionStrategy
{
    IngestionActionType ActionType { get; }
    ChunkStrategyResult Apply(
        ChunkRecord currentChunk,
        int currentIndex,
        IReadOnlyList<ChunkRecord> allOriginalChunks,
        List<ChunkRecord> acceptedChunksSoFar);
}
