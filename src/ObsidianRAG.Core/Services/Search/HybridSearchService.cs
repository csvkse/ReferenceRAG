using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 混合搜索服务 - 结合 BM25 关键词搜索和 Embedding 语义搜索
/// 使用分数级加权融合 (Score-level Weighted Fusion) 算法
/// </summary>
public class HybridSearchService
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly IBM25Store _bm25Store;
    private readonly HybridSearchOptions _options;
    private readonly ILogger<HybridSearchService>? _logger;

    // 默认 BM25 模型名称
    private const string DefaultBM25Model = "default";

    public HybridSearchService(
        IVectorStore vectorStore,
        IEmbeddingService embeddingService,
        IBM25Store bm25Store,
        HybridSearchOptions? options = null,
        ILogger<HybridSearchService>? logger = null)
    {
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _bm25Store = bm25Store;
        _options = options ?? new HybridSearchOptions();
        _logger = logger;
    }

    /// <summary>
    /// 异步初始化 BM25 索引 - 确保默认模型存在并加载所有 chunks
    /// 如果索引已存在则跳过索引步骤，实现快速启动
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("开始初始化 BM25 索引...");

        try
        {
            // 确保默认 BM25 模型存在
            if (!_bm25Store.ModelExists(DefaultBM25Model))
            {
                await _bm25Store.CreateModelAsync(DefaultBM25Model, _options.BM25Options.K1, _options.BM25Options.B);
                _logger?.LogInformation("创建默认 BM25 模型: {ModelName}", DefaultBM25Model);

                // 新创建的模型需要索引所有文档
                await IndexAllDocumentsAsync(cancellationToken);
            }
            else
            {
                // 如果模型存在但被禁用，则启用它
                if (!_bm25Store.IsModelEnabled(DefaultBM25Model))
                {
                    await _bm25Store.EnableModelAsync(DefaultBM25Model);
                    _logger?.LogInformation("启用已存在的 BM25 模型: {ModelName}", DefaultBM25Model);
                }

                // 检查索引是否已存在（TotalDocuments > 0 表示已有索引）
                var modelInfo = await _bm25Store.GetModelInfoAsync(DefaultBM25Model);
                if (modelInfo != null && modelInfo.TotalDocuments > 0)
                {
                    _logger?.LogInformation("BM25 索引已存在 ({TotalDocs} 个文档)，跳过索引步骤", modelInfo.TotalDocuments);
                }
                else
                {
                    // 索引为空，跳过自动索引（由用户手动触发索引）
                    _logger?.LogInformation("BM25 索引为空，请手动触发索引任务");
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "BM25 索引初始化失败");
            throw;
        }
    }

    /// <summary>
    /// 索引所有文档到 BM25
    /// </summary>
    private async Task IndexAllDocumentsAsync(CancellationToken cancellationToken)
    {
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        var documentsToIndex = new List<(string ChunkId, string Content)>();

        foreach (var file in fileList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunks = await _vectorStore.GetChunksByFileAsync(file.Id);

            foreach (var chunk in chunks)
            {
                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    documentsToIndex.Add((chunk.Id, chunk.Content));
                }
            }
        }

        if (documentsToIndex.Count > 0)
        {
            await _bm25Store.IndexBatchAsync(DefaultBM25Model, documentsToIndex);
            _logger?.LogInformation("BM25 索引初始化完成，共索引 {Count} 个文档", documentsToIndex.Count);
        }
        else
        {
            _logger?.LogWarning("BM25 索引初始化完成，但没有找到任何文档");
        }
    }

    /// <summary>
    /// 索引文档到 BM25 存储
    /// </summary>
    public async Task IndexDocumentAsync(string chunkId, string content, CancellationToken cancellationToken = default)
    {
        if (!_bm25Store.ModelExists(DefaultBM25Model))
        {
            await _bm25Store.CreateModelAsync(DefaultBM25Model, _options.BM25Options.K1, _options.BM25Options.B);
        }
        await _bm25Store.IndexDocumentAsync(DefaultBM25Model, chunkId, content);
    }

    /// <summary>
    /// 批量索引文档
    /// </summary>
    public async Task IndexDocumentsAsync(IEnumerable<(string ChunkId, string Content)> documents, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!_bm25Store.ModelExists(DefaultBM25Model))
        {
            await _bm25Store.CreateModelAsync(DefaultBM25Model, _options.BM25Options.K1, _options.BM25Options.B);
        }
        await _bm25Store.IndexBatchAsync(DefaultBM25Model, documents, progress);
    }

    /// <summary>
    /// 混合搜索 - 结合 BM25 和 Embedding 结果
    /// 使用分数级加权融合 (Score-level Weighted Fusion)，而非 RRF 排名融合
    /// </summary>
    public async Task<List<HybridSearchResult>> SearchAsync(
        string query,
        int topK = 10,
        float k1 = 1.5f,
        float b = 0.75f,
        CancellationToken cancellationToken = default)
    {
        var results = new List<HybridSearchResult>();

        // 检查 BM25 模型是否可用
        if (!_bm25Store.ModelExists(DefaultBM25Model) || !_bm25Store.IsModelEnabled(DefaultBM25Model))
        {
            _logger?.LogWarning("BM25 模型不可用，退化为纯向量搜索");
            return await VectorOnlySearchAsync(query, topK, cancellationToken);
        }

        // 1. BM25 关键词搜索（传入 k1, b 参数）
        var bm25Results = await _bm25Store.SearchAsync(DefaultBM25Model, query, topK * 2, k1, b);
        var bm25RankMap = CreateRankMap(bm25Results.Select(r => r.ChunkId).ToList());
        var bm25Dict = bm25Results.ToDictionary(r => r.ChunkId, r => r);

        // 获取 BM25 最大分数用于归一化
        var maxBm25Score = bm25Results.Count > 0 ? bm25Results.Max(r => r.Score) : 1.0;
        if (maxBm25Score <= 0) maxBm25Score = 1.0;

        // 2. Embedding 语义搜索（使用当前模型）
        var queryVector = await _embeddingService.EncodeAsync(query, EmbeddingMode.Query, cancellationToken);
        var modelName = _embeddingService.ModelName;
        var embeddingResults = await _vectorStore.SearchAsync(queryVector, modelName, topK * 2, cancellationToken);
        var embeddingDict = embeddingResults.ToDictionary(r => r.ChunkId, r => r);
        var embeddingRankMap = CreateRankMap(embeddingResults.Select(r => r.ChunkId).ToList());

        // 获取 Embedding 最大分数用于归一化
        var maxEmbeddingScore = embeddingResults.Any() ? embeddingResults.Max(r => r.Score) : 1.0;
        if (maxEmbeddingScore <= 0) maxEmbeddingScore = 1.0;

        // 3. 收集所有候选文档ID
        var allDocIds = bm25RankMap.Keys.Union(embeddingRankMap.Keys).ToHashSet();

        // 4. 分数级加权融合
        // 公式: finalScore = w1 * norm(BM25) + w2 * norm(Embedding)
        // 其中 norm(x) = x / maxX，将分数归一化到 [0, 1] 范围
        var fusedScores = new Dictionary<string, float>();

        foreach (var docId in allDocIds)
        {
            var bm25Score = bm25Dict.TryGetValue(docId, out var bm25Result) ? bm25Result.Score : 0;
            var embeddingScore = embeddingDict.TryGetValue(docId, out var embeddingResult) ? embeddingResult.Score : 0;

            // 归一化分数
            var normalizedBm25 = (float)(bm25Score / maxBm25Score);
            var normalizedEmbedding = (float)(embeddingScore / maxEmbeddingScore);

            // 加权融合
            var fusedScore = _options.BM25Weight * normalizedBm25 + _options.EmbeddingWeight * normalizedEmbedding;

            fusedScores[docId] = fusedScore;
        }

        // 5. 排序并取 Top-K
        var topResults = fusedScores
            .OrderByDescending(s => s.Value)
            .Take(topK)
            .ToList();

        // 6. 构建最终结果
        foreach (var (docId, fusedScore) in topResults)
        {
            bm25Dict.TryGetValue(docId, out var bm25Result);
            embeddingDict.TryGetValue(docId, out var embeddingResult);

            results.Add(new HybridSearchResult
            {
                ChunkId = docId,
                FileId = embeddingResult?.FileId ?? string.Empty,
                FilePath = embeddingResult?.FilePath ?? string.Empty,
                Title = embeddingResult?.Title ?? string.Empty,
                Content = embeddingResult?.Content ?? bm25Result?.Content ?? string.Empty,
                Score = fusedScore,
                BM25Score = (float)(bm25Result?.Score ?? 0),
                EmbeddingScore = (float)(embeddingResult?.Score ?? 0),
                BM25Rank = bm25RankMap.GetValueOrDefault(docId, -1),
                EmbeddingRank = embeddingRankMap.GetValueOrDefault(docId, -1),
                Source = embeddingResult?.Source ?? string.Empty,
                StartLine = embeddingResult?.StartLine ?? 0,
                EndLine = embeddingResult?.EndLine ?? 0,
                HeadingPath = embeddingResult?.HeadingPath
            });
        }

        _logger?.LogDebug(
            "Hybrid search: query='{Query}', bm25={BM25Count}, embedding={EmbeddingCount}, merged={MergedCount}",
            query, bm25Results.Count, embeddingResults.Count(), results.Count);

        return results;
    }

    /// <summary>
    /// 纯向量搜索（当 BM25 不可用时的降级方案）
    /// </summary>
    private async Task<List<HybridSearchResult>> VectorOnlySearchAsync(
        string query,
        int topK,
        CancellationToken cancellationToken)
    {
        var results = new List<HybridSearchResult>();

        var queryVector = await _embeddingService.EncodeAsync(query, EmbeddingMode.Query, cancellationToken);
        var modelName = _embeddingService.ModelName;
        var embeddingResults = await _vectorStore.SearchAsync(queryVector, modelName, topK, cancellationToken);

        foreach (var result in embeddingResults)
        {
            results.Add(new HybridSearchResult
            {
                ChunkId = result.ChunkId,
                FileId = result.FileId,
                FilePath = result.FilePath,
                Title = result.Title ?? string.Empty,
                Content = result.Content,
                Score = result.Score,
                BM25Score = 0,
                EmbeddingScore = result.Score,
                BM25Rank = -1,
                EmbeddingRank = results.Count,
                Source = result.Source ?? string.Empty,
                StartLine = result.StartLine,
                EndLine = result.EndLine,
                HeadingPath = result.HeadingPath
            });
        }

        _logger?.LogDebug(
            "Vector-only search (BM25 unavailable): query='{Query}', results={Count}",
            query, results.Count);

        return results;
    }

    /// <summary>
    /// 创建文档ID到排名的映射
    /// </summary>
    private static Dictionary<string, int> CreateRankMap(List<string> orderedDocIds)
    {
        var map = new Dictionary<string, int>();
        for (int i = 0; i < orderedDocIds.Count; i++)
        {
            map[orderedDocIds[i]] = i;
        }
        return map;
    }
}

