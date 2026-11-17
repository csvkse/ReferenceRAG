using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.Security;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    [McpServerTool(Title = "知识库检索（统一入口）")]
    [Description("知识库高精度检索。支持通用语义理解、精准代码/关键词匹配及混合检索。默认 auto 模式智能决策。")]
    public Task<string> KnowledgeSearch(
        [Description("检索查询文本（必填），支持自然语言问题、精确关键词、代码符号或错误码")] string query,
        [Description("检索模式：auto (默认，自动决策)、hybrid (混合检索)、semantic (语义检索)、lexical (精确词法)")] string mode = "auto",
        [Description("返回结果数量，默认 5，建议 1-20，上限 50")] int topK = 5,
        [Description("目标知识库 ID 列表，须为 JSON 数组字符串，如 [\"kb_a\",\"kb_b\"]。留空表示检索所有授权知识库")] string? knowledgeBaseIds = null)
        => SearchAsync(nameof(KnowledgeSearch), query, topK, knowledgeBaseIds, mode: mode);

    [McpServerTool(Title = "词法检索（知识库）")]
    [Description("知识库语义检索（免向量词法）。推荐使用 knowledge_search(mode='lexical')。")]
    public Task<string> LexicalSearch(
        [Description("关键词或查询文本（必填）")] string query,
        [Description("返回结果数量，默认 5，上限 50")] int topK = 5,
        [Description("目标知识库 ID 列表，须为 JSON 数组字符串，如 [\"kb_a\",\"kb_b\"]。留空表示检索所有授权知识库")] string? knowledgeBaseIds = null)
        => SearchAsync(nameof(LexicalSearch), query, topK, knowledgeBaseIds, mode: "lexical", legacyUseLexical: true);

    [McpServerTool(Title = "语义检索（向量）")]
    [Description("知识库语义检索（向量召回）。推荐使用 knowledge_search(mode='semantic')。")]
    public Task<string> SemanticSearch(
        [Description("搜索查询文本（必填），建议补充同义词与中英文术语")] string query,
        [Description("返回结果数量，默认 5，建议 1-20，上限 50")] int topK = 5,
        [Description("目标知识库 ID 列表，须为 JSON 数组字符串，如 [\"kb_a\",\"kb_b\"]。留空表示检索所有授权知识库")] string? knowledgeBaseIds = null)
        => SearchAsync(nameof(SemanticSearch), query, topK, knowledgeBaseIds, mode: "semantic", legacyUseLexical: false);

    [McpServerTool(Title = "混合检索（向量+词法）")]
    [Description("知识库混合检索（向量 + 词法 RRF 融合）。推荐使用 knowledge_search(mode='hybrid')。")]
    public Task<string> HybridSearch(
        [Description("搜索查询文本（必填）")] string query,
        [Description("返回结果数量，默认 5，上限 50")] int topK = 5,
        [Description("是否只走词法通道（跳过向量），默认 false")] bool lexicalOnly = false,
        [Description("目标知识库 ID 列表，须为 JSON 数组字符串，如 [\"kb_a\",\"kb_b\"]。留空表示检索所有授权知识库")] string? knowledgeBaseIds = null)
        => SearchAsync(nameof(HybridSearch), query, topK, knowledgeBaseIds, mode: lexicalOnly ? "lexical" : "hybrid", legacyUseLexical: lexicalOnly);

    [McpServerTool(Title = "展开切片正文")]
    [Description("展开某个知识库切片的完整正文（按 chunkId 或 documentId + 序号）。用于检索结果被截断时补全上下文。")]
    public async Task<string> KnowledgeBaseDrillDown(
        [Description("目标知识库 ID（必填）。可用 knowledge_base_list 获取")]
        string knowledgeBaseId,
        [Description("切片 ID（来自检索结果的 chunkId）。与 documentId 二选一")]
        string? chunkId = null,
        [Description("文档 ID（来自检索结果的 documentId）。配合 ordinal 取该文档下的第 N 个切片")]
        string? documentId = null,
        [Description("当使用 documentId 时返回的切片数量，默认 20，上限 50（超出按 50 处理）")] int limit = 20)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(KnowledgeBaseDrillDown), out var permErr))
            return Fail(permErr, McpErrorCodes.Forbidden);

        var currentKey = scope.ServiceProvider.GetService<IHttpContextAccessor>()?.HttpContext?.Items["CurrentApiKey"] as ApiKeyEntity;
        if (!ApiKeyPermissionMatrix.CanAccessKnowledgeBase(currentKey, knowledgeBaseId))
            return Fail($"无权访问指定的知识库 {knowledgeBaseId}", McpErrorCodes.Forbidden);

        if (string.IsNullOrWhiteSpace(chunkId) && string.IsNullOrWhiteSpace(documentId))
            return Fail("必须提供 chunkId 或 documentId", McpErrorCodes.InvalidArgument);

        try
        {
            var kbService = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseService>();

            if (!string.IsNullOrWhiteSpace(chunkId))
            {
                var chunk = await kbService.GetChunkByIdAsync(knowledgeBaseId, chunkId);
                if (chunk == null) return Fail($"知识库 {knowledgeBaseId} 中不存在切片 {chunkId}", McpErrorCodes.ChunkNotFound);
                return McpResult.Ok(new
                {
                    success = true,
                    knowledgeBaseId,
                    chunk = MapChunk(chunk)
                });
            }

            var (chunks, total) = await kbService.GetChunksAsync(knowledgeBaseId, documentId, 1, Math.Clamp(limit, 1, 50));
            if (total == 0) return Fail($"知识库 {knowledgeBaseId} 中不存在文档 {documentId} 的切片", McpErrorCodes.DocumentNotFound);
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
            return McpResult.FailException(ex);
        }
    }

    [McpServerTool(Title = "候选重排")]
    [Description("对候选文档进行重排，用深度语义理解重新评估每个文档与查询的相关性。")]
    public async Task<string> RerankResults(
        [Description("原始查询（必填）")] string query,
        [Description("待重排序的文本列表（必填，最多 100 条），须为 JSON 数组字符串，如[\"文档1\",\"文档2\"]")] string documents,
        [Description("最终返回数量，默认 5，上限 50（超出按 50 处理）")] int topK = 5)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(RerankResults), out var permErr))
            return Fail(permErr, McpErrorCodes.Forbidden);

        try
        {
            List<string> docs;
            (_, var effectiveTopK) = McpInputAdapters.ClampInt(nameof(topK), topK, 1, 50);
            try
            {
                docs = McpInputAdapters.ParseStringArray(documents, nameof(documents));
                McpInputAdapters.EnsureItemCount(nameof(documents), docs.Count, McpInputAdapters.MaxRerankDocuments);
            }
            catch (McpInputArgumentException ex)
            {
                return Fail(ex.Message, McpErrorCodes.InvalidArgument);
            }

            var rerankService = scope.ServiceProvider.GetRequiredService<IRerankService>();
            var result = await rerankService.RerankBatchAsync(query, docs);

            var topResults = result.Documents
                .OrderByDescending(d => d.RelevanceScore ?? double.NegativeInfinity)
                .Take(effectiveTopK)
                .ToList();

            // 降级时 relevanceScore 为 null 且 scoreKind=degraded：
            // 模型据此知道「这只是原顺序，不是相关性」，不会拿 0.85 之类的保底值做裁剪。
            return McpResult.OkBounded(new
            {
                success = true,
                query,
                requestedTopK = topK,
                effectiveTopK,
                inputCount = docs.Count,
                outputCount = topResults.Count,
                durationMs = result.DurationMs,
                degraded = result.Degraded,
                degradationReason = result.DegradationReason,
                scoreKind = result.ScoreKind.ToString().ToLowerInvariant(),
                results = topResults.Select(d => new
                {
                    index = d.Index,
                    document = d.Document.Length > 300 ? d.Document[..300] + "..." : d.Document,
                    relevanceScore = d.RelevanceScore,
                    scoreKind = d.ScoreKind.ToString().ToLowerInvariant()
                })
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    private async Task<string> SearchAsync(
        string toolName, string query, int topK, string? knowledgeBaseIds, string mode = "auto", bool? legacyUseLexical = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, toolName, out var permErr))
            return Fail(permErr, McpErrorCodes.Forbidden);

        if (string.IsNullOrWhiteSpace(query))
            return Fail("query 不能为空", McpErrorCodes.InvalidArgument);

        try
        {
            var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();
            var kbSearch = scope.ServiceProvider.GetService<IKnowledgeBaseSearchService>();
            if (kbService == null || kbSearch == null)
                return Fail("知识库服务未装配，无法执行检索（请检查宿主是否注册 AddKnowledgeBaseServices）", McpErrorCodes.ServiceUnavailable);

            List<string> targetIds;
            (_, var effectiveTopK) = McpInputAdapters.ClampInt(nameof(topK), topK, 1, 50);
            try
            {
                targetIds = McpInputAdapters.ParseKnowledgeBaseIds(knowledgeBaseIds);
            }
            catch (McpInputArgumentException ex)
            {
                return Fail(ex.Message, McpErrorCodes.InvalidArgument);
            }

            var currentKey = scope.ServiceProvider.GetService<IHttpContextAccessor>()?.HttpContext?.Items["CurrentApiKey"] as ApiKeyEntity;
            var allowedKbs = ApiKeyPermissionMatrix.GetEffectiveAllowedKnowledgeBases(currentKey);

            if (allowedKbs != null)
            {
                var allowedSet = new HashSet<string>(allowedKbs, StringComparer.OrdinalIgnoreCase);
                if (targetIds.Count > 0)
                {
                    targetIds = targetIds.Where(id => allowedSet.Contains(id)).ToList();
                    if (targetIds.Count == 0)
                    {
                        return Fail("无权访问指定的知识库或目标库未在授权列表中", McpErrorCodes.Forbidden);
                    }
                }
                else
                {
                    targetIds = allowedKbs.ToList();
                }
            }
            else if (targetIds.Count == 0)
            {
                var accessible = await kbService.ListKnowledgeBasesAsync();
                targetIds = accessible.Where(k => k.Enabled).Select(k => k.Id).ToList();
            }

            if (targetIds.Count == 0)
                return Fail("当前没有可检索的知识库，请先创建知识库并挂载数据源", McpErrorCodes.NoKnowledgeBase);

            var useLexical = legacyUseLexical ?? ResolveUseLexical(mode, query);

            var request = new CrossKnowledgeBaseSearchRequest(
                Query: query,
                TopK: effectiveTopK,
                KnowledgeBaseIds: targetIds,
                UseLexicalTokenizer: useLexical,
                FailurePolicy: FailurePolicy.AllowPartial,
                AllowDegrade: true)
            {
                AuthorizedSourceNames = GetAllowedSources(scope.ServiceProvider)
            };

            var response = await kbSearch.SearchAsync(request);

            return McpResult.OkBounded(new
            {
                success = true,
                tool = toolName,
                mode,
                query,
                knowledgeBaseIds = targetIds,
                requestedTopK = topK,
                // 回显真正生效的上限（原先 topK=900 被静默钳到 50，调用方无从判断）
                effectiveTopK,
                count = response.Items.Count,
                scoreKind = response.ScoreKind.ToString(),
                elapsedMs = Math.Round(response.Elapsed.TotalMilliseconds, 1),
                degraded = response.Degraded,
                degradationReason = response.DegradationReason,
                indexDegraded = response.IndexDegraded,
                // 通道事实：融合把 ScoreKind 统一成 RRF，只有这里能说清「向量还是关键词命中」。
                kbChannels = response.KbChannels,
                noMatch = response.NoMatch,
                noMatchReason = response.NoMatchReason,
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
                    channels = ChannelsOf(i.Channels),
                    startLine = i.StartLine,
                    endLine = i.EndLine,
                    headingPath = i.HeadingPath,
                    coverage = i.Coverage,
                    isFloor = i.IsFloor
                })
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    private static bool ResolveUseLexical(string? mode, string query)
    {
        if (string.Equals(mode, "lexical", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(mode, "semantic", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(mode, "hybrid", StringComparison.OrdinalIgnoreCase)) return false;

        // auto 智能模式：精确代码标识符、错误码、引号包裹走词法，其余走混合融合
        return IsCodeOrExactToken(query);
    }

    private static bool IsCodeOrExactToken(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        var trimmed = query.Trim();
        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) || (trimmed.StartsWith('“') && trimmed.EndsWith('”')))
            return true;
        if (Regex.IsMatch(trimmed, @"^[A-Za-z0-9_]{2,40}$") && (trimmed.Contains('_') || trimmed.Any(char.IsUpper)))
            return true;
        if (Regex.IsMatch(trimmed, @"(?i)\b(error|err|code|status|cs|e)\s*[:#\-]?\s*\d{3,5}\b"))
            return true;
        return false;
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

    private static IReadOnlyList<string> ChannelsOf(RecallChannel channels)
    {
        var list = new List<string>();
        if (channels.HasFlag(RecallChannel.Lexical)) list.Add("lexical");
        if (channels.HasFlag(RecallChannel.Vector)) list.Add("vector");
        return list;
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

    private static string Fail(string? error, string errorCode) => McpResult.Fail(error, errorCode);
}
