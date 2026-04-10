using System.Diagnostics;
using ObsidianRAG.Core.Helpers;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 搜索服务实现
/// </summary>
public class SearchService : ISearchService
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly ITextEnhancer _textEnhancer;
    private readonly HybridSearchService? _hybridSearchService;
    private readonly ConfigManager _configManager;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        IVectorStore vectorStore,
        IEmbeddingService embeddingService,
        ITextEnhancer textEnhancer,
        ConfigManager configManager,
        ILogger<SearchService> logger,
        HybridSearchService? hybridSearchService = null)
    {
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _textEnhancer = textEnhancer;
        _configManager = configManager;
        _logger = logger;
        _hybridSearchService = hybridSearchService;
    }

    /// <summary>
    /// 初始化搜索服务 - 预热 BM25 索引
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_hybridSearchService != null)
        {
            _logger.LogInformation("初始化混合搜索服务 BM25 索引...");
            await _hybridSearchService.InitializeAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 获取启用的源名称列表
    /// 如果配置中没有源，则从数据库获取所有存在的源
    /// </summary>
    private async Task<HashSet<string>> GetEnabledSourceNamesAsync()
    {
        var config = _configManager.Load();
        var enabledSources = config.Sources
            .Where(s => s.Enabled)
            .Select(s => s.Name)
            .ToHashSet();

        // 如果配置中没有源，从数据库获取所有存在的源作为回退
        if (enabledSources.Count == 0)
        {
            var allFiles = await _vectorStore.GetAllFilesAsync();
            enabledSources = allFiles
                .Select(f => f.Source)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .ToHashSet();

            _logger.LogDebug("配置中没有源，从数据库获取 {Count} 个源: {Sources}",
                enabledSources.Count, string.Join(", ", enabledSources));
        }

        return enabledSources;
    }

    /// <summary>
    /// 搜索
    /// </summary>
    public async Task<AIQueryResponse> SearchAsync(AIQueryRequest request, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // 获取启用的源列表
        var enabledSources = await GetEnabledSourceNamesAsync();

        List<SearchResult> topResults;

        // 混合搜索模式
        if (request.Mode == QueryMode.Hybrid && _hybridSearchService != null)
        {
            var hybridResults = await _hybridSearchService.SearchAsync(request.Query, request.TopK, cancellationToken);

            // 过滤禁用源
            var filteredHybridResults = hybridResults.Where(r => enabledSources.Contains(r.Source));

            topResults = filteredHybridResults.Select(h => new SearchResult
            {
                ChunkId = h.ChunkId,
                FileId = h.FileId,
                FilePath = h.FilePath,
                Title = h.Title,
                Content = h.Content,
                Score = h.Score,
                StartLine = h.StartLine,
                EndLine = h.EndLine,
                HeadingPath = h.HeadingPath,
                Source = h.Source
            }).ToList();
        }
        else
        {
            // 标准向量搜索（使用当前模型）
            var queryVector = await _embeddingService.EncodeAsync(request.Query, EmbeddingMode.Query, cancellationToken);
            var modelName = _embeddingService.ModelName;
            var results = await _vectorStore.SearchAsync(queryVector, modelName, request.TopK * 2, cancellationToken);

            // 首先过滤禁用源
            results = results.Where(r => enabledSources.Contains(r.Source));

            // 应用源过滤（用户指定的源）
            if (request.Sources?.Count > 0)
            {
                results = results.Where(r => request.Sources.Contains(r.Source));
            }

            // 应用其他过滤条件
            var filteredResults = ApplyFilters(results, request.Filters);

            // 流行度去偏
            if (request.Options?.DebiasPopularity ?? true)
            {
                filteredResults = DebiasByPopularity(filteredResults);
            }

            topResults = filteredResults.Take(request.TopK).ToList();
        }

        // 构建响应
        var chunks = BuildChunkResults(topResults);
        var files = BuildFileSummaries(topResults);
        var context = BuildContext(chunks, request.MaxTokens);
        var prompt = BuildPrompt(request.Query, context);

        sw.Stop();

        return new AIQueryResponse
        {
            Query = request.Query,
            Mode = request.Mode,
            Context = context,
            Prompt = prompt,
            Chunks = chunks,
            Files = files,
            Stats = new SearchStats
            {
                TotalMatches = topResults.Count,
                DurationMs = sw.ElapsedMilliseconds,
                EstimatedTokens = TokenEstimator.EstimateTokens(context)
            },
            HasMore = topResults.Count >= request.TopK,
            Suggestion = topResults.Count >= request.TopK
                ? "如需更多结果，可使用 drill-down 接口深入查询"
                : null
        };
    }

    /// <summary>
    /// 深入查询
    /// </summary>
    public async Task<DrillDownResponse> DrillDownAsync(DrillDownRequest request, CancellationToken cancellationToken = default)
    {
        var expandedChunks = new List<ChunkResult>();

        foreach (var refId in request.RefIds)
        {
            // 获取原始分段
            var chunk = await _vectorStore.GetChunkAsync(refId);
            if (chunk == null) continue;

            // 获取相邻分段
            var adjacentChunks = await GetAdjacentChunksAsync(chunk, request.ExpandContext);
            expandedChunks.AddRange(adjacentChunks);
        }

        var fullContext = string.Join("\n\n---\n\n", expandedChunks.Select(c => c.Content));

        return new DrillDownResponse
        {
            ExpandedChunks = expandedChunks,
            FullContext = fullContext
        };
    }

    /// <summary>
    /// 应用过滤条件
    /// </summary>
    private IEnumerable<SearchResult> ApplyFilters(IEnumerable<SearchResult> results, SearchFilter? filters)
    {
        if (filters == null) return results;

        var filtered = results;

        // 文件夹过滤
        if (filters.Folders?.Count > 0)
        {
            filtered = filtered.Where(r => 
                filters.Folders.Any(f => r.FilePath.StartsWith(f)));
        }

        return filtered;
    }

    /// <summary>
    /// 流行度去偏
    /// </summary>
    private IEnumerable<SearchResult> DebiasByPopularity(IEnumerable<SearchResult> results)
    {
        // 简化实现：降低热门文件的分数
        var fileCounts = results
            .GroupBy(r => r.FileId)
            .ToDictionary(g => g.Key, g => g.Count());

        return results.Select(r =>
        {
            var count = fileCounts.GetValueOrDefault(r.FileId, 1);
            var penalty = 1.0f / (1.0f + MathF.Log(count) * 0.1f);
            r.Score *= penalty;
            return r;
        }).OrderByDescending(r => r.Score);
    }

    /// <summary>
    /// 构建分段结果
    /// </summary>
    private List<ChunkResult> BuildChunkResults(IEnumerable<SearchResult> results)
    {
        return results.Select((r, i) => new ChunkResult
        {
            RefId = r.ChunkId,
            FileId = r.FileId,
            FilePath = r.FilePath,
            Source = r.Source,
            Title = r.Title,
            Content = r.Content,
            Score = r.Score,
            StartLine = r.StartLine,
            EndLine = r.EndLine,
            HeadingPath = r.HeadingPath,
            ObsidianLink = $"[[{Path.GetFileNameWithoutExtension(r.FilePath)}#L{r.StartLine}-L{r.EndLine}]]"
        }).ToList();
    }

    /// <summary>
    /// 构建文件摘要
    /// </summary>
    private List<FileSummary> BuildFileSummaries(IEnumerable<SearchResult> results)
    {
        return results
            .GroupBy(r => r.FileId)
            .Select(g => new FileSummary
            {
                Id = g.Key,
                Path = g.First().FilePath,
                Title = g.First().Title,
                ChunkCount = g.Count()
            })
            .ToList();
    }

    /// <summary>
    /// 构建上下文
    /// </summary>
    private string BuildContext(List<ChunkResult> chunks, int maxTokens)
    {
        var context = new System.Text.StringBuilder();
        context.AppendLine("# 相关内容\n");

        var currentTokens = 0;

        foreach (var chunk in chunks)
        {
            var chunkTokens = TokenEstimator.EstimateTokens(chunk.Content);
            if (currentTokens + chunkTokens > maxTokens) break;

            context.AppendLine($"## {chunk.Title ?? Path.GetFileNameWithoutExtension(chunk.FilePath)}");
            if (!string.IsNullOrEmpty(chunk.HeadingPath))
            {
                context.AppendLine($"章节: {chunk.HeadingPath}");
            }
            context.AppendLine($"\n{chunk.Content}\n");
            context.AppendLine($"来源: {chunk.ObsidianLink}\n");
            context.AppendLine("---\n");

            currentTokens += chunkTokens;
        }

        return context.ToString();
    }

    /// <summary>
    /// 构建 Prompt
    /// </summary>
    private string BuildPrompt(string query, string context)
    {
        return $@"基于以下知识库内容回答问题：

{context}

问题：{query}

请根据上述内容提供准确、详细的回答，并在回答中引用来源。";
    }

    /// <summary>
    /// 获取相邻分段
    /// </summary>
    private async Task<List<ChunkResult>> GetAdjacentChunksAsync(ChunkRecord chunk, int expandContext)
    {
        var chunks = await _vectorStore.GetChunksByFileAsync(chunk.FileId);
        var chunkList = chunks.OrderBy(c => c.ChunkIndex).ToList();

        var index = chunkList.FindIndex(c => c.Id == chunk.Id);
        if (index < 0) return new List<ChunkResult>();

        var start = Math.Max(0, index - expandContext);
        var end = Math.Min(chunkList.Count - 1, index + expandContext);

        return chunkList
            .Skip(start)
            .Take(end - start + 1)
            .Select(c => new ChunkResult
            {
                RefId = c.Id,
                FileId = c.FileId,
                FilePath = "",
                Content = c.Content,
                StartLine = c.StartLine,
                EndLine = c.EndLine,
                HeadingPath = c.HeadingPath,
                ObsidianLink = $"[[#L{c.StartLine}-L{c.EndLine}]]"
            })
            .ToList();
    }

}
