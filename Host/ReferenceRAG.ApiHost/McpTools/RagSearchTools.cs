using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using System.ComponentModel;
using System.Text.Json;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// RAG 检索 MCP 工具。
///
/// v2 之后「知识库（每库独立 kb.db）」是检索的一等公民，因此本组工具：
/// 1. 必读参数为 query，可选 knowledgeBaseIds 显式限定检索范围（留空=全部启用库）；
/// 2. 全部走 IKnowledgeBaseSearchService，**不再默认回落到已停止写入的旧单库 vectors.db**；
/// 3. 返回结构同时给出 chunkId/documentId/knowledgeBaseId，便于 kb_drilldown 精准展开。
/// </summary>
[McpServerToolType]
public class RagSearchTools
{
    private const int MaxPreviewChars = 500;

    private readonly IServiceProvider _serviceProvider;

    public RagSearchTools(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    [McpServerTool, Description("知识库语义检索（免向量词法）。适合精确词汇、错误码、API 名。可指定 knowledgeBaseIds 限定范围。")]
    public Task<string> LexicalSearch(
        [Description("关键词或查询文本（必填）")] string query,
        [Description("返回结果数量，默认 5")] int topK = 5,
        [Description("目标知识库 ID 的 JSON 数组，如 [\"kb_a\",\"kb_b\"]。留空表示检索所有已启用知识库")] string? knowledgeBaseIds = null)
        => SearchAsync(nameof(LexicalSearch), query, topK, knowledgeBaseIds, useLexical: true);

    [McpServerTool, Description("知识库语义检索（向量召回）。适合问答与信息检索。可指定 knowledgeBaseIds 限定范围。")]
    public Task<string> SemanticSearch(
        [Description("搜索查询文本（必填），建议补充同义词与中英文术语")] string query,
        [Description("返回结果数量，默认 5，建议 1-20")] int topK = 5,
        [Description("目标知识库 ID 的 JSON 数组，如 [\"kb_a\",\"kb_b\"]。留空表示检索所有已启用知识库")] string? knowledgeBaseIds = null)
        => SearchAsync(nameof(SemanticSearch), query, topK, knowledgeBaseIds, useLexical: false);

    [McpServerTool, Description("知识库混合检索（向量 + 词法 RRF 融合），兼顾精确匹配与语义理解。可指定 knowledgeBaseIds 限定范围。")]
    public Task<string> HybridSearch(
        [Description("搜索查询文本（必填）")] string query,
        [Description("返回结果数量，默认 5")] int topK = 5,
        [Description("是否只走词法通道（跳过向量），默认 false")] bool lexicalOnly = false,
        [Description("目标知识库 ID 的 JSON 数组，如 [\"kb_a\",\"kb_b\"]。留空表示检索所有已启用知识库")] string? knowledgeBaseIds = null)
        => SearchAsync(nameof(HybridSearch), query, topK, knowledgeBaseIds, useLexical: lexicalOnly);

    [McpServerTool, Description("展开某个知识库切片的完整正文（按 chunkId 或 documentId + 序号）。用于检索结果被截断时补全上下文。")]
    public async Task<string> KnowledgeBaseDrillDown(
        [Description("目标知识库 ID（必填）。可用 kb_list 获取")]
        string knowledgeBaseId,
        [Description("切片 ID（来自检索结果的 chunkId）。与 documentId 二选一")]
        string? chunkId = null,
        [Description("文档 ID（来自检索结果的 documentId）。配合 ordinal 取该文档下的第 N 个切片")]
        string? documentId = null,
        [Description("当使用 documentId 时，返回该文档的切片数量（默认 20）")] int limit = 20)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(KnowledgeBaseDrillDown), out var permErr))
            return Fail(permErr);

        if (string.IsNullOrWhiteSpace(chunkId) && string.IsNullOrWhiteSpace(documentId))
            return Fail("必须提供 chunkId 或 documentId");

        try
        {
            var kbService = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseService>();

            if (!string.IsNullOrWhiteSpace(chunkId))
            {
                var chunk = await kbService.GetChunkByIdAsync(knowledgeBaseId, chunkId);
                if (chunk == null) return Fail($"知识库 {knowledgeBaseId} 中不存在切片 {chunkId}");
                return McpResult.Ok(new
                {
                    success = true,
                    knowledgeBaseId,
                    chunk = MapChunk(chunk)
                });
            }

            var (chunks, total) = await kbService.GetChunksAsync(knowledgeBaseId, documentId, 1, limit);
            if (total == 0) return Fail($"知识库 {knowledgeBaseId} 中不存在文档 {documentId} 的切片");
            return McpResult.Ok(new
            {
                success = true,
                knowledgeBaseId,
                documentId,
                total,
                chunks = chunks.Select(MapChunk)
            });
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    [McpServerTool, Description("对候选文档进行重排，用深度语义理解重新评估每个文档与查询的相关性。")]
    public async Task<string> RerankResults(
        [Description("原始查询（必填）")] string query,
        [Description("待重排序的文本列表（必填，JSON 数组），如[\"文档1\",\"文档2\"]")] string documents,
        [Description("最终返回数量，默认 5")] int topK = 5)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(RerankResults), out var permErr))
            return Fail(permErr);

        try
        {
            var docs = JsonSerializer.Deserialize<List<string>>(documents) ?? new List<string>();
            var rerankService = scope.ServiceProvider.GetRequiredService<IRerankService>();
            var result = await rerankService.RerankBatchAsync(query, docs);

            var topResults = result.Documents
                .OrderByDescending(d => d.RelevanceScore)
                .Take(topK)
                .ToList();

            return JsonSerializer.Serialize(new
            {
                success = true,
                query,
                inputCount = docs.Count,
                outputCount = topResults.Count,
                durationMs = result.DurationMs,
                results = topResults.Select(d => new
                {
                    index = d.Index,
                    document = d.Document.Length > 300 ? d.Document[..300] + "..." : d.Document,
                    relevanceScore = d.RelevanceScore
                })
            }, PrettyOptions());
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private async Task<string> SearchAsync(
        string toolName, string query, int topK, string? knowledgeBaseIds, bool useLexical)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, toolName, out var permErr))
            return Fail(permErr);

        if (string.IsNullOrWhiteSpace(query))
            return Fail("query 不能为空");

        try
        {
            var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();
            var kbSearch = scope.ServiceProvider.GetService<IKnowledgeBaseSearchService>();
            if (kbService == null || kbSearch == null)
                return Fail("知识库服务未装配，无法执行检索（请检查宿主是否注册 AddKnowledgeBaseServices）");

            var targetIds = ParseIds(knowledgeBaseIds);
            if (targetIds.Count == 0)
            {
                var accessible = await kbService.ListKnowledgeBasesAsync();
                targetIds = accessible.Where(k => k.Enabled).Select(k => k.Id).ToList();
            }

            if (targetIds.Count == 0)
                return Fail("当前没有可检索的知识库，请先创建知识库并挂载数据源");

            var request = new CrossKnowledgeBaseSearchRequest(
                Query: query,
                TopK: Math.Clamp(topK, 1, 50),
                KnowledgeBaseIds: targetIds,
                UseLexicalTokenizer: useLexical,
                FailurePolicy: FailurePolicy.AllowPartial,
                AllowDegrade: true)
            {
                AuthorizedSourceNames = GetAllowedSources(scope.ServiceProvider)
            };

            var response = await kbSearch.SearchAsync(request);

            return McpResult.OkRaw(JsonSerializer.Serialize(new
            {
                success = true,
                tool = toolName,
                query,
                knowledgeBaseIds = targetIds,
                requestedTopK = topK,
                // 回显真正生效的上限（原先 topK=900 被静默钳到 50，调用方无从判断）
                effectiveTopK = Math.Clamp(topK, 1, 50),
                count = response.Items.Count,
                scoreKind = response.ScoreKind.ToString(),
                elapsedMs = Math.Round(response.Elapsed.TotalMilliseconds, 1),
                degraded = response.Degraded,
                degradationReason = response.DegradationReason,
                indexDegraded = response.IndexDegraded,
                successfulKbs = response.SuccessfulKnowledgeBaseIds,
                failedKbs = response.FailedKnowledgeBaseIds,
                usedRecallFloor = response.UsedRecallFloor,
                items = response.Items.Select(i => new
                {
                    knowledgeBaseId = i.KnowledgeBaseId,
                    chunkId = i.ChunkId,
                    documentId = i.DocumentId,
                    sourceKey = i.SourceKey,
                    locator = i.Locator,
                    text = Truncate(i.Text),
                    score = Math.Round(i.Score, 4),
                    scoreKind = i.ScoreKind.ToString(),
                    startLine = i.StartLine,
                    endLine = i.EndLine,
                    headingPath = i.HeadingPath,
                    coverage = i.Coverage,
                    isFloor = i.IsFloor
                })
            }, PrettyOptions()));
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static object MapChunk(KnowledgeBaseChunkRecord chunk) => new
    {
        chunkId = chunk.Id,
        documentId = chunk.DocumentId,
        documentSourceKey = chunk.DocumentSourceKey,
        ordinal = chunk.Ordinal,
        text = chunk.Text,
        tokenCount = chunk.TokenCount,
        hasVector = chunk.HasVector,
        enabled = chunk.Enabled,
        startLine = chunk.StartLine,
        endLine = chunk.EndLine,
        headingPath = chunk.HeadingPath
    };

    private static List<string> ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json);
            return parsed?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
        }
        catch
        {
            // 容错：也接受逗号分隔
            return json.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
    }

    private static string Truncate(string text) =>
        text.Length <= MaxPreviewChars ? text : text[..MaxPreviewChars] + "...";

    private static IReadOnlyList<string>? GetAllowedSources(IServiceProvider sp)
    {
        var context = sp.GetService<IHttpContextAccessor>()?.HttpContext;
        return ApiKeyPermissionMatrix.GetEffectiveAllowedSources(context?.Items["CurrentApiKey"] as ApiKeyEntity);
    }

    private static JsonSerializerOptions PrettyOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Fail(string? error) => McpResult.Fail(error);
}
