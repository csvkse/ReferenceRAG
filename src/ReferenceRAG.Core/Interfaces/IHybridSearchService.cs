using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Interfaces;

public interface IHybridSearchService
{
    Task IndexDocumentAsync(
        string chunkId,
        string content,
        CancellationToken cancellationToken = default);

    Task IndexDocumentsAsync(
        IEnumerable<(string ChunkId, string Content)> documents,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    Task<List<HybridSearchResult>> SearchAsync(
        string query,
        int topK = 10,
        float k1 = 1.5f,
        float b = 0.75f,
        IEnumerable<string>? folders = null,
        CancellationToken cancellationToken = default,
        float[]? precomputedQueryVector = null);
}
