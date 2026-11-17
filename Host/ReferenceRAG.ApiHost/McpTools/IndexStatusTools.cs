using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 索引状态查询工具（归属 index 业务模块）
/// </summary>
[McpServerToolType]
public class IndexStatusTools
{
    private readonly IServiceProvider _serviceProvider;

    public IndexStatusTools(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    private static IMcpStatsCache GetStatsCache(IServiceProvider sp) =>
        sp.GetService<IMcpStatsCache>() ?? new McpStatsCache();

    /// <summary>
    /// 获取当前索引状态，包括向量索引和BM25索引的统计信息。
    /// 在需要了解知识库索引情况时使用，如确认索引是否已完成。
    /// </summary>
    [McpServerTool(Title = "索引状态", UseStructuredContent = true, OutputSchemaType = typeof(IndexStatusResult))]
    [Description("按物理知识库逐库统计分块数/文档数/向量数。确认索引是否建完、或检索为空时排查索引问题使用。")]
    public async Task<string> GetIndexStatus()
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GetIndexStatus), out var permErr))
        {
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);
        }

        // v2 起索引统计的事实源是各库 kb.db；旧 vectors.db 已停止写入，
        // 继续读它会返回过期快照（真机实测：25 个工具里唯独这里还报 940）。
        var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();
        var kbStorage = scope.ServiceProvider.GetService<IKnowledgeBaseStorageManager>();
        if (kbService != null && kbStorage != null)
        {
            try
            {
                // 30s TTL 缓存：逐库 COUNT(*) + AVG(LENGTH(text)) 在 237k 行语料上稳定耗时 1.2–1.5 秒。
                var stats = await GetStatsCache(scope.ServiceProvider)
                    .GetIndexStatsAsync(kbService, kbStorage);

                var perKb = new List<object>();
                int totalChunks = 0, totalVectors = 0, totalDocuments = 0;
                foreach (var kb in stats)
                {
                    totalChunks += kb.ChunkCount;
                    totalDocuments += kb.DocumentCount;
                    totalVectors += kb.VectorCount;
                    perKb.Add(new
                    {
                        knowledgeBaseId = kb.KnowledgeBaseId,
                        name = kb.Name,
                        chunkCount = kb.ChunkCount,
                        documentCount = kb.DocumentCount,
                        vectorCount = kb.VectorCount,
                        vocabularySize = kb.VocabularySize,
                        embeddingSpecId = kb.EmbeddingSpecId,
                        status = kb.ChunkCount > 0 ? "indexed" : "empty"
                    });
                }

                return McpResult.Ok(new
                {
                    success = true,
                    knowledgeBaseCount = perKb.Count,
                    totalChunks,
                    totalDocuments,
                    totalVectors,
                    knowledgeBases = perKb,
                    lastUpdated = DateTime.UtcNow.ToString("O")
                });
            }
            catch (Exception ex)
            {
                return McpResult.FailException(ex);
            }
        }

        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var bm25Store = scope.ServiceProvider.GetRequiredService<IBM25Store>();

        try
        {
            var vectorStats = await vectorStore.GetVectorStatsAsync();
            var totalVectors = vectorStats.Sum(s => s.VectorCount);

            var bm25Stats = await bm25Store.GetStatsAsync();

            return McpResult.Ok(new
            {
                success = true,
                vectorIndex = new
                {
                    totalVectors = totalVectors,
                    models = vectorStats.Select(s => new
                    {
                        modelName = s.ModelName,
                        dimension = s.Dimension,
                        vectorCount = s.VectorCount,
                        storageMB = Math.Round(s.StorageBytes / 1024.0 / 1024.0, 2)
                    }),
                    status = totalVectors > 0 ? "indexed" : "empty"
                },
                bm25Index = new
                {
                    documentCount = bm25Stats.TotalDocuments,
                    averageDocLength = Math.Round(bm25Stats.AverageDocLength, 2),
                    vocabularySize = bm25Stats.VocabularySize,
                    status = bm25Stats.TotalDocuments > 0 ? "indexed" : "empty"
                },
                lastUpdated = DateTime.UtcNow.ToString("O")
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    /// <summary>
    /// 获取指定文档片段的详细信息。
    /// 在需要查看某个具体文档内容或调试检索结果时使用。
    /// </summary>
    /// <param name="chunkId">文档片段ID（Chunk ID）</param>
    [McpServerTool(Title = "切片归属文件", UseStructuredContent = true, OutputSchemaType = typeof(DocumentInfoResult))]
    [Description("获取指定分片的详细信息（内容、行号、所属文件）。查某个 chunkId 属于哪个文件时使用；要看完整正文改用 knowledge_base_drill_down。必须同时传 knowledgeBaseId。")]
    public async Task<string> GetDocumentInfo(
        [Description("文档分片 ID（必填）。来自 knowledge_base_search / list_files 的 chunkId")] string chunkId,
        [Description("目标知识库 ID（必填）。缺失时会报 chunk_not_found，不会跨库猜测")] string? knowledgeBaseId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GetDocumentInfo), out var permErr))
        {
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);
        }

        var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();
        if (!string.IsNullOrWhiteSpace(knowledgeBaseId) && kbService != null)
        {
            try
            {
                var kbChunk = await kbService.GetChunkByIdAsync(knowledgeBaseId, chunkId);
                if (kbChunk == null)
                    return McpResult.Fail($"知识库 {knowledgeBaseId} 中不存在切片 {chunkId}", McpErrorCodes.ChunkNotFound);
                return McpResult.Ok(new
                {
                    success = true,
                    knowledgeBaseId,
                    chunk = new
                    {
                        id = kbChunk.Id,
                        documentId = kbChunk.DocumentId,
                        ordinal = kbChunk.Ordinal,
                        content = kbChunk.Text.Length > 500 ? kbChunk.Text[..500] + "..." : kbChunk.Text,
                        startLine = kbChunk.StartLine,
                        endLine = kbChunk.EndLine,
                        headingPath = kbChunk.HeadingPath
                    },
                    file = new { path = kbChunk.DocumentSourceKey }
                });
            }
            catch (Exception ex)
            {
                return McpResult.FailException(ex);
            }
        }

        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();

        try
        {
            var chunk = await vectorStore.GetChunkAsync(chunkId);
            if (chunk == null)
            {
                return McpResult.Fail($"Document '{chunkId}' not found", McpErrorCodes.DocumentNotFound);
            }

            var file = await vectorStore.GetFileAsync(chunk.FileId);

            return McpResult.Ok(new
            {
                success = true,
                chunk = new
                {
                    id = chunk.Id,
                    fileId = chunk.FileId,
                    content = chunk.Content.Length > 500 ? chunk.Content[..500] + "..." : chunk.Content,
                    startLine = chunk.StartLine,
                    endLine = chunk.EndLine,
                    headingPath = chunk.HeadingPath
                },
                file = file != null ? new
                {
                    id = file.Id,
                    path = file.Path,
                    title = file.Title,
                    source = file.Source
                } : null
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    /// <summary>
    /// 列出索引中的文件。
    /// 在需要浏览知识库中有哪些文件时使用。
    /// </summary>
    /// <param name="connectorId">连接器 ID（精确过滤），可来自 list_sources</param>
    /// <param name="pathKeyword">路径关键词（sourceKey / locator 子串，LIKE 已 ESCAPE）</param>
    /// <param name="limit">返回数量限制，默认20</param>
    [McpServerTool(Title = "已索引文件清单", UseStructuredContent = true, OutputSchemaType = typeof(ListFilesResult))]
    [Description("列出知识库中已索引的文件（文档 ID、路径、切片数）。支持按 connectorId 精确过滤，或按 pathKeyword 做路径子串过滤（可叠加）。")]
    public async Task<string> ListFiles(
        [Description("目标知识库 ID 列表，须为 JSON 数组字符串，如 [\"kb_a\"]（可选）。留空表示全部已启用知识库")] string? knowledgeBaseIds = null,
        [Description("连接器 ID（可选，精确匹配）。可由 list_sources 的 connectorId 字段获得")] string? connectorId = null,
        [Description("路径关键词（可选，sourceKey / locator 的子串匹配，% 与 _ 按字面处理）")] string? pathKeyword = null,
        [Description("返回文件数量限制，默认 20，上限 200（多库时按库合计截断，超出按 200 处理）")] int limit = 20)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(ListFiles), out var permErr))
        {
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);
        }

        var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();
        if (kbService == null)
        {
            // 知识库服务不可用时不再回落到旧索引：旧路径的 sourceId 语义与此处不同，
            // 静默换语义比直接失败更难排查。
            return McpResult.Fail("知识库服务不可用，无法列出文件", McpErrorCodes.KnowledgeBaseUnavailable);
        }

        try
        {
            (_, var effectiveLimit) = McpInputAdapters.ClampInt(nameof(limit), limit, 1, 200);

            List<string> requested;
            try
            {
                requested = McpInputAdapters.ParseKnowledgeBaseIds(knowledgeBaseIds);
            }
            catch (McpInputArgumentException ex)
            {
                return McpResult.Fail(ex.Message, McpErrorCodes.InvalidArgument);
            }
            var kbs = await kbService.ListKnowledgeBasesAsync();
            var targetKbs = requested.Count > 0
                ? kbs.Where(k => requested.Contains(k.Id, StringComparer.OrdinalIgnoreCase)).ToList()
                : kbs.Where(k => k.Enabled).ToList();

            if (requested.Count > 0 && targetKbs.Count == 0)
                return McpResult.Fail($"未找到指定知识库: {knowledgeBaseIds}", McpErrorCodes.KnowledgeBaseNotFound);

            // connectorId 必须能解析到具体连接器，否则过滤条件等同失效；
            // 直接报错而不是返回 0 条，调用方才能区分"没命中"与"参数错"。
            if (!string.IsNullOrWhiteSpace(connectorId))
            {
                var matched = false;
                foreach (var kb in targetKbs)
                {
                    if ((await kbService.GetConnectorsAsync(kb.Id))
                        .Any(c => string.Equals(c.Id, connectorId, StringComparison.OrdinalIgnoreCase)))
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                    return McpResult.Fail(
                        $"未找到连接器: {connectorId}（可由 list_sources 查看合法 connectorId）",
                        McpErrorCodes.ConnectorNotFound);
            }

            var rows = new List<object>();
            var totalMatched = 0;
            // 不提前 break：totalMatched 必须是全部目标库的合计，否则 truncated 判定会漏算。
            foreach (var kb in targetKbs)
            {
                var (docs, total) = await kbService.GetDocumentsAsync(
                    kb.Id, page: 1, pageSize: effectiveLimit, search: pathKeyword, connectorId: connectorId);
                totalMatched += total;
                if (rows.Count >= effectiveLimit) continue;
                foreach (var d in docs)
                {
                    rows.Add(new
                    {
                        knowledgeBaseId = kb.Id,
                        knowledgeBaseName = kb.Name,
                        connectorId = d.ConnectorId,
                        documentId = d.Id,
                        sourceKey = d.SourceKey,
                        locator = d.Locator,
                        chunkCount = d.ChunkCount,
                        updatedAt = d.UpdatedAt
                    });
                    if (rows.Count >= effectiveLimit) break;
                }
            }

            var files = rows.Take(effectiveLimit).ToList();
            return McpResult.OkBounded(new
            {
                success = true,
                appliedFilters = new
                {
                    connectorId = string.IsNullOrWhiteSpace(connectorId) ? null : connectorId,
                    pathKeyword = string.IsNullOrWhiteSpace(pathKeyword) ? null : pathKeyword
                },
                requestedLimit = limit,
                effectiveLimit,
                count = files.Count,
                totalMatched,
                truncated = totalMatched > files.Count,
                files
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }
}
