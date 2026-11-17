using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 数据源管理工具。
///
/// v2 之后「源」有两个物理来源：config.Sources（配置源）与知识库连接器。
/// 本组工具统一经 <see cref="ISourceCatalog"/> 解析、用 per-KB 连接器快照计数，
/// 与 list_sources 保持同一口径（此前 get_source_info 仍读旧 vectors.db，生产实测
/// 同一路径给出 26,945/0，而 list_sources 给出 165/24,776）。
/// </summary>
[McpServerToolType]
public class SourceManagementTools
{
    private readonly IServiceProvider _serviceProvider;

    public SourceManagementTools(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    [McpServerTool(Title = "数据源清单", UseStructuredContent = true, OutputSchemaType = typeof(ListSourcesResult))]
    [Description("列出所有已配置的知识库数据源及其索引统计。第一次接触服务时先调它拿到合法 sourceName 与 connectorId。")]
    public async Task<string> ListSources(
        [Description("目标知识库 ID 列表，须为 JSON 数组字符串，如 [\"kb_a\",\"kb_b\"]。留空表示全部已启用知识库")] string? knowledgeBaseIds = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(ListSources), out var permErr))
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);

        var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();

        try
        {
            if (kbService == null)
            {
                var onlyConfig = scope.ServiceProvider.GetRequiredService<ConfigManager>().Load();
                var rows = onlyConfig.Sources.Select(s => new
                {
                    name = string.IsNullOrEmpty(s.Name) ? Path.GetFileName(s.Path) : s.Name,
                    path = s.Path,
                    enabled = s.Enabled,
                    origin = "SourceConfig",
                    fileCount = 0,
                    chunkCount = 0
                }).ToList();
                return McpResult.Ok(new { success = true, knowledgeBaseCount = 0, count = rows.Count, sources = rows });
            }

            var requested = string.IsNullOrWhiteSpace(knowledgeBaseIds)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(knowledgeBaseIds) ?? new List<string>();

            var kbs = await kbService.ListKnowledgeBasesAsync();
            var targetKbs = requested.Count > 0
                ? kbs.Where(k => requested.Contains(k.Id, StringComparer.OrdinalIgnoreCase)).ToList()
                : kbs.Where(k => k.Enabled).ToList();

            var snapshots = (await GetStatsCache(scope.ServiceProvider).GetConnectorStatsAsync(kbService))
                .Where(s => targetKbs.Any(k => k.Id == s.KnowledgeBaseId))
                .ToList();

            var byPath = new Dictionary<string, (int Docs, int Chunks)>(StringComparer.OrdinalIgnoreCase);
            foreach (var snap in snapshots)
            {
                var key = NormalizePath(snap.TargetPath);
                byPath.TryGetValue(key, out var acc);
                byPath[key] = (acc.Docs + snap.DocumentCount, acc.Chunks + snap.ChunkCount);
            }

            var config = scope.ServiceProvider.GetRequiredService<ConfigManager>().Load();
            var rowsOut = new List<object>();
            foreach (var s in config.Sources)
            {
                byPath.TryGetValue(NormalizePath(s.Path), out var stat);
                rowsOut.Add(new
                {
                    name = string.IsNullOrEmpty(s.Name) ? Path.GetFileName(s.Path) : s.Name,
                    path = s.Path,
                    enabled = s.Enabled,
                    origin = "SourceConfig",
                    fileCount = stat.Docs,
                    chunkCount = stat.Chunks
                });
            }

            // 仅由知识库连接器投影出来的路径也要可见，否则客户端看不到真实语料。
            var knownPaths = config.Sources.Select(s => NormalizePath(s.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var kb in targetKbs)
            {
                foreach (var conn in await kbService.GetConnectorsAsync(kb.Id))
                {
                    var key = NormalizePath(conn.TargetPath);
                    if (!knownPaths.Add(key)) continue;
                    byPath.TryGetValue(key, out var stat);
                    rowsOut.Add(new
                    {
                        name = $"{kb.Name} - {Path.GetFileName(conn.TargetPath.TrimEnd('/', '\\'))}",
                        path = conn.TargetPath,
                        enabled = conn.Enabled,
                        origin = "KnowledgeBaseOnly",
                        knowledgeBaseId = kb.Id,
                        connectorId = conn.Id,
                        fileCount = stat.Docs,
                        chunkCount = stat.Chunks
                    });
                }
            }

            return McpResult.Ok(new
            {
                success = true,
                knowledgeBaseCount = targetKbs.Count,
                count = rowsOut.Count,
                sources = rowsOut
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    [McpServerTool(Title = "数据源详情")]
    [Description("获取指定数据源的详细信息：路径状态、文档数、分块数、物理文件数量与大小。源名可来自 list_sources（含知识库连接器）。")]
    public async Task<string> GetSourceInfo(
        [Description("数据源名称或完整路径（必填），可来自 list_sources")] string sourceName)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GetSourceInfo), out var permErr))
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);

        var catalog = scope.ServiceProvider.GetService<ISourceCatalog>() ?? new SourceCatalog(scope.ServiceProvider);
        var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();

        try
        {
            var descriptor = await catalog.ResolveAsync(sourceName);
            if (descriptor == null)
                return McpResult.Fail($"未找到数据源: {sourceName}（可用 list_sources 查看合法名称）", McpErrorCodes.SourceNotFound);

            var stats = (await GetStatsCache(scope.ServiceProvider).GetConnectorStatsAsync(kbService))
                .Where(s => NormalizePath(s.TargetPath) == NormalizePath(descriptor.Path))
                .ToList();

            var pathExists = Directory.Exists(descriptor.Path);
            var physicalFiles = 0;
            var totalSize = 0L;
            if (pathExists)
            {
                try
                {
                    var files = EnumeratePhysicalFiles(descriptor.Path);
                    physicalFiles = files.Count;
                    totalSize = files.TotalSize;
                }
                catch { }
            }

            return McpResult.Ok(new
            {
                success = true,
                source = new
                {
                    name = descriptor.Name,
                    path = descriptor.Path,
                    enabled = descriptor.Enabled,
                    origin = descriptor.Origin,
                    knowledgeBaseId = descriptor.KnowledgeBaseId,
                    connectorId = descriptor.ConnectorId,
                    stats = new
                    {
                        documentCount = stats.Sum(s => s.DocumentCount),
                        chunkCount = stats.Sum(s => s.ChunkCount),
                        physicalFiles,
                        totalSizeBytes = totalSize,
                        totalSizeMB = Math.Round(totalSize / 1024.0 / 1024.0, 2),
                        lastIndexed = stats.Count > 0 ? stats.Max(s => s.UpdatedAt) : (DateTime?)null
                    },
                    pathExists
                }
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    [McpServerTool(Title = "系统统计", UseStructuredContent = true, OutputSchemaType = typeof(SystemStatsResult))]
    [Description("获取整体系统统计：配置概览、Embedding 模型参数、搜索与重排配置、按库汇总的索引统计。排查配置问题或确认当前模型参数时使用。")]
    public async Task<string> GetSystemStats()
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GetSystemStats), out var permErr))
            return McpResult.Fail(permErr, McpErrorCodes.Forbidden);

        var config = scope.ServiceProvider.GetRequiredService<ConfigManager>().Load();
        var kbService = scope.ServiceProvider.GetService<IKnowledgeBaseService>();
        var kbStorage = scope.ServiceProvider.GetService<IKnowledgeBaseStorageManager>();

        try
        {
            var sources = await GetCatalog(scope.ServiceProvider).ListAsync();

            // 重排健康度：provider 只注册为 IRerankService（非独立注册），
            // 所以要先拿 IRerankService 再按接口判断，否则 GetService<IRerankRuntimeTelemetryProvider>() 恒为 null。
            var rerankHealth = "unknown";
            string? rerankDegradationReason = null;
            var rerankRuntime = scope.ServiceProvider.GetService<IRerankRuntimeTelemetryProvider>()
                                ?? scope.ServiceProvider.GetService<IRerankService>() as IRerankRuntimeTelemetryProvider;
            if (rerankRuntime is not null)
            {
                rerankHealth = rerankRuntime.IsDegraded
                    ? "degraded"
                    : (rerankRuntime.GetLifecycleSnapshot().IsReady ? "ok" : "idle");
                rerankDegradationReason = rerankRuntime.DegradationReason;
            }

            int chunks = 0, docs = 0, vectors = 0;
            if (kbService != null && kbStorage != null)
            {
                var stats = await GetStatsCache(scope.ServiceProvider).GetIndexStatsAsync(kbService, kbStorage);
                chunks = stats.Sum(s => s.ChunkCount);
                docs = stats.Sum(s => s.DocumentCount);
                vectors = stats.Sum(s => s.VectorCount);
            }

            return McpResult.Ok(new
            {
                success = true,
                system = new
                {
                    dataPath = config.DataPath,
                    modelsRootPath = config.ModelsRootPath,
                    sourcesCount = sources.Count(s => s.Enabled),
                    knowledgeBaseConnectorCount = sources.Count(s => s.Origin == "KnowledgeBaseOnly"),
                    enabledSourcesCount = sources.Count(s => s.Enabled)
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
                    useCuda = config.Rerank.UseCuda,
                    modelPath = config.Rerank.ModelPath,
                    // 配置面必须能看出「模型名/设备都对，但其实完全打不出可信分数」。
                    health = rerankHealth,
                    degradationReason = rerankDegradationReason
                },
                index = new
                {
                    totalChunks = chunks,
                    totalDocuments = docs,
                    totalVectors = vectors
                }
            });
        }
        catch (Exception ex)
        {
            return McpResult.FailException(ex);
        }
    }

    private static ISourceCatalog GetCatalog(IServiceProvider sp) =>
        sp.GetService<ISourceCatalog>() ?? new SourceCatalog(sp);

    private static IMcpStatsCache GetStatsCache(IServiceProvider sp) =>
        sp.GetService<IMcpStatsCache>() ?? new McpStatsCache();

    private static string NormalizePath(string? path) =>
        (path ?? string.Empty).TrimEnd('/', '\\').Replace('\\', '/');

    private static (int Count, long TotalSize) EnumeratePhysicalFiles(string root)
    {
        var count = 0;
        long size = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/.git/", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            try { size += new FileInfo(file).Length; } catch { }
        }
        return (count, size);
    }
}
