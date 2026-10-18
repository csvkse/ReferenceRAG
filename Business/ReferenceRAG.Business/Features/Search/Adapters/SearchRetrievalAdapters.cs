using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Business.Features.Search.Adapters;

/// <summary>
/// Lucene BM25 关键词检索适配器实现
/// </summary>
public class BM25RetrievalAdapter : IBM25RetrievalAdapter
{
    private readonly IBM25Store _bm25Store;
    private readonly SynonymService? _synonymService;

    public BM25RetrievalAdapter(IBM25Store bm25Store, SynonymService? synonymService = null)
    {
        _bm25Store = bm25Store;
        _synonymService = synonymService;
    }

    public async Task<List<BM25SearchResult>> SearchAsync(
        string query,
        int topK,
        float k1 = 1.5f,
        float b = 0.75f,
        CancellationToken cancellationToken = default)
    {
        var expandedQuery = _synonymService?.ExpandQuery(query) ?? query;
        return await _bm25Store.SearchAsync(expandedQuery, topK, k1, b);
    }
}

/// <summary>
/// 密集向量检索适配器实现
/// </summary>
public class DenseVectorRetrievalAdapter : IDenseVectorRetrievalAdapter
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;
    private readonly IModelTelemetryService? _telemetryService;

    public DenseVectorRetrievalAdapter(
        IEmbeddingService embeddingService,
        IVectorStore vectorStore,
        IModelTelemetryService? telemetryService = null)
    {
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
        _telemetryService = telemetryService;
    }

    public ModelExecutionInfo ModelInfo
    {
        get
        {
            if (_telemetryService != null)
            {
                var tel = _telemetryService.GetCurrentEmbeddingTelemetry();
                return new ModelExecutionInfo
                {
                    ModelName = !string.IsNullOrEmpty(tel.Configuration.ModelName) ? tel.Configuration.ModelName : _embeddingService.ModelName,
                    Variant = !string.IsNullOrEmpty(tel.Configuration.Quantization) ? tel.Configuration.Quantization : null,
                    Format = tel.Configuration.ModelFormat,
                    Device = tel.Hardware.ExecutionDevice,
                    IsGpu = tel.Hardware.IsGpuMode,
                    State = tel.StateMachine.StateName
                };
            }

            return new ModelExecutionInfo
            {
                ModelName = _embeddingService.ModelName,
                Format = "onnx",
                Device = "CPU",
                IsGpu = false,
                State = "Ready"
            };
        }
    }

    public async Task<float[]> EncodeQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        return await _embeddingService.EncodeAsync(query, EmbeddingMode.Query, cancellationToken);
    }

    public async Task<List<SearchResult>> SearchAsync(
        float[] queryVector,
        int candidateLimit,
        CancellationToken cancellationToken = default)
    {
        var results = await _vectorStore.SearchAsync(queryVector, _embeddingService.ModelName, candidateLimit, cancellationToken);
        return results.ToList();
    }
}

/// <summary>
/// 重排 Cross-Encoder 适配器实现
/// </summary>
public class RerankExecutionAdapter : IRerankExecutionAdapter
{
    private readonly IRerankService? _rerankService;
    private readonly IModelTelemetryService? _telemetryService;

    public RerankExecutionAdapter(
        IRerankService? rerankService = null,
        IModelTelemetryService? telemetryService = null)
    {
        _rerankService = rerankService;
        _telemetryService = telemetryService;
    }

    public bool IsAvailable => _rerankService != null;

    public ModelExecutionInfo? ModelInfo
    {
        get
        {
            if (_rerankService == null) return null;

            if (_telemetryService != null)
            {
                var tel = _telemetryService.GetCurrentRerankTelemetry();
                return new ModelExecutionInfo
                {
                    ModelName = !string.IsNullOrEmpty(tel.Configuration.ModelName) ? tel.Configuration.ModelName : _rerankService.ModelName,
                    Variant = !string.IsNullOrEmpty(tel.Configuration.Quantization) ? tel.Configuration.Quantization : null,
                    Format = tel.Configuration.ModelFormat,
                    Device = tel.Hardware.ExecutionDevice,
                    IsGpu = tel.Hardware.IsGpuMode,
                    State = tel.StateMachine.StateName
                };
            }

            return new ModelExecutionInfo
            {
                ModelName = _rerankService.ModelName,
                Format = "onnx",
                Device = "CPU",
                IsGpu = false,
                State = _rerankService.IsLoaded ? "Ready" : "Idle"
            };
        }
    }

    public async Task<RerankResult> RerankBatchAsync(
        string query,
        List<string> documents,
        CancellationToken cancellationToken = default)
    {
        if (_rerankService == null)
        {
            return new RerankResult
            {
                Query = query,
                Documents = documents.Select((d, i) => new RerankDocument { Index = i, RelevanceScore = 0.5 }).ToList()
            };
        }

        return await _rerankService.RerankBatchAsync(query, documents, cancellationToken);
    }
}
