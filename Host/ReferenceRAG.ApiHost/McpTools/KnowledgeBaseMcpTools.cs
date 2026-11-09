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
    [McpServerTool, Description("获取当前系统中已注册的所有物理隔离知识库列表，包含知识库ID、名称与配置代次")]
    public async Task<string> KnowledgeBaseList()
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(KnowledgeBaseList), out var error))
            return error ?? JsonSerializer.Serialize(new { success = false, error = "权限检查失败" });
        try
        {
            var allowed = GetAllowedSources(scope.ServiceProvider);
            var kbs = allowed == null
                ? await scope.ServiceProvider.GetRequiredService<IKnowledgeBaseService>().ListKnowledgeBasesAsync()
                : await scope.ServiceProvider.GetRequiredService<IKnowledgeBaseSearchService>().ListAccessibleKnowledgeBasesAsync(allowed);
            return JsonSerializer.Serialize(new
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
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    /// <summary>
    /// 在指定的多个隔离知识库中执行跨库多路召回与融合检索
    /// </summary>
    [McpServerTool, Description("在指定的多个物理隔离知识库中执行跨库多路召回与 RRF 融合检索。支持多库扇出与容错")]
    public async Task<string> KnowledgeBaseSearch(
        [Description("检索查询文本（必填）。使用完整的问题或查询短语")] string query,
        [Description("目标知识库 ID 列表（可选，JSON 字符串如 [\"kb_1\",\"kb_2\"]）。若留空则自动查询所有已启用的知识库")] string? knowledgeBaseIds = null,
        [Description("返回候选条数，默认 5")] int topK = 5,
        [Description("使用免向量词法检索")] bool useLexicalTokenizer = false)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(KnowledgeBaseSearch), out var error))
            return error ?? JsonSerializer.Serialize(new { success = false, error = "权限检查失败" });
        var kbService = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseService>();
        var searchService = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseSearchService>();

        try
        {
            List<string> targetIds;
            if (!string.IsNullOrWhiteSpace(knowledgeBaseIds))
            {
                targetIds = JsonSerializer.Deserialize<List<string>>(knowledgeBaseIds) ?? new List<string>();
            }
            else
            {
                var allKbs = await kbService.ListKnowledgeBasesAsync();
                targetIds = allKbs.Where(k => k.Enabled).Select(k => k.Id).ToList();
            }

            if (targetIds.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "没有可供检索的有效知识库，请先创建或指定知识库 ID"
                });
            }

            var request = new CrossKnowledgeBaseSearchRequest(
                Query: query,
                KnowledgeBaseIds: targetIds,
                TopK: topK,
                FailurePolicy: FailurePolicy.AllowPartial,
                UseLexicalTokenizer: useLexicalTokenizer
            ) { AuthorizedSourceNames = GetAllowedSources(scope.ServiceProvider) };

            var response = await searchService.SearchAsync(request);

            return JsonSerializer.Serialize(new
            {
                success = true,
                query = query,
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
                    text = item.Text,
                    score = item.Score,
                    scoreKind = item.ScoreKind.ToString(),
                    documentVersion = item.DocumentVersion,
                    connectorId = item.ConnectorId
                })
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                query = query,
                error = ex.Message
            });
        }
    }

    private static IReadOnlyList<string>? GetAllowedSources(IServiceProvider serviceProvider)
    {
        var context = serviceProvider.GetService<IHttpContextAccessor>()?.HttpContext;
        return ApiKeyPermissionMatrix.GetEffectiveAllowedSources(context?.Items["CurrentApiKey"] as ApiKeyEntity);
    }
}
