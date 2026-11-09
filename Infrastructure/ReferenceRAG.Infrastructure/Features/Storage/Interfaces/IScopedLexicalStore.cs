namespace ReferenceRAG.Core.Interfaces;

/// <summary>在调用方提供的可见分块范围内执行词法召回。</summary>
public interface IScopedLexicalStore
{
    Task<List<BM25SearchResult>> SearchScopedAsync(
        string query, IReadOnlyCollection<string> allowedChunkIds, int topK,
        CancellationToken cancellationToken = default);
}
