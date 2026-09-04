using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 索引状态查询工具
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
    [McpServerTool, Description("获取当前索引状态，包括向量索引（Vector）和全文搜索索引（BM25）的统计信息。返回向量数量、文档数量、存储大小等。用于确认知识库索引是否完成或诊断检索问题。")]
    public async Task<string> GetIndexStatus()
    {
        using var scope = _serviceProvider.CreateScope();
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
    /// 获取指定文档/片段的详细信息。
    /// 在需要查看某个具体文档内容或调试检索结果时使用。
    /// </summary>
    /// <param name="chunkId">文档片段ID（Chunk ID）</param>
    [McpServerTool, Description("获取指定文档片段的详细信息，包括内容、行号、所属文件等。用于查看具体文档片段的详情或调试检索结果。chunkId可以从搜索结果中获取。")]
    public async Task<string> GetDocumentInfo(
        [Description("文档片段ID（Chunk ID，必填）。从搜索结果的refId字段获取")] string chunkId)
    {
        using var scope = _serviceProvider.CreateScope();
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
    [McpServerTool, Description("列出索引中的文件。返回文件ID、路径、标题、所属数据源等信息。用于浏览知识库中已索引的文件列表，支持按数据源过滤。")]
    public async Task<string> ListFiles(
        [Description("数据源名称（可选），如\"VNote\"。空值表示所有数据源")] string? sourceId = null,
        [Description("返回文件数量限制，默认20")] int limit = 20)
    {
        using var scope = _serviceProvider.CreateScope();
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
