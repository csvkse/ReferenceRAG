using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 隔离知识库 MCP 协议工具套件（支持显式多知识库检索与资源发现）
/// </summary>
[McpServerToolType]
public class KnowledgeBaseMcpTools
{
    private readonly IServiceProvider _serviceProvider;

    public KnowledgeBaseMcpTools(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 列出所有可用的物理隔离知识库
    /// </summary>
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(KnowledgeBaseListResult))]
    [Description("列出系统中已注册的知识库（ID、名称、配置代次、嵌入规格）。需要知识库 ID 来限定检索范围时使用。")]
    public async Task<string> KnowledgeBaseList()
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(KnowledgeBaseList), out var error))
            return error ?? McpResult.Fail("权限检查失败", McpErrorCodes.Forbidden);
        try
        {
            var allowed = GetAllowedSources(scope.ServiceProvider);
            var kbs = allowed == null
                ? await scope.ServiceProvider.GetRequiredService<IKnowledgeBaseService>().ListKnowledgeBasesAsync()
                : await scope.ServiceProvider.GetRequiredService<IKnowledgeBaseSearchService>().ListAccessibleKnowledgeBasesAsync(allowed);
            return McpResult.Ok(new
            {
                success = true,
                count = kbs.Count,
                knowledgeBases = kbs.Select(k => new
                {
                    id = k.Id,
                    name = k.Name,
                    description = k.Description,
                    enabled = k.Enabled,
                    desiredConfigRevision = k.DesiredConfigRevision,
                    effectiveConfigRevision = k.EffectiveConfigRevision,
                    embeddingSpecId = k.EmbeddingSpecId,
                    updatedAt = k.UpdatedAt
                })
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    /// <summary>
    /// 在指定的多个隔离知识库中执行跨库多路召回与融合检索
    /// </summary>
    [McpServerTool(UseStructuredContent = true, OutputSchemaType = typeof(KnowledgeBaseSearchResult))]
    [Description("在指定知识库中执行跨库多路召回与 RRF 融合检索。已确定库范围时使用；不确定用 semantic_search / hybrid_search。")]
    public async Task<string> KnowledgeBaseSearch(
        [Description("检索查询文本（必填）。使用完整的问题或查询短语")] string query,
        [Description("目标知识库 ID 列表（可选），须为 JSON 数组字符串，形如 [\"kb_1\",\"kb_2\"]；留空则查全部已启用知识库")] string? knowledgeBaseIds = null,
        [Description("返回候选条数，默认 5，上限 50（超出按 50 处理）")] int topK = 5,
        [Description("使用免向量词法检索")] bool useLexicalTokenizer = false)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(KnowledgeBaseSearch), out var error))
            return error ?? McpResult.Fail("权限检查失败", McpErrorCodes.Forbidden);
        var kbService = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseService>();
        var searchService = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseSearchService>();

        try
        {
            if (string.IsNullOrWhiteSpace(query))
                return McpResult.Fail("query 不能为空", McpErrorCodes.InvalidArgument);

            List<string> targetIds;
            if (!string.IsNullOrWhiteSpace(knowledgeBaseIds))
            {
                try
                {
                    targetIds = JsonSerializer.Deserialize<List<string>>(knowledgeBaseIds) ?? new List<string>();
                }
                catch (JsonException)
                {
                    return McpResult.Fail(
                        $"knowledgeBaseIds 必须是 JSON 数组字符串，如 [\"kb_a\",\"kb_b\"]，收到: {knowledgeBaseIds}",
                        McpErrorCodes.InvalidArgument);
                }
            }
            else
            {
                var allKbs = await kbService.ListKnowledgeBasesAsync();
                targetIds = allKbs.Where(k => k.Enabled).Select(k => k.Id).ToList();
            }

            if (targetIds.Count == 0)
            {
                return McpResult.Fail("没有可供检索的有效知识库，请先创建或指定知识库 ID", McpErrorCodes.NoKnowledgeBase);
            }

            // 与另三个检索工具一致：topK 硬夹取到 [1,50]，并回显生效值。
            // 此前 topK=900 会返回 900 条 / 2.37MB，单次调用即可打爆模型上下文。
            var effectiveTopK = Math.Clamp(topK, 1, 50);

            var request = new CrossKnowledgeBaseSearchRequest(
                Query: query,
                KnowledgeBaseIds: targetIds,
                TopK: effectiveTopK,
                FailurePolicy: FailurePolicy.AllowPartial,
                UseLexicalTokenizer: useLexicalTokenizer
            ) { AuthorizedSourceNames = GetAllowedSources(scope.ServiceProvider) };

            var response = await searchService.SearchAsync(request);

            return McpResult.OkBounded(new
            {
                success = true,
                query = query,
                requestedTopK = topK,
                effectiveTopK,
                count = response.Items.Count,
                scoreKind = response.ScoreKind.ToString(),
                elapsedMs = response.Elapsed.TotalMilliseconds,
                successfulKbs = response.SuccessfulKnowledgeBaseIds,
                failedKbs = response.FailedKnowledgeBaseIds,
                items = response.Items.Select(item => new
                {
                    chunkId = item.ChunkId,
                    documentId = item.DocumentId,
                    sourceKey = item.SourceKey,
                    // 切片正文按 500 字截断：完整正文用 knowledge_base_drill_down 展开。
                    text = item.Text.Length <= 500 ? item.Text : item.Text[..500] + "...",
                    score = item.Score,
                    scoreKind = item.ScoreKind.ToString(),
                    documentVersion = item.DocumentVersion,
                    connectorId = item.ConnectorId
                })
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    private static IReadOnlyList<string>? GetAllowedSources(IServiceProvider serviceProvider)
    {
        var context = serviceProvider.GetService<IHttpContextAccessor>()?.HttpContext;
        return ApiKeyPermissionMatrix.GetEffectiveAllowedSources(context?.Items["CurrentApiKey"] as ApiKeyEntity);
    }
}
