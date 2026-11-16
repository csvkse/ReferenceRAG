using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 数据源管理工具
/// </summary>
[McpServerToolType]
public class SourceManagementTools
{
    private readonly IServiceProvider _serviceProvider;

    public SourceManagementTools(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 列出所有已配置的数据源及其状态。
    /// 在管理知识库、查看数据源概览时使用。
    /// </summary>
    [McpServerTool, Description("列出所有已配置的知识库数据源及其索引统计（计数取自各知识库 kb.db 物理事实源）。包括源名称、路径、启用状态、文档数、分块数。用于了解当前知识库的覆盖范围。")]
    public async Task<string> ListSources([Description("所有物理知识库 ID 的 JSON 数组，如 [\"kb_a\",\"kb_b\"]。留空表示全部已启用知识库")] string? knowledgeBaseIds = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(ListSources), out var permErr))
        {
            return permErr!;
        }
        var config = scope.ServiceProvider.GetRequiredService<ConfigManager>().Load();
        var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();

        try
        {
            if (kbService != null)
            {
                return JsonSerializer.Serialize(await BuildKnowledgeBaseSourceViewAsync(
                    kbService, config, knowledgeBaseIds), JsonOptions);
            }

            var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
            var bm25Store = scope.ServiceProvider.GetRequiredService<IBM25Store>();

            var sources = config.Sources;
            var sourceStats = new List<object>();

            foreach (var source in sources)
            {
                var vectorCount = await GetVectorCountForSource(vectorStore, source.Name);
                var bm25Stats = await GetBm25StatsForSource(bm25Store, source.Name);

                sourceStats.Add(new
                {
                    name = string.IsNullOrEmpty(source.Name) ? Path.GetFileName(source.Path) : source.Name,
                    path = source.Path,
                    type = source.Type.ToString(),
                    enabled = source.Enabled,
                    priority = source.Priority,
                    filePatterns = source.FilePatterns,
                    tags = source.Tags,
                    stats = new
                    {
                        vectorCount = vectorCount,
                        documentCount = bm25Stats.DocumentCount,
                        totalTokens = bm25Stats.TotalTokens
                    },
                    excludeDirs = source.ExcludeDirs
                });
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                count = sources.Count,
                sources = sourceStats
            }, JsonOptions);
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
    /// 以 per-KB 连接器快照为事实源，产出「源」视图：
    /// config.Sources 中的路径保留其名称，仅存在于知识库连接器中的路径补一行投影。
    /// 与 REST /api/Sources 的计数口径一致（旧 vectors.db 快照不再参与）。
    /// </summary>
    private static async Task<object> BuildKnowledgeBaseSourceViewAsync(
        IKnowledgeBaseService kbService, ObsidianRagConfig config, string? knowledgeBaseIdsJson)
    {
        var requested = string.IsNullOrWhiteSpace(knowledgeBaseIdsJson)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(knowledgeBaseIdsJson) ?? new List<string>();

        var kbs = await kbService.ListKnowledgeBasesAsync();
        var targetKbs = requested.Count > 0
            ? kbs.Where(k => requested.Contains(k.Id, StringComparer.OrdinalIgnoreCase)).ToList()
            : kbs.Where(k => k.Enabled).ToList();

        var snapshots = await kbService.GetConnectorStatsSnapshotAsync();
        var byPath = new Dictionary<string, (int Docs, int Chunks)>(StringComparer.OrdinalIgnoreCase);
        foreach (var snap in snapshots)
        {
            if (!targetKbs.Any(k => k.Id == snap.KnowledgeBaseId)) continue;
            var key = NormalizePathKey(snap.TargetPath);
            byPath.TryGetValue(key, out var acc);
            byPath[key] = (acc.Docs + snap.DocumentCount, acc.Chunks + snap.ChunkCount);
        }

        var rows = new List<object>();
        foreach (var source in config.Sources)
        {
            byPath.TryGetValue(NormalizePathKey(source.Path), out var stat);
            rows.Add(new
            {
                name = string.IsNullOrEmpty(source.Name) ? Path.GetFileName(source.Path) : source.Name,
                path = source.Path,
                type = source.Type.ToString(),
                enabled = source.Enabled,
                origin = "SourceConfig",
                fileCount = stat.Docs,
                chunkCount = stat.Chunks
            });
        }

        // 仅由知识库连接器投影出来的路径（config.Sources 中没有）也要可见，否则 MCP 客户端看不到真实语料
        var knownPaths = config.Sources.Select(s => NormalizePathKey(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var connectorRows = new List<object>();
        foreach (var kb in targetKbs)
        {
            foreach (var conn in await kbService.GetConnectorsAsync(kb.Id))
            {
                var key = NormalizePathKey(conn.TargetPath);
                if (!knownPaths.Add(key)) continue;
                byPath.TryGetValue(key, out var stat);
                connectorRows.Add(new
                {
                    name = $"{kb.Name} - {Path.GetFileName(conn.TargetPath)}",
                    path = conn.TargetPath,
                    type = "KnowledgeBaseConnector",
                    enabled = conn.Enabled,
                    origin = "KnowledgeBaseOnly",
                    knowledgeBaseId = kb.Id,
                    connectorId = conn.Id,
                    fileCount = stat.Docs,
                    chunkCount = stat.Chunks
                });
            }
        }

        return new
        {
            success = true,
            knowledgeBaseCount = targetKbs.Count,
            count = rows.Count + connectorRows.Count,
            sources = rows.Concat(connectorRows).ToList()
        };
    }

    private static string NormalizePathKey(string? path) =>
        (path ?? string.Empty).TrimEnd('/', '\\').Replace('\\', '/');

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// 获取指定数据源的详细信息，包括存储统计和物理文件信息。
    /// 在需要了解某个知识库的具体情况时使用。
    /// </summary>
    /// <param name="sourceName">数据源名称或路径（必填）</param>
    [McpServerTool, Description("获取指定数据源的详细信息，包括索引统计、路径状态、物理文件数量和大小。用于诊断特定知识库的问题或了解其规模。")]
    public async Task<string> GetSourceInfo(
        [Description("数据源名称（必填），如\"VNote\"、\"Note\"，或完整路径")] string sourceName)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GetSourceInfo), out var permErr))
        {
            return permErr!;
        }
        var config = scope.ServiceProvider.GetRequiredService<ConfigManager>().Load();
        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var bm25Store = scope.ServiceProvider.GetRequiredService<IBM25Store>();

        try
        {
            var source = config.Sources.FirstOrDefault(s =>
                s.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase) ||
                s.Path.Equals(sourceName, StringComparison.OrdinalIgnoreCase));

            if (source == null)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"未找到数据源: {sourceName}"
                });
            }

            var vectorCount = await GetVectorCountForSource(vectorStore, source.Name);
            var bm25Stats = await GetBm25StatsForSource(bm25Store, source.Name);

            var pathExists = Directory.Exists(source.Path);
            var fileCount = 0;
            var totalSize = 0L;

            if (pathExists)
            {
                try
                {
                    var files = GetFilesFromSource(source);
                    fileCount = files.Count();
                    totalSize = files.Sum(f => new FileInfo(f).Length);
                }
                catch { }
            }

            return JsonSerializer.Serialize(new
            {
                success = true,
                source = new
                {
                    name = string.IsNullOrEmpty(source.Name) ? Path.GetFileName(source.Path) : source.Name,
                    path = source.Path,
                    type = source.Type.ToString(),
                    enabled = source.Enabled,
                    priority = source.Priority,
                    filePatterns = source.FilePatterns,
                    tags = source.Tags,
                    excludeDirs = source.ExcludeDirs,
                    excludeFiles = source.ExcludeFiles,
                    stats = new
                    {
                        vectorCount = vectorCount,
                        documentCount = bm25Stats.DocumentCount,
                        totalTokens = bm25Stats.TotalTokens,
                        physicalFiles = fileCount,
                        totalSizeBytes = totalSize,
                        totalSizeMB = Math.Round(totalSize / 1024.0 / 1024.0, 2)
                    },
                    pathExists = pathExists
                }
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
                sourceName = sourceName
            });
        }
    }

    /// <summary>
    /// 获取整体系统统计信息，包括配置、模型、索引状态等。
    /// 在系统诊断、监控、配置审查时使用。
    /// </summary>
    [McpServerTool, Description("获取整体系统统计信息，包括配置概览、Embedding模型参数、搜索配置、重排配置、索引统计等。用于系统诊断和配置审查。")]
    public async Task<string> GetSystemStats()
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GetSystemStats), out var permErr))
        {
            return permErr!;
        }
        var config = scope.ServiceProvider.GetRequiredService<ConfigManager>().Load();
        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var bm25Store = scope.ServiceProvider.GetRequiredService<IBM25Store>();
        var embeddingService = scope.ServiceProvider.GetService<IEmbeddingService>();

        try
        {
            var totalVectors = await GetTotalVectorCount(vectorStore);
            var totalBm25Stats = await GetTotalBm25Stats(bm25Store);

            return JsonSerializer.Serialize(new
            {
                success = true,
                system = new
                {
                    dataPath = config.DataPath,
                    modelsRootPath = config.ModelsRootPath,
                    sourcesCount = config.Sources.Count,
                    enabledSourcesCount = config.Sources.Count(s => s.Enabled)
                },
                embedding = new
                {
                    modelName = config.Embedding.ModelName,
                    maxSequenceLength = config.Embedding.MaxSequenceLength,
                    batchSize = config.Embedding.BatchSize,
                    useCuda = config.Embedding.UseCuda
                },
                search = new
                {
                    defaultTopK = config.Search.DefaultTopK,
                    similarityThreshold = config.Search.SimilarityThreshold,
                    enableMmr = config.Search.EnableMmr,
                    bm25Provider = config.Search.BM25Provider
                },
                rerank = new
                {
                    enabled = config.Rerank.Enabled,
                    modelName = config.Rerank.ModelName,
                    topN = config.Rerank.TopN,
                    useCuda = config.Rerank.UseCuda
                },
                index = new
                {
                    autoIndexEnabled = config.Indexing.AutoIndexEnabled,
                    syncOnStartup = config.Indexing.SyncOnStartup,
                    totalVectors = totalVectors,
                    totalDocuments = totalBm25Stats.DocumentCount,
                    totalTokens = totalBm25Stats.TotalTokens
                }
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

    #region Private Helper Methods

    private async Task<int> GetVectorCountForSource(IVectorStore vectorStore, string sourceName)
    {
        try
        {
            var stats = await vectorStore.GetVectorStatsAsync();
            return stats.Sum(s => s.VectorCount);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<(int DocumentCount, long TotalTokens)> GetBm25StatsForSource(IBM25Store bm25Store, string sourceName)
    {
        try
        {
            var stats = await bm25Store.GetStatsAsync();
            return (stats.TotalDocuments, 0L);
        }
        catch
        {
            return (0, 0);
        }
    }

    private async Task<int> GetTotalVectorCount(IVectorStore vectorStore)
    {
        try
        {
            var stats = await vectorStore.GetVectorStatsAsync();
            return stats.Sum(s => s.VectorCount);
        }
        catch
        {
            return 0;
        }
    }

    private async Task<(int DocumentCount, long TotalTokens)> GetTotalBm25Stats(IBM25Store bm25Store)
    {
        try
        {
            var stats = await bm25Store.GetStatsAsync();
            return (stats.TotalDocuments, 0L);
        }
        catch
        {
            return (0, 0);
        }
    }

    private IEnumerable<string> GetFilesFromSource(SourceFolder source)
    {
        if (!Directory.Exists(source.Path))
            return [];

        return source.FilePatterns
            .SelectMany(pattern => Directory.EnumerateFiles(source.Path, pattern, SearchOption.AllDirectories))
            .Where(file => !IsExcluded(file, source));
    }

    private bool IsExcluded(string filePath, SourceFolder source)
    {
        var fileName = Path.GetFileName(filePath);
        var directory = Path.GetDirectoryName(filePath) ?? "";

        foreach (var excludeDir in source.ExcludeDirs)
        {
            if (directory.Contains(excludeDir, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var excludePattern in source.ExcludeFiles)
        {
            if (MatchesPattern(fileName, excludePattern))
                return true;
        }

        return false;
    }

    private bool MatchesPattern(string fileName, string pattern)
    {
        if (pattern.StartsWith("*"))
            return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith("*"))
            return fileName.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        if (pattern.StartsWith("~"))
            return fileName.StartsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);

        return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
