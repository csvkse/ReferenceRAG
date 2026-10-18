using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Search.Adapters;

/// <summary>
/// BM25 关键词检索适配器接口
/// </summary>
public interface IBM25RetrievalAdapter
{
    Task<List<BM25SearchResult>> SearchAsync(
        string query,
        int topK,
        float k1 = 1.5f,
        float b = 0.75f,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 密集向量检索适配器接口
/// </summary>
public interface IDenseVectorRetrievalAdapter
{
    ModelExecutionInfo ModelInfo { get; }

    Task<float[]> EncodeQueryAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<List<SearchResult>> SearchAsync(
        float[] queryVector,
        int candidateLimit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 重排精排适配器接口
/// </summary>
public interface IRerankExecutionAdapter
{
    bool IsAvailable { get; }
    ModelExecutionInfo? ModelInfo { get; }

    Task<RerankResult> RerankBatchAsync(
        string query,
        List<string> documents,
        CancellationToken cancellationToken = default);
}
