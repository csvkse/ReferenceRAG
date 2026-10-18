using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Strategies;

/// <summary>
/// 连续结构块切分策略统一契约
/// </summary>
public interface IContinuousChunkingStrategy
{
    /// <summary>
    /// 支持的分块类型
    /// </summary>
    ChunkType SupportedType { get; }

    /// <summary>
    /// 对超限连续结构块执行切分与上下文保护
    /// </summary>
    List<ChunkRecord> Chunk(
        object element,
        string[] allLines,
        ChunkingOptions options,
        ITokenizer? tokenizer,
        string fileId,
        string? headingPath,
        int headingLevel,
        ref int chunkIndexCounter);
}
