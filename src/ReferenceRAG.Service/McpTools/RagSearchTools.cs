using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// RAG 搜索相关工具
/// </summary>
[McpServerToolType]
public class RagSearchTools
{
    private readonly IServiceProvider _serviceProvider;

    public RagSearchTools(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 执行语义搜索，使用向量相似度在知识库中查找相关内容。
    /// 当用户提出问题或需要从知识库获取信息时使用此工具。
    /// </summary>
    /// <param name="query">搜索查询文本（必填）。使用自然语言描述要查找的信息，如"如何使用API认证"</param>
    /// <param name="topK">返回结果数量，默认5。数值越大返回越多结果，建议范围1-20</param>
    /// <param name="sources">数据源名称列表（可选），用于限定只在特定知识库中搜索，如["VNote","Note"]</param>
    [McpServerTool, Description("在知识库中执行语义搜索，通过自然语言查询找到相关文档片段。适用于问答、信息检索、事实查询等场景。")]
    public async Task<string> SemanticSearch(
        [Description("搜索查询文本（必填）。使用完整的问题或关键词，如\"Obsidian如何配置插件\"、\"Python异步编程\"")] string query,
        [Description("返回结果数量，默认5。建议1-10，数值越大结果越多但处理越慢")] int topK = 5,
        [Description("数据源名称列表（可选），如[\"VNote\",\"Note\"]。空值表示搜索所有数据源")] string? sources = null)
    {
        using var scope = _serviceProvider.CreateScope();
        var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();

        try
        {
            var request = new AIQueryRequest
            {
                Query = query,
                Mode = QueryMode.Standard,
                TopK = topK,
                EnableRerank = false
            };

            if (!string.IsNullOrEmpty(sources))
            {
                request.Sources = JsonSerializer.Deserialize<List<string>>(sources);
            }

            var response = await searchService.SearchAsync(request);

            return JsonSerializer.Serialize(new
            {
                success = true,
                query = query,
                count = response.Chunks.Count,
                stats = response.Stats,
                chunks = response.Chunks.Select(c => new
                {
                    refId = c.RefId,
                    filePath = c.FilePath,
                    title = c.Title,
                    content = c.Content.Length > 500 ? c.Content.Substring(0, 500) + "..." : c.Content,
                    score = c.Score,
                    source = c.Source,
                    obsidianLink = c.ObsidianLink
                })
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                query = query
            });
        }
    }

    /// <summary>
    /// 执行混合搜索，结合向量相似度和关键词匹配，获得更准确的搜索结果。
    /// 当需要精确关键词匹配同时又想利用语义理解时使用。
    /// </summary>
    /// <param name="query">搜索查询文本（必填）</param>
    /// <param name="topK">返回结果数量，默认5</param>
    /// <param name="k1">BM25 k1参数，控制词频影响程度，默认1.5</param>
    /// <param name="b">BM25 b参数，控制文档长度归一化，默认0.75</param>
    /// <param name="enableRerank">是否启用重排，默认false。重排可提高相关性但会增加延迟</param>
    [McpServerTool, Description("混合搜索结合向量语义理解和关键词匹配。当需要同时精确匹配某些词汇又需要语义理解时使用，如查找特定API名称的同时理解其用途。")]
    public async Task<string> HybridSearch(
        [Description("搜索查询文本（必填）")] string query,
        [Description("返回结果数量，默认5")] int topK = 5,
        [Description("BM25词频参数k1，默认1.5。值越大词频影响越大")] float k1 = 1.5f,
        [Description("BM25长度归一化参数b，默认0.75")] float b = 0.75f,
        [Description("是否启用重排提升相关性，默认false。重排会消耗更多资源但结果更准确")] bool enableRerank = false)
    {
        using var scope = _serviceProvider.CreateScope();
        var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();

        try
        {
            var request = new AIQueryRequest
            {
                Query = query,
                Mode = QueryMode.Hybrid,
                TopK = topK,
                K1 = k1,
                B = b,
                EnableRerank = enableRerank
            };

            var response = await searchService.SearchAsync(request);

            return JsonSerializer.Serialize(new
            {
                success = true,
                query = query,
                count = response.Chunks.Count,
                mode = response.Mode.ToString(),
                rerankApplied = response.RerankApplied,
                stats = response.Stats,
                chunks = response.Chunks.Select(c => new
                {
                    refId = c.RefId,
                    filePath = c.FilePath,
                    title = c.Title,
                    content = c.Content.Length > 500 ? c.Content.Substring(0, 500) + "..." : c.Content,
                    score = c.Score,
                    bm25Score = c.BM25Score,
                    embeddingScore = c.EmbeddingScore,
                    rerankScore = c.RerankScore,
                    source = c.Source
                })
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                query = query
            });
        }
    }

    /// <summary>
    /// 对已有的搜索结果或候选文档进行重排序，提高相关性。
    /// 当有大量候选文档需要筛选时使用，可显著提升结果质量。
    /// </summary>
    /// <param name="query">原始查询问题（必填）</param>
    /// <param name="documents">待重排序的文档列表（必填，JSON数组格式），每个元素是一段文本</param>
    /// <param name="topK">返回数量，默认5</param>
    [McpServerTool, Description("对候选文档进行重排，用深度语义理解重新评估每个文档与查询的相关性。当搜索结果过多或初步结果不理想时用于优化。")]
    public async Task<string> RerankResults(
        [Description("原始查询（必填）。用户的问题或需求描述")] string query,
        [Description("待重排序的文档列表（必填，JSON数组），如[\"文档1内容...\",\"文档2内容...\"]")] string documents,
        [Description("最终返回数量，默认5")] int topK = 5)
    {
        using var scope = _serviceProvider.CreateScope();
        var rerankService = scope.ServiceProvider.GetRequiredService<IRerankService>();

        try
        {
            var docs = JsonSerializer.Deserialize<List<string>>(documents) ?? new List<string>();
            var result = await rerankService.RerankBatchAsync(query, docs);

            var topResults = result.Documents
                .OrderByDescending(d => d.RelevanceScore)
                .Take(topK)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                success = true,
                query = query,
                inputCount = docs.Count,
                outputCount = topResults.Count,
                durationMs = result.DurationMs,
                results = topResults.Select(d => new
                {
                    index = d.Index,
                    document = d.Document.Length > 300 ? d.Document.Substring(0, 300) + "..." : d.Document,
                    relevanceScore = d.RelevanceScore
                })
            }, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                query = query
            });
        }
    }
}
