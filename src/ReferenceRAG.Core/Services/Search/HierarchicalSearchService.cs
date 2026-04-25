using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 层级检索服务 - 文档 → 章节 → 分段三级检索
/// </summary>
public class HierarchicalSearchService
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly ITextEnhancer _textEnhancer;
    private readonly QueryOptimizer _queryOptimizer;
    private readonly ObsidianLinkGenerator _linkGenerator;

    public HierarchicalSearchService(
        IVectorStore vectorStore,
        IEmbeddingService embeddingService,
        ITextEnhancer textEnhancer,
        QueryOptimizer queryOptimizer,
        ObsidianLinkGenerator linkGenerator)
    {
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _textEnhancer = textEnhancer;
        _queryOptimizer = queryOptimizer;
        _linkGenerator = linkGenerator;
    }

    /// <summary>
    /// 层级检索 - 主入口
    /// </summary>
    public async Task<AIQueryResponse> SearchAsync(
        AIQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;

        // 1. 编码查询向量
        var queryVector = await _embeddingService.EncodeAsync(request.Query, EmbeddingMode.Query, cancellationToken);

        // 2. 层级检索
        var results = await HierarchicalSearchAsync(queryVector, request, cancellationToken);

        // 3. 优化结果
        var optimizedResults = await _queryOptimizer.OptimizeAsync(results, request);

        // 4. 组装上下文
        var context = BuildContext(optimizedResults, request.ContextWindow);

        // 5. 构建响应
        var response = new AIQueryResponse
        {
            Query = request.Query,
            Mode = request.Mode,
            Context = context,
            Prompt = BuildPrompt(request.Query, context),
            Chunks = optimizedResults.Select((r, i) => new ChunkResult
            {
                RefId = $"@{i + 1}",
                FileId = r.FileId,
                FilePath = r.FilePath,
                Title = r.Title,
                Content = r.Content,
                Score = r.Score,
                StartLine = r.StartLine,
                EndLine = r.EndLine,
                HeadingPath = r.HeadingPath,
                ObsidianLink = _linkGenerator.GenerateLink(r.FilePath, r.StartLine, r.EndLine)
            }).ToList(),
            Stats = new SearchStats
            {
                TotalMatches = optimizedResults.Count,
                DurationMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds,
                EstimatedTokens = TokenEstimator.EstimateTokens(context)
            },
            HasMore = optimizedResults.Count >= request.TopK
        };

        // 6. 生成建议
        response.Suggestion = GenerateSuggestion(response);

        return response;
    }

    /// <summary>
    /// 层级检索流程
    /// </summary>
    private async Task<List<SearchResult>> HierarchicalSearchAsync(
        float[] queryVector,
        AIQueryRequest request,
        CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();
        var modelName = _embeddingService.ModelName;

        // 第一层：文档级检索
        var docResults = await _vectorStore.SearchByAggregateTypeAsync(
            queryVector, AggregateType.Document, request.TopK * 2, cancellationToken);

        var docList = docResults.ToList();
        var candidateFileIds = docList.Select(r => r.FileId).Distinct().ToList();

        // 第二层：章节级检索（在候选文档范围内）
        var sectionResults = await _vectorStore.SearchByAggregateTypeAsync(
            queryVector, AggregateType.Section, request.TopK * 3, cancellationToken);

        var sectionList = sectionResults
            .Where(r => candidateFileIds.Contains(r.FileId))
            .ToList();

        // 第三层：分段级检索
        var chunkResults = await _vectorStore.SearchAsync(
            queryVector, modelName, request.TopK * 2, cancellationToken);

        var chunkList = chunkResults.ToList();

        // 合并结果，按层级权重计算最终分数
        results = MergeResults(docList, sectionList, chunkList, request);

        return results;
    }

    /// <summary>
    /// 合并多层级结果
    /// </summary>
    private List<SearchResult> MergeResults(
        List<SearchResult> docResults,
        List<SearchResult> sectionResults,
        List<SearchResult> chunkResults,
        AIQueryRequest request)
    {
        var merged = new Dictionary<string, SearchResult>();

        // 文档级权重
        const float docWeight = 0.3f;
        // 章节级权重
        const float sectionWeight = 0.3f;
        // 分段级权重
        const float chunkWeight = 0.4f;

        // 添加分段结果（最高优先级）
        foreach (var chunk in chunkResults.Take(request.TopK))
        {
            if (!merged.ContainsKey(chunk.ChunkId))
            {
                merged[chunk.ChunkId] = chunk;
            }
        }

        // 根据文档和章节分数调整
        foreach (var doc in docResults)
        {
            // 提升属于高分文档的分段
            var relatedChunks = chunkResults.Where(c => c.FileId == doc.FileId);
            foreach (var chunk in relatedChunks)
            {
                if (merged.TryGetValue(chunk.ChunkId, out var existing))
                {
                    existing.Score = existing.Score * chunkWeight + doc.Score * docWeight;
                }
            }
        }

        // 根据章节分数调整
        foreach (var section in sectionResults)
        {
            // 提升属于高分章节的分段
            var relatedChunks = chunkResults.Where(c => 
                c.FileId == section.FileId && 
                c.HeadingPath?.StartsWith(section.HeadingPath ?? "") == true);

            foreach (var chunk in relatedChunks)
            {
                if (merged.TryGetValue(chunk.ChunkId, out var existing))
                {
                    existing.Score += section.Score * sectionWeight;
                }
            }
        }

        return merged.Values
            .OrderByDescending(r => r.Score)
            .Take(request.TopK)
            .ToList();
    }

    /// <summary>
    /// 构建上下文
    /// </summary>
    private string BuildContext(List<SearchResult> results, int contextWindow)
    {
        var contextParts = new List<string>();

        foreach (var result in results)
        {
            var part = $"【{result.FilePath}";
            if (!string.IsNullOrEmpty(result.HeadingPath))
            {
                part += $" > {result.HeadingPath}";
            }
            part += "】\n";
            part += result.Content;
            contextParts.Add(part);
        }

        return string.Join("\n\n---\n\n", contextParts);
    }

    /// <summary>
    /// 构建 Prompt
    /// </summary>
    private string BuildPrompt(string query, string context)
    {
        return $@"基于以下上下文回答问题：

{context}

问题：{query}

请基于上下文内容回答，如果上下文中没有相关信息，请明确说明。";
    }

    /// <summary>
    /// 生成建议
    /// </summary>
    private string? GenerateSuggestion(AIQueryResponse response)
    {
        if (response.HasMore)
        {
            return "还有更多相关结果，可以尝试更精确的查询或使用 Deep 模式获取更多内容。";
        }

        if (response.Chunks.Count == 0)
        {
            return "未找到相关内容，请尝试不同的关键词或检查索引是否完整。";
        }

        if (response.Stats.EstimatedTokens < 500)
        {
            return "结果较少，可以尝试扩展查询词或使用 Deep 模式。";
        }

        return null;
    }

    /// <summary>
    /// 深入查询 - 获取更多上下文
    /// </summary>
    public async Task<DrillDownResponse> DrillDownAsync(
        DrillDownRequest request,
        CancellationToken cancellationToken = default)
    {
        var expandedChunks = new List<ChunkResult>();

        foreach (var refId in request.RefIds)
        {
            // 解析 refId 获取 chunkId
            // 简化实现：假设 refId 就是 chunkId
            var chunk = await _vectorStore.GetChunkAsync(refId, cancellationToken);
            if (chunk == null) continue;

            // 获取相邻分段
            var adjacentChunks = await GetAdjacentChunksAsync(chunk, request.ExpandContext, cancellationToken);

            foreach (var adjChunk in adjacentChunks)
            {
                var file = await _vectorStore.GetFileAsync(adjChunk.FileId, cancellationToken);
                if (file == null) continue;

                expandedChunks.Add(new ChunkResult
                {
                    RefId = adjChunk.Id,
                    FileId = adjChunk.FileId,
                    FilePath = file.Path,
                    Title = file.Title,
                    Content = adjChunk.Content,
                    Score = 1.0f,
                    StartLine = adjChunk.StartLine,
                    EndLine = adjChunk.EndLine,
                    HeadingPath = adjChunk.HeadingPath,
                    ObsidianLink = _linkGenerator.GenerateLink(file.Path, adjChunk.StartLine, adjChunk.EndLine)
                });
            }
        }

        // 构建完整上下文
        var fullContext = string.Join("\n\n", expandedChunks.Select(c => c.Content));

        return new DrillDownResponse
        {
            ExpandedChunks = expandedChunks,
            FullContext = fullContext
        };
    }

    /// <summary>
    /// 获取相邻分段
    /// </summary>
    private async Task<List<ChunkRecord>> GetAdjacentChunksAsync(
        ChunkRecord chunk,
        int expandContext,
        CancellationToken cancellationToken)
    {
        var chunks = (await _vectorStore.GetAdjacentChunksByFileAsync(
            chunk.FileId,
            chunk.Id,
            expandContext,
            cancellationToken)).ToList();

        if (chunks.Count > 0)
        {
            return chunks.OrderBy(c => c.StartLine).ToList();
        }

        return new List<ChunkRecord> { chunk };
    }
}
