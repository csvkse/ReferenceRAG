using System.Diagnostics;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Services.Graph;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 搜索服务实现
/// </summary>
public class SearchService : ISearchService
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly ITextEnhancer _textEnhancer;
    private readonly HybridSearchService? _hybridSearchService;
    private readonly IRerankService? _rerankService;
    private readonly IGraphStore? _graphStore;
    private readonly ConfigManager _configManager;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        IVectorStore vectorStore,
        IEmbeddingService embeddingService,
        ITextEnhancer textEnhancer,
        ConfigManager configManager,
        ILogger<SearchService> logger,
        HybridSearchService? hybridSearchService = null,
        IRerankService? rerankService = null,
        IGraphStore? graphStore = null)
    {
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _textEnhancer = textEnhancer;
        _configManager = configManager;
        _logger = logger;
        _hybridSearchService = hybridSearchService;
        _rerankService = rerankService;
        _graphStore = graphStore;
    }

    /// <summary>
    /// 初始化搜索服务
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // HybridSearchService 不再需要初始化（已移除多模型支持）
        return Task.CompletedTask;
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
            var sources = new HashSet<string>();
            var allFiles = await _vectorStore.StreamAllFilesAsync();

            await foreach (var file in allFiles)
            {
                if (!string.IsNullOrEmpty(file.Source))
                {
                    sources.Add(file.Source);
                }
            }

            enabledSources = sources;

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
        var config = _configManager.Load();
        var rerankConfig = config.Rerank;

        // 判断是否需要执行两阶段搜索
        bool shouldRerank = DetermineRerankExecution(request, rerankConfig);

        // 获取启用的源列表
        var enabledSources = await GetEnabledSourceNamesAsync();

        // 请求源限定
        if (request.Sources != null && request.Sources.Count != 0)
            enabledSources = enabledSources.Where(m => request.Sources.Contains(m)).ToHashSet();

        // 并行启动：embedding 推理（CPU/GPU）与标题搜索（SQLite）互不依赖，同时进行
        var embeddingTask = _embeddingService.EncodeAsync(request.Query, EmbeddingMode.Query, cancellationToken);
        var titleTask = ShouldPreferTitleSearch(request.Query)
            ? TryTitleFirstSearchAsync(request, enabledSources, cancellationToken)
            : Task.FromResult<List<SearchResult>>(new());

        List<SearchResult> topResults;
        RerankStats? rerankStats = null;

        // 等待向量完成（标题搜索在后台继续，hybrid search 依赖向量先就绪）
        var queryVector = await embeddingTask;

        // ========== 第一阶段：召回 ==========
        if ((request.Mode == QueryMode.Hybrid || request.Mode == QueryMode.HybridRerank) && _hybridSearchService != null)
        {
            // 始终使用 RecallFactor 扩大候选池，不管是否重排，更多候选 = 更好的融合结果
            int recallTopK = request.TopK * rerankConfig.RecallFactor;

            _logger.LogDebug("混合搜索召回: TopK={TopK}, RecallFactor={Factor}, RecallTopK={RecallTopK}",
                request.TopK, rerankConfig.RecallFactor, recallTopK);

            var hybridResults = await _hybridSearchService.SearchAsync(
                request.Query, recallTopK, request.K1, request.B,
                request.Filters?.Folders, cancellationToken,
                precomputedQueryVector: queryVector);

            // 过滤禁用源（source 为空表示旧数据未分配来源，允许通过）
            var filteredHybridResults = hybridResults.Where(r => string.IsNullOrEmpty(r.Source) || enabledSources.Contains(r.Source));

            topResults = filteredHybridResults.Select(h => new SearchResult
            {
                ChunkId = h.ChunkId,
                FileId = h.FileId,
                FilePath = h.FilePath,
                Title = h.Title,
                Content = h.Content,
                Score = h.Score,
                BM25Score = h.BM25Score,
                EmbeddingScore = h.EmbeddingScore,
                StartLine = h.StartLine,
                EndLine = h.EndLine,
                HeadingPath = h.HeadingPath,
                Source = h.Source
            }).ToList();
        }
        else
        {
            // 标准向量搜索（复用预计算向量）
            topResults = await ExecuteStandardSearchAsync(request, enabledSources, cancellationToken, queryVector);
        }

        // 合并标题候选：等待标题搜索完成，去重后插入头部
        // 标题命中高精度，rerank 有机会将其排到最前；无 rerank 时凭分数自然优先
        var titleResults = await titleTask;
        if (titleResults.Count > 0)
        {
            var existingIds = topResults.Select(r => r.ChunkId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newFromTitle = titleResults.Where(r => existingIds.Add(r.ChunkId)).ToList();
            if (newFromTitle.Count > 0)
            {
                topResults.InsertRange(0, newFromTitle);
                _logger.LogDebug("标题候选合并: +{Count} 条（总候选 {Total}）", newFromTitle.Count, topResults.Count);
            }
        }

        // ========== 图扩展：补充 wiki-link 邻居节点 ==========
        var searchConfig = config.Search;
        if (searchConfig.EnableGraphExpansion && _graphStore != null && topResults.Count > 0)
        {
            topResults = await ExpandWithGraphAsync(topResults, queryVector, searchConfig, cancellationToken);
        }

        // ========== 第二阶段：精排 ==========
        if (shouldRerank && _rerankService != null && topResults.Count > 0)
        {
            var rerankSw = Stopwatch.StartNew();
            var topN = request.RerankTopN ?? rerankConfig.TopN;
            var rerankCandidateLimit = Math.Clamp(topN * Math.Max(2, rerankConfig.RecallFactor), topN, 50);

            if (topResults.Count > rerankCandidateLimit)
            {
                topResults = topResults
                    .Take(rerankCandidateLimit)
                    .ToList();
            }

            _logger.LogInformation("开始重排: 候选数={Count}, Query={Query}", topResults.Count, request.Query);

            // 批量重排
            var documents = topResults.Select(r => r.Content).ToList();
            var rerankResult = await _rerankService.RerankBatchAsync(
                request.Query,
                documents,
                cancellationToken);

            // 获取 TopN
            // 构建重排后的结果
            var rerankedResults = new List<SearchResult>();
            foreach (var doc in rerankResult.Documents.Take(topN))
            {
                var originalResult = topResults[doc.Index];
                originalResult.RerankScore = doc.RelevanceScore;
                originalResult.Score = (float)doc.RelevanceScore; // 更新主分数
                rerankedResults.Add(originalResult);
            }

            // 应用分数阈值过滤
            if (rerankConfig.ScoreThreshold > 0)
            {
                var beforeCount = rerankedResults.Count;
                rerankedResults = rerankedResults
                    .Where(r => r.RerankScore >= rerankConfig.ScoreThreshold)
                    .ToList();
                _logger.LogDebug("分数阈值过滤: {Before} -> {After}", beforeCount, rerankedResults.Count);
            }

            rerankSw.Stop();

            // 记录重排统计
            rerankStats = new RerankStats
            {
                CandidatesCount = topResults.Count,
                RerankDurationMs = rerankSw.ElapsedMilliseconds,
                ModelName = _rerankService.ModelName,
                AverageScore = rerankedResults.Count > 0
                    ? rerankedResults.Average(r => r.RerankScore ?? 0)
                    : 0
            };

            topResults = rerankedResults;

            _logger.LogInformation(
                "两阶段搜索完成: 召回 {Candidates} 个候选, 重排耗时 {Ms}ms, 返回 {Count} 个结果, 平均分数={AvgScore:F4}",
                rerankStats.CandidatesCount, rerankStats.RerankDurationMs, topResults.Count, rerankStats.AverageScore);
        }
        else if (shouldRerank && _rerankService == null)
        {
            _logger.LogWarning("重排服务不可用，跳过第二阶段精排");
            topResults = topResults.Take(request.TopK).ToList();
        }
        else
        {
            // 无重排：RecallFactor 扩大了候选池，最终 trim 回 TopK
            topResults = topResults.Take(request.TopK).ToList();
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
            HasMore = false, // 重排后不再有分页
            Suggestion = null,
            RerankApplied = shouldRerank && _rerankService != null && topResults.Count > 0,
            RerankStats = rerankStats
        };
    }

    /// <summary>
    /// 判断是否应该优先走标题检索
    /// </summary>
    private static bool ShouldPreferTitleSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;

        var trimmed = query.Trim();
        if (trimmed.Length <= 32) return true;

        var tokens = trimmed.Split(
            new[] { ' ', '\t', '\r', '\n', '/', '\\', '-', '_', ':', '|', '，', '。', '？', '！', '?', '!' },
            StringSplitOptions.RemoveEmptyEntries);

        return tokens.Length <= 4;
    }

    /// <summary>
    /// 标题优先检索：先查图节点标题，再回到对应文件的首个命中 chunk
    /// </summary>
    private async Task<List<SearchResult>> TryTitleFirstSearchAsync(
        AIQueryRequest request,
        HashSet<string> enabledSources,
        CancellationToken cancellationToken)
    {
        if (_graphStore == null || string.IsNullOrWhiteSpace(request.Query))
            return new List<SearchResult>();

        var nodes = await _graphStore.SearchNodesAsync(
            request.Query.Trim(),
            Math.Max(request.TopK * 3, 10),
            cancellationToken);

        if (nodes.Count == 0)
            return new List<SearchResult>();

        // 收集 TopK*2 个候选：合并后由 rerank 决定最终排序，不再提前截断
        var maxCandidates = request.TopK * 2;
        var results = new List<SearchResult>();
        var seenChunkIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodes)
        {
            if (results.Count >= maxCandidates)
                break;

            var filePath = ExtractFilePathFromNodeId(node.Id);
            if (string.IsNullOrWhiteSpace(filePath))
                continue;

            var file = await _vectorStore.GetFileByPathAsync(filePath, cancellationToken);
            if (file == null)
                continue;

            if (!string.IsNullOrEmpty(file.Source) && !enabledSources.Contains(file.Source))
                continue;

            var chunks = (await _vectorStore.GetChunksByFileAsync(file.Id, cancellationToken)).ToList();
            if (chunks.Count == 0)
                continue;

            var chosenChunk = ChooseBestTitleChunk(node, request.Query, chunks);
            if (chosenChunk == null)
                continue;

            if (!seenChunkIds.Add(chosenChunk.Id))
                continue;

            if (node.Type.Equals("document", StringComparison.OrdinalIgnoreCase) && !seenFileIds.Add(file.Id))
                continue;

            results.Add(new SearchResult
            {
                ChunkId = chosenChunk.Id,
                FileId = file.Id,
                FilePath = file.Path,
                Source = file.Source,
                Title = file.Title ?? file.FileName,
                Content = chosenChunk.Content,
                Score = ComputeTitlePriorityScore(node, file, request.Query),
                StartLine = chosenChunk.StartLine,
                EndLine = chosenChunk.EndLine,
                HeadingPath = chosenChunk.HeadingPath,
                Level = chosenChunk.Level,
                AggregateType = chosenChunk.AggregateType,
                ChildChunkCount = chosenChunk.ChildChunkCount
            });
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ExtractFilePathFromNodeId(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) return null;

        var hashIndex = nodeId.IndexOf('#');
        return hashIndex >= 0 ? nodeId[..hashIndex] : nodeId;
    }

    private static ChunkRecord? ChooseBestTitleChunk(GraphNode node, string query, List<ChunkRecord> chunks)
    {
        if (chunks.Count == 0) return null;

        if (node.Type.Equals("heading", StringComparison.OrdinalIgnoreCase))
        {
            var headingChunk = chunks.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.HeadingPath) &&
                c.HeadingPath.Contains(node.Title, StringComparison.OrdinalIgnoreCase));

            if (headingChunk != null)
                return headingChunk;
        }

        var exactHeading = chunks.FirstOrDefault(c =>
            !string.IsNullOrEmpty(c.HeadingPath) &&
            c.HeadingPath.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (exactHeading != null)
            return exactHeading;

        return chunks.OrderBy(c => c.ChunkIndex).FirstOrDefault();
    }

    private static float ComputeTitlePriorityScore(GraphNode node, FileRecord file, string query)
    {
        var normalizedQuery = query.Trim();
        var fileTitle = file.Title ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(file.FileName ?? file.Path ?? string.Empty);

        if (fileTitle.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0f;
        }

        if (node.Type.Equals("heading", StringComparison.OrdinalIgnoreCase))
        {
            if (node.Title.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                return 0.98f;

            if (node.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                return 0.92f;
        }

        if (fileTitle.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 0.95f;
        }

        return 0.85f;
    }

    /// <summary>
    /// 判断是否需要执行重排
    /// </summary>
    private bool DetermineRerankExecution(AIQueryRequest request, RerankConfig config)
    {
        // 请求参数优先级最高
        if (request.EnableRerank.HasValue)
            return request.EnableRerank.Value;

        // HybridRerank 模式强制启用重排
        if (request.Mode == QueryMode.HybridRerank)
            return true;

        // Hybrid 模式不启用重排（纯混合召回）
        // 其他模式也不启用
        return false;
    }

    /// <summary>
    /// 执行标准向量搜索
    /// </summary>
    private async Task<List<SearchResult>> ExecuteStandardSearchAsync(
        AIQueryRequest request,
        HashSet<string> enabledSources,
        CancellationToken cancellationToken,
        float[]? precomputedQueryVector = null)
    {
        var queryVector = precomputedQueryVector
            ?? await _embeddingService.EncodeAsync(request.Query, EmbeddingMode.Query, cancellationToken);
        var modelName = _embeddingService.ModelName;
        var results = await _vectorStore.SearchAsync(queryVector, modelName, request.TopK * 2, cancellationToken);

        // 首先过滤禁用源（source 为空表示旧数据未分配来源，允许通过）
        results = results.Where(r => string.IsNullOrEmpty(r.Source) || enabledSources.Contains(r.Source));

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

        return filteredResults.Take(request.TopK).ToList();
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

        if (filters.Folders?.Count > 0)
        {
            filtered = filtered.Where(r =>
                filters.Folders.Any(f =>
                {
                    // 标准化：统一分隔符，去除尾部斜杠
                    var folder = f.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var filePath = r.FilePath;

                    // 必须是完整路径段：后面跟着分隔符，或者就是文件本身在该目录下
                    return filePath.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || filePath.StartsWith(folder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(filePath, folder, StringComparison.OrdinalIgnoreCase); // 精确匹配自身（如果 FilePath 可能是目录）
                }));
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
            BM25Score = r.BM25Score > 0 ? r.BM25Score : null,
            EmbeddingScore = r.EmbeddingScore > 0 ? r.EmbeddingScore : null,
            RerankScore = r.RerankScore,
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
        var chunks = await _vectorStore.GetAdjacentChunksByFileAsync(
            chunk.FileId,
            chunk.Id,
            expandContext);

        var chunkList = chunks.ToList();
        if (chunkList.Count == 0)
        {
            return new List<ChunkResult>();
        }

        return chunkList
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

    /// <summary>
    /// 图扩展：对每个召回结果，查找其 wiki-link 邻居文件的 chunks，追加到候选集。
    /// 邻居 chunks 初始分数设为直接召回结果的最低分乘以衰减系数，确保精排后不会盲目排前。
    /// </summary>
    private async Task<List<SearchResult>> ExpandWithGraphAsync(
        List<SearchResult> results,
        float[] queryVector,
        SearchConfig config,
        CancellationToken ct)
    {
        if (_graphStore == null) return results;

        var depth    = Math.Clamp(config.GraphExpansionDepth,    1, 2);
        var maxNodes = Math.Clamp(config.GraphExpansionMaxNodes, 1, 10);
        var existingChunkIds = results.Select(r => r.ChunkId).ToHashSet();
        var existingFileIds  = results.Select(r => r.FileId).ToHashSet();
        var additions = new List<SearchResult>();
        var traversalCache = new Dictionary<string, GraphTraversalResult>(StringComparer.OrdinalIgnoreCase);

        // 保底分：向量全零（模拟模式）时退回固定衰减值
        var fallbackScore = results.Min(r => r.Score) * 0.6f;

        // 先对同一文件去重，避免同一文档的多个 chunk 反复做图遍历
        var seedResults = results
            .Where(r => !string.IsNullOrEmpty(r.FilePath))
            .OrderByDescending(r => r.Score)
            .GroupBy(r => r.FilePath.Replace('\\', '/').TrimStart('/'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (var result in seedResults)
        {
            var nodeId = result.FilePath.Replace('\\', '/').TrimStart('/');

            try
            {
                if (!traversalCache.TryGetValue(nodeId, out var traversal))
                {
                    traversal = await _graphStore.GetNeighborsAsync(nodeId, depth, ct: ct);
                    traversalCache[nodeId] = traversal;
                }

                var neighborNodes = traversal.Nodes
                    .Where(n => n.Id != nodeId)
                    .Take(maxNodes)
                    .ToList();

                foreach (var neighbor in neighborNodes)
                {
                    if (existingFileIds.Contains(neighbor.Id)) continue;

                    // 一次性拉取候选 chunk 的向量，避免 N+1 查询
                    var candidateIds = neighbor.ChunkIds
                        .Where(cId => !existingChunkIds.Contains(cId))
                        .Take(10)
                        .ToList();

                    if (candidateIds.Count == 0) continue;

                    var candidateVectors = await _vectorStore.GetVectorsByChunkIdsAsync(candidateIds, ct);
                    string? bestChunkId = null;
                    float bestSim = -1;

                    foreach (var cId in candidateIds)
                    {
                        if (!candidateVectors.TryGetValue(cId, out var vec)) continue;

                        var sim = ReferenceRAG.Core.Helpers.MathHelper.CosineSimilarity(queryVector, vec.Vector);
                        if (sim > bestSim)
                        {
                            bestSim = sim;
                            bestChunkId = cId;
                        }
                    }

                    if (bestChunkId == null) continue;

                    var bestChunk = await _vectorStore.GetChunkAsync(bestChunkId, ct);
                    if (bestChunk == null) continue;

                    var file = await _vectorStore.GetFileAsync(bestChunk.FileId, ct);
                    if (file == null) continue;

                    // 用实际余弦相似度作为初始分；若向量为空则退回保底分
                    var score = bestSim > 0 ? bestSim : fallbackScore;

                    additions.Add(new SearchResult
                    {
                        ChunkId    = bestChunk.Id,
                        FileId     = bestChunk.FileId,
                        FilePath   = file.Path,
                        Title      = file.Title ?? file.FileName,
                        Content    = bestChunk.Content,
                        Score      = score,
                        Source     = file.Source ?? string.Empty,
                        StartLine  = bestChunk.StartLine,
                        EndLine    = bestChunk.EndLine,
                        HeadingPath = bestChunk.HeadingPath
                    });

                    existingChunkIds.Add(bestChunk.Id);
                    existingFileIds.Add(bestChunk.FileId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "图扩展失败: {NodeId}", nodeId);
            }
        }

        if (additions.Count > 0)
            _logger.LogInformation("图扩展: +{Count} 个邻居 chunks（共 {Total} 个候选）",
                additions.Count, results.Count + additions.Count);

        return results.Concat(additions).ToList();
    }

}
