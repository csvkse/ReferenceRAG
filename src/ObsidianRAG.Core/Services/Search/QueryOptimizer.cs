using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 文件元数据提供者接口 - 用于查询优化器获取文件元数据
/// </summary>
public interface IFileMetadataProvider
{
    /// <summary>
    /// 根据文件ID获取标签列表
    /// </summary>
    Task<List<string>?> GetTagsAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 根据文件ID获取修改时间
    /// </summary>
    Task<DateTime?> GetModifiedAtAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量获取文件元数据
    /// </summary>
    Task<Dictionary<string, FileMetadata>> GetBatchAsync(
        IEnumerable<string> fileIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 文件元数据
/// </summary>
public class FileMetadata
{
    public string FileId { get; set; } = string.Empty;
    public List<string>? Tags { get; set; }
    public DateTime? ModifiedAt { get; set; }
    public DateTime? CreatedAt { get; set; }
}

/// <summary>
/// 基于 IVectorStore 的文件元数据提供者实现
/// </summary>
public class VectorStoreFileMetadataProvider : IFileMetadataProvider
{
    private readonly IVectorStore _vectorStore;

    public VectorStoreFileMetadataProvider(IVectorStore vectorStore)
    {
        _vectorStore = vectorStore;
    }

    public async Task<List<string>?> GetTagsAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var file = await _vectorStore.GetFileAsync(fileId, cancellationToken);
        return file?.Tags;
    }

    public async Task<DateTime?> GetModifiedAtAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var file = await _vectorStore.GetFileAsync(fileId, cancellationToken);
        return file?.ModifiedAt;
    }

    public async Task<Dictionary<string, FileMetadata>> GetBatchAsync(
        IEnumerable<string> fileIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, FileMetadata>();

        foreach (var fileId in fileIds.Distinct())
        {
            var file = await _vectorStore.GetFileAsync(fileId, cancellationToken);
            if (file != null)
            {
                result[fileId] = new FileMetadata
                {
                    FileId = fileId,
                    Tags = file.Tags,
                    ModifiedAt = file.ModifiedAt,
                    CreatedAt = file.CreatedAt
                };
            }
        }

        return result;
    }
}

/// <summary>
/// 查询优化器 - 属性过滤、流行度去偏
/// </summary>
public class QueryOptimizer
{
    private readonly IVectorStore _vectorStore;
    private readonly IFileMetadataProvider _metadataProvider;
    private readonly QueryOptimizeOptions _options;
    private readonly SynonymService _synonymService;

    public QueryOptimizer(IVectorStore vectorStore, IFileMetadataProvider? metadataProvider = null, QueryOptimizeOptions? options = null)
    {
        _vectorStore = vectorStore;
        _metadataProvider = metadataProvider ?? new VectorStoreFileMetadataProvider(vectorStore);
        _options = options ?? new QueryOptimizeOptions();
        _synonymService = new SynonymService();
    }

    /// <summary>
    /// 优化查询 - 应用过滤和重排序
    /// </summary>
    public async Task<List<SearchResult>> OptimizeAsync(
        IEnumerable<SearchResult> results,
        AIQueryRequest request)
    {
        var resultlist = results.ToList();

        // 1. 应用过滤器
        if (request.Filters != null)
        {
            resultlist = await ApplyFiltersAsync(resultlist, request.Filters);
        }

        // 2. 流行度去偏
        if (_options.ApplyPopularityDebias)
        {
            resultlist = ApplyPopularityDebias(resultlist);
        }

        // 3. 多样性重排
        if (_options.ApplyDiversityRerank && request.TopK > 1)
        {
            resultlist = ApplyDiversityRerank(resultlist, request.TopK);
        }

        // 4. 截断到请求数量
        return resultlist.Take(request.TopK).ToList();
    }

