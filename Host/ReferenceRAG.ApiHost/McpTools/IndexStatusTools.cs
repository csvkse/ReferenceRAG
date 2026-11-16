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

    /// <summary>
    /// 获取当前索引状态，包括向量索引和BM25索引的统计信息。
    /// 在需要了解知识库索引情况时使用，如确认索引是否已完成。
    /// </summary>
    [McpServerTool, Description("获取当前索引状态（按物理知识库逐库统计：分块数/文档数/向量数）。用于确认知识库索引是否完成或诊断检索问题。")]
    public async Task<string> GetIndexStatus()
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GetIndexStatus), out var permErr))
        {
            return permErr!;
        }

        // v2 起索引统计的事实源是各库 kb.db；旧 vectors.db 已停止写入，
        // 继续读它会返回过期快照（真机实测：25 个工具里唯独这里还报 940）。
        var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();
        var kbStorage = scope.ServiceProvider.GetService<IKnowledgeBaseStorageManager>();
        if (kbService != null && kbStorage != null)
        {
            try
            {
                var kbs = await kbService.ListKnowledgeBasesAsync();
                var perKb = new List<object>();
                int totalChunks = 0, totalVectors = 0, totalDocuments = 0;
                foreach (var kb in kbs.Where(k => k.Enabled))
                {
                    if (!await kbStorage.ExistsAsync(kb.Id)) continue;
                    await using var lease = await kbStorage.AcquireLeaseAsync(kb.Id, StorageAccess.ReadOnly);
                    var lexical = await lease.Store.GetLexicalStatsAsync();
                    var manifest = await lease.Store.GetManifestAsync();
                    totalChunks += lexical.TotalChunks;
                    totalDocuments += lexical.TotalDocuments;
                    totalVectors += manifest.ActiveVectorCount;
                    perKb.Add(new
                    {
                        knowledgeBaseId = kb.Id,
                        name = kb.Name,
                        chunkCount = lexical.TotalChunks,
                        documentCount = lexical.TotalDocuments,
                        vectorCount = manifest.ActiveVectorCount,
                        vocabularySize = lexical.TotalVocabularySize,
                        embeddingSpecId = kb.EmbeddingSpecId,
                        status = lexical.TotalChunks > 0 ? "indexed" : "empty"
                    });
                }

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    knowledgeBaseCount = perKb.Count,
                    totalChunks,
                    totalDocuments,
                    totalVectors,
                    knowledgeBases = perKb,
                    lastUpdated = DateTime.UtcNow.ToString("O")
                }, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }

        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var bm25Store = scope.ServiceProvider.GetRequiredService<IBM25Store>();

        try
        {
            var vectorStats = await vectorStore.GetVectorStatsAsync();
            var totalVectors = vectorStats.Sum(s => s.VectorCount);

            var bm25Stats = await bm25Store.GetStatsAsync();

            return JsonSerializer.Serialize(new
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
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// 获取指定文档片段的详细信息。
    /// 在需要查看某个具体文档内容或调试检索结果时使用。
    /// </summary>
    /// <param name="chunkId">文档片段ID（Chunk ID）</param>
    [McpServerTool, Description("获取指定文档分片的详细信息（内容、行号、所属文件）。传 knowledgeBaseId 时读物理知识库切片；否则读旧索引。")]
    public async Task<string> GetDocumentInfo(
        [Description("文档分片 ID（必填）。知识库结果来自 chunkId，旧库结果来自 refId")] string chunkId,
        [Description("目标知识库 ID（可选）。来自 knowledge_base_search 的 chunkId 必须传它才能定位")] string? knowledgeBaseId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GetDocumentInfo), out var permErr))
        {
            return permErr!;
        }

        var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();
        if (!string.IsNullOrWhiteSpace(knowledgeBaseId) && kbService != null)
        {
            try
            {
                var kbChunk = await kbService.GetChunkByIdAsync(knowledgeBaseId, chunkId);
                if (kbChunk == null)
                    return JsonSerializer.Serialize(new { success = false, error = $"知识库 {knowledgeBaseId} 中不存在切片 {chunkId}" });
                return JsonSerializer.Serialize(new
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
                }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }

        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();

        try
        {
            var chunk = await vectorStore.GetChunkAsync(chunkId);
            if (chunk == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Document '{chunkId}' not found"
                });
            }

            var file = await vectorStore.GetFileAsync(chunk.FileId);

            return JsonSerializer.Serialize(new
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
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// 列出索引中的文件。
    /// 在需要浏览知识库中有哪些文件时使用。
    /// </summary>
    /// <param name="sourceId">数据源名称（可选），用于过滤特定数据源的文件</param>
    /// <param name="limit">返回数量限制，默认20</param>
    [McpServerTool, Description("列出知识库中已索引的文件（文档 ID、路径、切片数）。传 knowledgeBaseId 读物理知识库；否则读数据源过滤后的旧索引。")]
    public async Task<string> ListFiles(
        [Description("目标知识库 ID 的 JSON 数组，如 [\"kb_a\"]（可选）。留空表示全部已启用知识库")] string? knowledgeBaseIds = null,
        [Description("旧索引的数据源名称（可选）。仅当未提供 knowledgeBaseIds 时生效")] string? sourceId = null,
        [Description("返回文件数量限制，默认20")] int limit = 20)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(ListFiles), out var permErr))
        {
            return permErr!;
        }

        var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();
        if (kbService != null)
        {
            try
            {
                var requested = string.IsNullOrWhiteSpace(knowledgeBaseIds)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(knowledgeBaseIds) ?? new List<string>();
                var kbs = await kbService.ListKnowledgeBasesAsync();
                var targetKbs = requested.Count > 0
                    ? kbs.Where(k => requested.Contains(k.Id, StringComparer.OrdinalIgnoreCase)).ToList()
                    : kbs.Where(k => k.Enabled).ToList();

                var rows = new List<object>();
                foreach (var kb in targetKbs)
                {
                    var (docs, _) = await kbService.GetDocumentsAsync(kb.Id, page: 1, pageSize: limit, search: sourceId);
                    foreach (var d in docs)
                    {
                        rows.Add(new
                        {
                            knowledgeBaseId = kb.Id,
                            knowledgeBaseName = kb.Name,
                            documentId = d.Id,
                            sourceKey = d.SourceKey,
                            locator = d.Locator,
                            chunkCount = d.ChunkCount,
                            updatedAt = d.UpdatedAt
                        });
                    }
                    if (rows.Count >= limit) break;
                }

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    count = Math.Min(rows.Count, limit),
                    files = rows.Take(limit)
                }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }

        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();

        try
        {
            var allFiles = await vectorStore.GetAllFilesAsync();

            var files = string.IsNullOrEmpty(sourceId)
                ? allFiles
                : allFiles.Where(f => f.Source?.Equals(sourceId, StringComparison.OrdinalIgnoreCase) == true);

            var fileList = files.Take(limit).ToList();

            return JsonSerializer.Serialize(new
            {
                success = true,
                count = fileList.Count,
                files = fileList.Select(f => new
                {
                    id = f.Id,
                    path = f.Path,
                    title = f.Title,
                    source = f.Source
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
                error = ex.Message
            });
        }
    }
}
