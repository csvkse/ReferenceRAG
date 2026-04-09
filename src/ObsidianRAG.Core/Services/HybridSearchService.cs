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
    private readonly BM25Searcher _bm25Searcher;
    private readonly HybridSearchOptions _options;
    private readonly ILogger<HybridSearchService>? _logger;

    public HybridSearchService(
        IVectorStore vectorStore,
        IEmbeddingService embeddingService,
        HybridSearchOptions? options = null,
        ILogger<HybridSearchService>? logger = null)
    {
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _bm25Searcher = new BM25Searcher(options?.BM25Options);
        _options = options ?? new HybridSearchOptions();
        _logger = logger;
    }

    /// <summary>
    /// 索引文档到 BM25 搜索器
    /// </summary>
    public void IndexDocument(string docId, string content)
    {
        _bm25Searcher.IndexDocument(docId, content);
    }

    /// <summary>
    /// 批量索引文档
    /// </summary>
    public void IndexDocuments(IEnumerable<(string DocId, string Content)> documents)
    {
        _bm25Searcher.IndexDocuments(documents);
    }

    /// <summary>
    /// 从索引中移除文档
    /// </summary>
    public void RemoveDocument(string docId)
    {
        _bm25Searcher.RemoveDocument(docId);
    }

    /// <summary>
    /// 清空 BM25 索引
    /// </summary>
    public void ClearIndex()
    {
        _bm25Searcher.Clear();
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

        // 1. BM25 关键词搜索
        var bm25Results = _bm25Searcher.Search(query, topK * 2);
        var bm25RankMap = CreateRankMap(bm25Results.Select(r => r.DocId).ToList());

        // 2. Embedding 语义搜索
        var queryVector = await _embeddingService.EncodeAsync(query, EmbeddingMode.Query, cancellationToken);
        var embeddingResults = await _vectorStore.SearchAsync(queryVector, topK * 2, cancellationToken);
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

            // RRF 分数 = 1/(k + rank_bm25) + 1/(k + rank_embedding)
            var rrfScore = 0f;
            if (bm25Rank < int.MaxValue)
            {
                rrfScore += _options.BM25Weight / (k + bm25Rank + 1);
            }
            if (embeddingRank < int.MaxValue)
            {
                rrfScore += _options.EmbeddingWeight / (k + embeddingRank + 1);
            }

            rrfScores[docId] = rrfScore;
        }

        // 5. 排序并取 Top-K
        var topResults = rrfScores
            .OrderByDescending(s => s.Value)
            .Take(topK)
            .ToList();

        // 6. 构建最终结果
        var bm25Dict = bm25Results.ToDictionary(r => r.DocId, r => r);
        var embeddingDict = embeddingResults.ToDictionary(r => r.ChunkId, r => r);

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
                BM25Score = bm25Result?.Score ?? 0,
                EmbeddingScore = embeddingResult?.Score ?? 0,
                BM25Rank = bm25RankMap.GetValueOrDefault(docId, -1),
                EmbeddingRank = embeddingRankMap.GetValueOrDefault(docId, -1),
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
    /// BM25 权重
    /// </summary>
    public float BM25Weight { get; set; } = 0.4f;

    /// <summary>
    /// Embedding 权重
    /// </summary>
    public float EmbeddingWeight { get; set; } = 0.6f;

    /// <summary>
    /// BM25 配置
    /// </summary>
    public BM25Options BM25Options { get; set; } = new();
}