/// <summary>
/// 混合搜索结果
/// </summary>
public class HybridSearchResult
{
    public string ChunkId { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// RRF 融合分数
    /// </summary>
    public float Score { get; set; }

    /// <summary>
    /// BM25 原始分数
    /// </summary>
    public float BM25Score { get; set; }

    /// <summary>
    /// Embedding 原始分数
    /// </summary>
    public float EmbeddingScore { get; set; }

    /// <summary>
    /// BM25 排名 (-1 表示未出现在结果中)
    /// </summary>
    public int BM25Rank { get; set; }

    /// <summary>
    /// Embedding 排名 (-1 表示未出现在结果中)
    /// </summary>
    public int EmbeddingRank { get; set; }

    /// <summary>
    /// 源名称
    /// </summary>
    public string Source { get; set; } = string.Empty;

    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? HeadingPath { get; set; }
}

/// <summary>
/// 混合搜索配置
/// </summary>
public class HybridSearchOptions
{
    /// <summary>
    /// RRF 参数 K (通常 50-100)
    /// </summary>
    public int RRFK { get; set; } = 60;

    /// <summary>
    /// BM25 权重（关键词精确匹配权重）
    /// </summary>
    public float BM25Weight { get; set; } = 0.6f;

    /// <summary>
    /// Embedding 权重（语义相似度权重）
    /// </summary>
    public float EmbeddingWeight { get; set; } = 0.4f;

    /// <summary>
    /// BM25 配置
    /// </summary>
    public BM25Options BM25Options { get; set; } = new();
}
