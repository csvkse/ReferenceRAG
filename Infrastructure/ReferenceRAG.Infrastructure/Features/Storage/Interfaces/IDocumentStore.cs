using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

/// <summary>按分块标识批量读取原文，不依赖向量查询。</summary>
public interface IDocumentStore
{
    Task<IReadOnlyDictionary<string, ChunkRecord>> GetChunksByIdsAsync(
        IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);
}
