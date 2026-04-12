using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// Markdown 分段器接口
/// </summary>
public interface IMarkdownChunker
{
    /// <summary>
    /// 分段 Markdown 内容
    /// </summary>
    List<ChunkRecord> Chunk(string content, ChunkingOptions? options = null);
}
