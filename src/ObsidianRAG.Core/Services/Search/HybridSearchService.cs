using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 混合搜索服务 - 结合 BM25 关键词搜索和 Embedding 语义搜索
/// 使用 RRF (Reciprocal Rank Fusion) 算法融合结果
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
            }
            else
            {
                // 如果模型存在但被禁用，则启用它
                if (!_bm25Store.IsModelEnabled(DefaultBM25Model))
                {
                    await _bm25Store.EnableModelAsync(DefaultBM25Model);
                    _logger?.LogInformation("启用已存在的 BM25 模型: {ModelName}", DefaultBM25Model);
                }
            }

            // 获取所有文件
            var files = await _vectorStore.GetAllFilesAsync();
            var fileList = files.ToList();

            var documentsToIndex = new List<(string ChunkId, string Content)>();

            foreach (var file in fileList)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 获取该文件的所有 chunks
                var chunks = await _vectorStore.GetChunksByFileAsync(file.Id);

                foreach (var chunk in chunks)
                {
                    if (!string.IsNullOrEmpty(chunk.Content))
                    {
                        documentsToIndex.Add((chunk.Id, chunk.Content));
                    }
                }
            }

            // 批量索引
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "BM25 索引初始化失败");
            throw;
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
    /// </summary>
    public async Task<List<HybridSearchResult>> SearchAsync(
        string query,
        int topK = 10,
        CancellationToken cancellationToken = default)
    {
        var results = new List<HybridSearchResult>();

        // 检查 BM25 模型是否可用
        if (!_bm25Store.ModelExists(DefaultBM25Model) || !_bm25Store.IsModelEnabled(DefaultBM25Model))
        {
            _logger?.LogWarning("BM25 模型不可用，退化为纯向量搜索");
            return await VectorOnlySearchAsync(query, topK, cancellationToken);
        }

        // 1. BM25 关键词搜索
        var bm25Results = await _bm25Store.SearchAsync(DefaultBM25Model, query, topK * 2);
        var bm25RankMap = CreateRankMap(bm25Results.Select(r => r.ChunkId).ToList());

        // 2. Embedding 语义搜索（使用当前模型）
        var queryVector = await _embeddingService.EncodeAsync(query, EmbeddingMode.Query, cancellationToken);
        var modelName = _embeddingService.ModelName;
        var embeddingResults = await _vectorStore.SearchAsync(queryVector, modelName, topK * 2, cancellationToken);
        var embeddingDict = embeddingResults.ToDictionary(r => r.ChunkId, r => r);
        var embeddingRankMap = CreateRankMap(embeddingResults.Select(r => r.ChunkId).ToList());

        // 3. 收集所有候选文档ID
        var allDocIds = bm25RankMap.Keys.Union(embeddingRankMap.Keys).ToHashSet();

        // 4. RRF 融合
        var rrfScores = new Dictionary<string, float>();
        var k = _options.RRFK; // RRF 参数

        foreach (var docId in allDocIds)
        {
            var bm25Rank = bm25RankMap.GetValueOrDefault(docId, int.MaxValue);
            var embeddingRank = embeddingRankMap.GetValueOrDefault(docId, int.MaxValue);

            // 对于只在 BM25 中的文档（embeddingRank = int.MaxValue），使用 BM25 排名作为虚拟 embedding 排名
            // 这意味着：如果 BM25 说某文档是 #1，则在 embedding 排名中也视为 #1
            var effectiveEmbeddingRank = embeddingRank;
            if (embeddingRank == int.MaxValue && bm25Rank < int.MaxValue)
            {
                effectiveEmbeddingRank = bm25Rank;
            }

            // RRF 分数 = 1/(k + rank_bm25) + 1/(k + rank_embedding)
            var rrfScore = 0f;
            if (bm25Rank < int.MaxValue)
            {
                rrfScore += _options.BM25Weight / (k + bm25Rank + 1);
            }
            if (effectiveEmbeddingRank < int.MaxValue)
            {
                rrfScore += _options.EmbeddingWeight / (k + effectiveEmbeddingRank + 1);
            }

            rrfScores[docId] = rrfScore;
        }

        // 5. 排序并取 Top-K
        var topResults = rrfScores
            .OrderByDescending(s => s.Value)
            .Take(topK)
            .ToList();

        // 6. 构建最终结果
        var bm25Dict = bm25Results.ToDictionary(r => r.ChunkId, r => r);

        foreach (var (docId, rrfScore) in topResults)
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
                Score = rrfScore,
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
