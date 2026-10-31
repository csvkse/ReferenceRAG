using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Services;

/// <summary>
/// 搜索与向量生成双通道解耦嵌入服务实现 (Dual-Channel Decoupled Embedding Service)
/// 支持：
/// 1. 向量生成 (Document Indexing / Ingestion)：路由至高并发、高吞吐的线上云端 API (如 SiliconFlow / OpenAI) 或高精度量化版；
/// 2. 搜索查询 (Query Search / Retrieval)：路由至本地低延迟、零费用、隐私离线的本地轻量引擎 (如 ONNX / GGUF INT8)；
/// 3. 故障容灾熔断降级：当线上入库端点不可用或网络异常时，平滑降级回退至本地服务。
/// </summary>
public sealed class DualChannelEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly IEmbeddingService _searchService;
    private readonly IEmbeddingService _ingestionService;
    private readonly ILogger<DualChannelEmbeddingService>? _logger;
    private readonly bool _enableFallback;

    public DualChannelEmbeddingService(
        IEmbeddingService searchService,
        IEmbeddingService ingestionService,
        bool enableFallback = true,
        ILogger<DualChannelEmbeddingService>? logger = null)
    {
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _ingestionService = ingestionService ?? throw new ArgumentNullException(nameof(ingestionService));
        _enableFallback = enableFallback;
        _logger = logger;

        // 校验向量维度一致性
        if (_searchService.Dimension > 0 && _ingestionService.Dimension > 0 &&
            _searchService.Dimension != _ingestionService.Dimension)
        {
            var msg = $"[DualChannel] 搜索通道模型维度 ({_searchService.Dimension}) 与生成通道模型维度 ({_ingestionService.Dimension}) 不一致，无法在同一向量空间中检索！";
            _logger?.LogError(msg);
            throw new InvalidOperationException(msg);
        }
    }

    public string ModelName => _searchService.ModelName;

    public int Dimension => _searchService.Dimension > 0 ? _searchService.Dimension : _ingestionService.Dimension;

    public bool IsSimulationMode => _searchService.IsSimulationMode && _ingestionService.IsSimulationMode;

    public bool SupportsAsymmetricEncoding => _searchService.SupportsAsymmetricEncoding || _ingestionService.SupportsAsymmetricEncoding;

    public Task<bool> ReloadModelAsync(string modelPath, string modelName, int? maxSequenceLength = null)
    {
        return _searchService.ReloadModelAsync(modelPath, modelName, maxSequenceLength);
    }

    public void UnloadModel()
    {
        _searchService.UnloadModel();
        _ingestionService.UnloadModel();
    }

    public Task<float[]> EncodeAsync(string text, CancellationToken cancellationToken = default)
    {
        return EncodeAsync(text, EmbeddingMode.Query, cancellationToken);
    }

    public async Task<float[]> EncodeAsync(string text, EmbeddingMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == EmbeddingMode.Document)
        {
            try
            {
                return await _ingestionService.EncodeAsync(text, mode, cancellationToken);
            }
            catch (Exception ex) when (_enableFallback)
            {
                _logger?.LogWarning(ex, "[DualChannel] 线上向量生成失败，触发容灾降级回退至本地引擎: {Message}", ex.Message);
                return await _searchService.EncodeAsync(text, mode, cancellationToken);
            }
        }

        return await _searchService.EncodeAsync(text, mode, cancellationToken);
    }

    public Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        return EncodeBatchAsync(texts, EmbeddingMode.Document, cancellationToken);
    }

    public async Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, EmbeddingMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == EmbeddingMode.Document)
        {
            try
            {
                return await _ingestionService.EncodeBatchAsync(texts, mode, cancellationToken);
            }
            catch (Exception ex) when (_enableFallback)
            {
                _logger?.LogWarning(ex, "[DualChannel] 线上批量向量生成失败，触发容灾降级回退至本地引擎: {Message}", ex.Message);
                return await _searchService.EncodeBatchAsync(texts, mode, cancellationToken);
            }
        }

        return await _searchService.EncodeBatchAsync(texts, mode, cancellationToken);
    }

    public float[] Normalize(float[] vector)
    {
        return _searchService.Normalize(vector);
    }

    public float Similarity(float[] a, float[] b)
    {
        return _searchService.Similarity(a, b);
    }

    public void Dispose()
    {
        if (_searchService is IDisposable d1) d1.Dispose();
        if (_ingestionService is IDisposable d2) d2.Dispose();
    }
}