    /// <summary>
    /// 应用过滤器
    /// </summary>
    private async Task<List<SearchResult>> ApplyFiltersAsync(List<SearchResult> results, SearchFilter filters)
    {
        var query = results.AsQueryable();

        // 路径过滤
        if (filters.Folders?.Count > 0)
        {
            query = query.Where(r => filters.Folders.Any(f => r.FilePath.StartsWith(f)));
        }

        var filteredResults = query.ToList();

        // 标签过滤 - 从文件记录中获取标签
        if (filters.Tags?.Count > 0)
        {
            var fileIds = filteredResults.Select(r => r.FileId).Distinct();
            var metadata = await _metadataProvider.GetBatchAsync(fileIds);

            filteredResults = filteredResults
                .Where(r => metadata.TryGetValue(r.FileId, out var meta)
                    && meta.Tags != null
                    && filters.Tags.Any(t => meta.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                .ToList();
        }

        // 时间范围过滤 - 从文件记录中获取时间
        if (filters.DateRange != null)
        {
            var fileIds = filteredResults.Select(r => r.FileId).Distinct();
            var metadata = await _metadataProvider.GetBatchAsync(fileIds);

            filteredResults = filteredResults
                .Where(r =>
                {
                    if (!metadata.TryGetValue(r.FileId, out var meta) || meta.ModifiedAt == null)
                        return false;

                    var modifiedAt = meta.ModifiedAt.Value;
                    var afterStart = filters.DateRange.Start == null || modifiedAt >= filters.DateRange.Start;
                    var beforeEnd = filters.DateRange.End == null || modifiedAt <= filters.DateRange.End;

                    return afterStart && beforeEnd;
                })
                .ToList();
        }

        return filteredResults;
    }

    /// <summary>
    /// 流行度去偏 - 降低高频文档的权重
    /// </summary>
    private List<SearchResult> ApplyPopularityDebias(List<SearchResult> results)
    {
        // 计算文档出现频率
        var docFrequency = results
            .GroupBy(r => r.FileId)
            .ToDictionary(g => g.Key, g => g.Count());

        var avgFrequency = docFrequency.Values.Average();

        // 调整分数
        foreach (var result in results)
        {
            if (docFrequency.TryGetValue(result.FileId, out var freq))
            {
                // 频率越高，惩罚越大
                var penalty = MathF.Log10((float)(freq / avgFrequency) + 1) * _options.PopularityPenalty;
                result.Score *= (1 - penalty);
            }
        }

        return results.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>
    /// 多样性重排 - MMR 算法
    /// </summary>
    private List<SearchResult> ApplyDiversityRerank(List<SearchResult> results, int topK)
    {
        if (results.Count <= topK) return results;

        var selected = new List<SearchResult>();
        var remaining = new List<SearchResult>(results);

        // 选择第一个（最高分）
        selected.Add(remaining[0]);
        remaining.RemoveAt(0);

        // 迭代选择
        while (selected.Count < topK && remaining.Count > 0)
        {
            var bestIndex = 0;
            var bestScore = float.MinValue;

            for (int i = 0; i < remaining.Count; i++)
            {
                var candidate = remaining[i];
                
                // 计算与已选结果的最大相似度
                var maxSimilarity = selected.Max(s => ComputeContentSimilarity(s.Content, candidate.Content));
                
                // MMR 分数 = λ * Relevance - (1-λ) * Redundancy
                var mmrScore = _options.MmrLambda * candidate.Score - 
                               (1 - _options.MmrLambda) * maxSimilarity;

                if (mmrScore > bestScore)
                {
                    bestScore = mmrScore;
                    bestIndex = i;
                }
            }

            selected.Add(remaining[bestIndex]);
            remaining.RemoveAt(bestIndex);
        }

        return selected;
    }

    /// <summary>
    /// 计算内容相似度（简化版）
    /// </summary>
    private float ComputeContentSimilarity(string a, string b)
    {
        // 使用 Jaccard 相似度
        var wordsA = a.Split(' ', '　', '\n', '\t').ToHashSet();
        var wordsB = b.Split(' ', '　', '\n', '\t').ToHashSet();

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union == 0 ? 0 : (float)intersection / union;
    }

    /// <summary>
    /// 扩展查询 - 同义词扩展
    /// </summary>
    public string ExpandQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        return _synonymService.ExpandQuery(query);
    }

    /// <summary>
    /// 分析查询意图
    /// </summary>
    public QueryIntent AnalyzeIntent(string query)
    {
        var intent = new QueryIntent();

        // 检测查询类型
        if (query.Contains("如何") || query.Contains("怎么"))
        {
            intent.Type = QueryType.HowTo;
        }
        else if (query.Contains("什么") || query.Contains("定义"))
        {
            intent.Type = QueryType.Definition;
        }
        else if (query.Contains("为什么"))
        {
            intent.Type = QueryType.Explanation;
        }
        else if (query.Contains("例子") || query.Contains("示例"))
        {
            intent.Type = QueryType.Example;
        }
        else
        {
            intent.Type = QueryType.General;
        }

        // 提取关键词
        intent.Keywords = ExtractKeywords(query);

        return intent;
    }

    /// <summary>
    /// 提取关键词
    /// </summary>
    private List<string> ExtractKeywords(string query)
    {
        var words = query.Split(' ', '　', '，', '。', '？', '！')
            .Where(w => w.Length > 1 && !_synonymService.IsStopWord(w))
            .ToList();

        return words;
    }
}

/// <summary>
/// 查询优化配置
/// </summary>
public class QueryOptimizeOptions
{
    /// <summary>
    /// 是否应用流行度去偏
    /// </summary>
    public bool ApplyPopularityDebias { get; set; } = true;

    /// <summary>
    /// 流行度惩罚系数
    /// </summary>
    public float PopularityPenalty { get; set; } = 0.1f;

    /// <summary>
    /// 是否应用多样性重排
    /// </summary>
    public bool ApplyDiversityRerank { get; set; } = true;

    /// <summary>
    /// MMR Lambda 参数（相关性 vs 多样性权衡）
    /// </summary>
    public float MmrLambda { get; set; } = 0.7f;
}

/// <summary>
/// 查询意图
/// </summary>
public class QueryIntent
{
    public QueryType Type { get; set; }
    public List<string> Keywords { get; set; } = new();
}

/// <summary>
/// 查询类型
/// </summary>
public enum QueryType
{
    General,
    HowTo,
    Definition,
    Explanation,
    Example,
    Comparison
}
