using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace ReferenceRAG.Core.Services;

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
    private readonly SynonymService? _synonymService;
    private readonly ILogger<HybridSearchService>? _logger;

    public HybridSearchService(
        IVectorStore vectorStore,
        IEmbeddingService embeddingService,
        IBM25Store bm25Store,
        HybridSearchOptions? options = null,
        ILogger<HybridSearchService>? logger = null,
        SynonymService? synonymService = null)
    {
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _bm25Store = bm25Store;
        _options = options ?? new HybridSearchOptions();
        _synonymService = synonymService;
        _logger = logger;
    }

    /// <summary>
    /// 索引文档到 BM25 存储
    /// </summary>
    public async Task IndexDocumentAsync(string chunkId, string content, CancellationToken cancellationToken = default)
    {
        await _bm25Store.IndexDocumentAsync(chunkId, content);
    }

    /// <summary>
    /// 批量索引文档
    /// </summary>
    public async Task IndexDocumentsAsync(IEnumerable<(string ChunkId, string Content)> documents, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        await _bm25Store.IndexBatchAsync(documents, progress);
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
        IEnumerable<string>? folders = null,
        CancellationToken cancellationToken = default,
        float[]? precomputedQueryVector = null)
    {
        var results = new List<HybridSearchResult>();

        // 1. BM25 关键词搜索（先做同义词扩展，提升关键词 miss 召回率）
        var bm25Query = _synonymService?.ExpandQuery(query) ?? query;
        var bm25Results = await _bm25Store.SearchAsync(bm25Query, topK * 2, k1, b);
        var bm25RankMap = CreateRankMap(bm25Results.Select(r => r.ChunkId).ToList());
        // 使用 GroupBy 处理可能的重复 ChunkId，取第一个结果
        var bm25Dict = bm25Results
            .GroupBy(r => r.ChunkId)
            .ToDictionary(g => g.Key, g => g.First());

        // 获取 BM25 最大分数用于归一化
        var maxBm25Score = bm25Results.Count > 0 ? bm25Results.Max(r => r.Score) : 1.0;
        if (maxBm25Score <= 0) maxBm25Score = 1.0;

        // 2. Embedding 语义搜索（复用调用方预计算的向量，避免重复推理）
        var queryVector = precomputedQueryVector
            ?? await _embeddingService.EncodeAsync(query, EmbeddingMode.Query, cancellationToken);
        var modelName = _embeddingService.ModelName;
        var embeddingResults = await _vectorStore.SearchAsync(queryVector, modelName, topK * 2, cancellationToken);
        // 使用 GroupBy 处理可能的重复 ChunkId，取第一个结果
        var embeddingDict = embeddingResults
            .GroupBy(r => r.ChunkId)
            .ToDictionary(g => g.Key, g => g.First());
        var embeddingRankMap = CreateRankMap(embeddingResults.Select(r => r.ChunkId).ToList());

        // 获取 Embedding 最大分数用于归一化
        var maxEmbeddingScore = embeddingResults.Any() ? embeddingResults.Max(r => r.Score) : 1.0;
        if (maxEmbeddingScore <= 0) maxEmbeddingScore = 1.0;

        // 3. 收集所有候选文档ID
        var allDocIds = bm25RankMap.Keys.Union(embeddingRankMap.Keys).ToHashSet();

        // 4. 融合 BM25 和 Embedding 结果
        var fusedScores = _options.UseRRF
            ? FuseWithRRF(allDocIds, bm25RankMap, embeddingRankMap)
            : FuseWithWeightedAverage(allDocIds, bm25Dict, embeddingDict, maxBm25Score, maxEmbeddingScore);

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

            string source = embeddingResult?.Source ?? string.Empty;
            string fileId = embeddingResult?.FileId ?? string.Empty;
            string filePath = embeddingResult?.FilePath ?? string.Empty;
            string title = embeddingResult?.Title ?? string.Empty;
            int startLine = embeddingResult?.StartLine ?? 0;
            int endLine = embeddingResult?.EndLine ?? 0;
            string? headingPath = embeddingResult?.HeadingPath;
            string? sqliteContent = null;  // 从 SQLite 取到的原始内容，优先于 BM25 分词内容

            if (embeddingResult == null && bm25Result != null)
            {
                try
                {
                    var chunk = await _vectorStore.GetChunkAsync(docId, cancellationToken);
                    if (chunk != null)
                    {
                        // 使用 SQLite 存储的原始内容，而非 BM25 的分词内容
                        sqliteContent = chunk.Content;
                        startLine = chunk.StartLine;
                        endLine = chunk.EndLine;
                        headingPath = chunk.HeadingPath;

                        var file = await _vectorStore.GetFileAsync(chunk.FileId, cancellationToken);
                        if (file != null)
                        {
                            source = file.Source ?? string.Empty;
                            fileId = chunk.FileId;
                            filePath = file.Path;
                            title = file.Title ?? string.Empty;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "查找 chunk {ChunkId} 的元数据失败", docId);
                }
            }

            results.Add(new HybridSearchResult
            {
                ChunkId = docId,
                FileId = fileId,
                FilePath = filePath,
                Title = title,
                // 优先级：向量搜索内容 > SQLite 原始内容 > BM25 分词内容
                Content = embeddingResult?.Content ?? sqliteContent ?? bm25Result?.Content ?? string.Empty,
                Score = fusedScore,
                BM25Score = (float)(bm25Result?.Score ?? 0),
                EmbeddingScore = (float)(embeddingResult?.Score ?? 0),
                BM25Rank = bm25RankMap.GetValueOrDefault(docId, -1),
                EmbeddingRank = embeddingRankMap.GetValueOrDefault(docId, -1),
                Source = source,
                StartLine = startLine,
                EndLine = endLine,
                HeadingPath = headingPath
            });
        }

        // 7. 应用文件夹过滤
        if (folders?.Any() == true)
        {
            var folderList = folders
                .Select(f => f.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                              .TrimEnd(Path.DirectorySeparatorChar)
                            + Path.DirectorySeparatorChar)
                .ToList();

            var beforeCount = results.Count;
            results = results.Where(r =>
            {
                var filePath = r.FilePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                return folderList.Any(f => filePath.StartsWith(f, StringComparison.OrdinalIgnoreCase));
            }).ToList();

            _logger?.LogDebug("文件夹过滤后: {Before} -> {After}", beforeCount, results.Count);
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

    /// <summary>
    /// 使用 RRF (Reciprocal Rank Fusion) 融合排名
    /// </summary>
    private Dictionary<string, float> FuseWithRRF(
        HashSet<string> allDocIds,
        Dictionary<string, int> bm25RankMap,
        Dictionary<string, int> embeddingRankMap)
    {
        var fusedScores = new Dictionary<string, float>();
        var k = _options.RRFK;

        foreach (var docId in allDocIds)
        {
            var bm25Rank = bm25RankMap.GetValueOrDefault(docId, -1);
            var embeddingRank = embeddingRankMap.GetValueOrDefault(docId, -1);

            // 同时出现在两个列表：标准 RRF 求和；仅出现在一个列表：打 0.5 折扣
            double fusedScore;
            if (bm25Rank >= 0 && embeddingRank >= 0)
                fusedScore = 1.0 / (k + bm25Rank) + 1.0 / (k + embeddingRank);
            else if (bm25Rank >= 0)
                fusedScore = 0.5 / (k + bm25Rank);
            else if (embeddingRank >= 0)
                fusedScore = 0.5 / (k + embeddingRank);
            else
                fusedScore = 0;

            fusedScores[docId] = (float)fusedScore;
        }

        return fusedScores;
    }

    /// <summary>
    /// 使用分数级加权融合 (Score-level Weighted Fusion)
    /// </summary>
    private Dictionary<string, float> FuseWithWeightedAverage(
        HashSet<string> allDocIds,
        Dictionary<string, BM25SearchResult> bm25Dict,
        Dictionary<string, SearchResult> embeddingDict,
        double maxBm25Score,
        double maxEmbeddingScore)
    {
        var fusedScores = new Dictionary<string, float>();

        foreach (var docId in allDocIds)
        {
            var bm25Score = bm25Dict.TryGetValue(docId, out var bm25Result) ? bm25Result.Score : 0;
            var embeddingScore = embeddingDict.TryGetValue(docId, out var embeddingResult) ? embeddingResult.Score : 0;

            var normalizedBm25 = maxBm25Score > 0 ? (float)(bm25Score / maxBm25Score) : 0;
            var normalizedEmbedding = maxEmbeddingScore > 0 ? (float)(embeddingScore / maxEmbeddingScore) : 0;

            var fusedScore = _options.BM25Weight * normalizedBm25 + _options.EmbeddingWeight * normalizedEmbedding;

            fusedScores[docId] = fusedScore;
        }

        return fusedScores;
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
    public float Score { get; set; }
    public float BM25Score { get; set; }
    public float EmbeddingScore { get; set; }
    public int BM25Rank { get; set; }
    public int EmbeddingRank { get; set; }
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
    public bool UseRRF { get; set; } = false;
    public int RRFK { get; set; } = 60;
    public float BM25Weight { get; set; } = 0.35f;
    public float EmbeddingWeight { get; set; } = 0.65f;
    public BM25Options BM25Options { get; set; } = new();

    public void Validate()
    {
        if (RRFK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RRFK), "RRFK must be positive");
        }

        if (!UseRRF)
        {
            var weightSum = BM25Weight + EmbeddingWeight;
            if (Math.Abs(weightSum - 1.0f) > 0.001f)
            {
                throw new ArgumentException(
                    $"BM25Weight ({BM25Weight}) + EmbeddingWeight ({EmbeddingWeight}) must equal 1.0 when UseRRF=false. Current sum: {weightSum}");
            }
        }

        if (BM25Weight < 0 || BM25Weight > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(BM25Weight), "BM25Weight must be between 0 and 1");
        }

        if (EmbeddingWeight < 0 || EmbeddingWeight > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(EmbeddingWeight), "EmbeddingWeight must be between 0 and 1");
        }
    }
}
