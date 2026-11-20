using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.ApiHost.Services;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Connectors;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Graph;
using System.Diagnostics;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Service.Controllers;

[ApiController]
[Route("api/graph")]
public class GraphController : ControllerBase
{
    private readonly IGraphStore _graphStore;
    private readonly IVectorStore? _vectorStore;
    private readonly IGraphIndexingService _graphIndexingService;
    private readonly ConfigManager _configManager;
    private readonly ILogger<GraphController> _logger;
    private readonly IKnowledgeBaseStorageManager? _kbStorageManager;
    private readonly ICatalogStore? _catalogStore;
    private readonly IServiceProvider? _serviceProvider;
    private readonly KnowledgeBaseJobCoordinator? _jobCoordinator;
    private readonly IIndexEventPublisher? _eventPublisher;
    private readonly WikiLinkExtractor _wikiLinkExtractor = new();

    // 控制器是 Scoped，用 static 字段跨请求共享重建状态。
    // 按 kbId 分锁：全局单锁会让「全量重建」把其它库的重建请求一并挡掉（Conflict）。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _rebuildingByKb =
        new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim _rebuildLock = new(1, 1);

    internal static Func<Task>? BeforeGraphClearHook { get; set; }

    public GraphController(
        IGraphStore graphStore,
        IGraphIndexingService graphIndexingService,
        ConfigManager configManager,
        ILogger<GraphController> logger,
        IVectorStore? vectorStore = null,
        IServiceProvider? serviceProvider = null,
        IKnowledgeBaseStorageManager? kbStorageManager = null,
        ICatalogStore? catalogStore = null,
        KnowledgeBaseJobCoordinator? jobCoordinator = null,
        IIndexEventPublisher? eventPublisher = null)
    {
        _graphStore = graphStore;
        _vectorStore = vectorStore ?? serviceProvider?.GetService<IVectorStore>();
        _graphIndexingService = graphIndexingService;
        _configManager = configManager;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _kbStorageManager = kbStorageManager ?? serviceProvider?.GetService<IKnowledgeBaseStorageManager>();
        _catalogStore = catalogStore ?? serviceProvider?.GetService<ICatalogStore>();
        _jobCoordinator = jobCoordinator ?? serviceProvider?.GetService<KnowledgeBaseJobCoordinator>();
        _eventPublisher = eventPublisher ?? serviceProvider?.GetService<IIndexEventPublisher>();
    }

    /// <summary>
    /// 独立重建知识图谱（支持指定知识库或全局）
    /// </summary>
    [HttpPost("rebuild")]
    public async Task<IActionResult> StartRebuild([FromQuery] string? kbId = null)
    {
        KnowledgeBaseScope scope;
        try
        {
            // 写接口：缺省作用域直接 400——重建必须能回答「到底重建哪个库」。
            scope = await KnowledgeBaseScopeResolver.ResolveWriteAsync(_catalogStore, kbId);
        }
        catch (KnowledgeBaseScopeRequiredException ex)
        {
            return BadRequest(new { error = ex.Message, errorCode = "scope_required" });
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return NotFound(new { error = ex.Message, errorCode = "knowledge_base_not_found" });
        }

        if (scope.Ids.Count == 0)
            return BadRequest(new { error = "没有已启用的知识库可供重建", errorCode = "no_knowledge_base" });

        // 每个库一把「正在重建」标记；已占用则 409（而不是全局 409）。
        var busy = scope.Ids.Where(id => _rebuildingByKb.ContainsKey(id)).ToList();
        if (busy.Count > 0)
            return Conflict(new { error = $"图谱正在重建中：{string.Join(",", busy)}", busyKbs = busy });
        foreach (var id in scope.Ids) _rebuildingByKb[id] = true;

        if (_kbStorageManager == null || _catalogStore == null)
        {
            foreach (var id in scope.Ids) _rebuildingByKb.TryRemove(id, out _);
            if (LegacyGraphUnavailable())
                return Conflict(new { error = "词法索引模式下旧知识图谱可能过期，暂不可用" });
        }

        var targetKbId = scope.Ids.FirstOrDefault() ?? "default";
        var jobId = Guid.NewGuid().ToString("N")[..16];
        var job = new JobDefinition(
            Id: jobId,
            Operation: "graph_rebuild",
            KnowledgeBaseId: targetKbId,
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Running,
            Phase: OperationPhase.Scanning,
            TotalUnits: 0,
            ProcessedUnits: 0,
            IdempotencyKey: $"graph:{targetKbId}:{DateTime.UtcNow.Ticks}",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow,
            FinishedAt: null
        );

        if (_catalogStore != null)
        {
            await _catalogStore.SaveJobAsync(job);
        }

        var cts = new CancellationTokenSource();
        _jobCoordinator?.Register(jobId, cts);

        _ = Task.Run(async () =>
        {
            await _rebuildLock.WaitAsync();
            var sw = Stopwatch.StartNew();
            int rebuilt = 0, failed = 0;

            try
            {
                if (BeforeGraphClearHook != null)
                    await BeforeGraphClearHook();

                // 按作用域逐库重建（全量 = 循环所有启用库；单库 = 只跑一个）
                if (_kbStorageManager != null && _catalogStore != null && scope.Ids.Count > 0)
                {
                  foreach (var targetKbId in scope.Ids)
                  {
                    _logger.LogInformation("开始单知识库图谱重建: {KbId}", targetKbId);
                    await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadWrite);
                    await lease.Store.GraphStore.ClearAllAsync();

                    var connectors = await _catalogStore.GetConnectorsByKnowledgeBaseAsync(targetKbId);
                    foreach (var connDef in connectors)
                    {
                        try
                        {
                            IDataConnector? connector = null;
                            if (string.Equals(connDef.Kind, "Folder", StringComparison.OrdinalIgnoreCase))
                            {
                                if (Directory.Exists(connDef.TargetPath))
                                {
                                    connector = new LocalFolderConnector(connDef.Id, connDef.KnowledgeBaseId, connDef.TargetPath, connDef.FilePattern);
                                }
                            }
                            else if (string.Equals(connDef.Kind, "SingleFile", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(connDef.Kind, "File", StringComparison.OrdinalIgnoreCase))
                            {
                                if (System.IO.File.Exists(connDef.TargetPath))
                                {
                                    connector = new SingleFileConnector(connDef.Id, connDef.KnowledgeBaseId, connDef.TargetPath);
                                }
                            }

                            if (connector == null) continue;

                            await foreach (var change in connector.ScanAsync(new SyncRequest(FullScan: true)))
                            {
                                if (change.Kind == ChangeKind.Delete) continue;
                                try
                                {
                                    string content;
                                    await using (var stream = await connector.OpenReadAsync(change))
                                    using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                                    {
                                        content = await reader.ReadToEndAsync();
                                    }

                                    var graphLinks = _wikiLinkExtractor.Extract(content);
                                    var fileNode = new GraphNode
                                    {
                                        Id = change.SourceKey,
                                        Title = Path.GetFileNameWithoutExtension(change.SourceKey),
                                        Type = "document"
                                    };
                                    var extraNodes = new List<GraphNode>();
                                    var edges = new List<GraphEdge>();
                                    foreach (var (target, heading, type, line) in graphLinks)
                                    {
                                        var targetId = target;
                                        if (!string.IsNullOrEmpty(heading))
                                        {
                                            targetId = $"{target}#{heading}";
                                            extraNodes.Add(new GraphNode
                                            {
                                                Id = targetId,
                                                Title = heading,
                                                Type = "heading"
                                            });
                                        }
                                        else if (type == "tag")
                                        {
                                            targetId = $"#{target}";
                                            extraNodes.Add(new GraphNode
                                            {
                                                Id = targetId,
                                                Title = target,
                                                Type = "tag"
                                            });
                                        }
                                        edges.Add(new GraphEdge
                                        {
                                            FromId = change.SourceKey,
                                            ToId = targetId,
                                            Type = type,
                                            LineNumber = line
                                        });
                                    }
                                    await lease.Store.GraphStore.UpsertFileGraphAsync(change.SourceKey, fileNode, extraNodes, edges);
                                    rebuilt++;
                                    if (rebuilt % 10 == 0 || rebuilt == 1)
                                    {
                                        job = job with { ProcessedUnits = rebuilt, TotalUnits = Math.Max(rebuilt, job.TotalUnits) };
                                        if (_catalogStore != null) await _catalogStore.SaveJobAsync(job);
                                        await PublishJobEventAsync("IndexProgress", jobId, rebuilt, Math.Max(rebuilt, job.TotalUnits), $"正在提取知识图谱关系 ({rebuilt})", 50);
                                        await PublishJobEventAsync("ReindexJobProgress", jobId, rebuilt, Math.Max(rebuilt, job.TotalUnits), $"正在提取知识图谱关系 ({rebuilt})", 50);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    failed++;
                                    _logger.LogWarning(ex, "知识库单文档图谱抽取失败: {Key}", change.SourceKey);
                                }
                                await Task.Yield();
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "处理知识库连接器图谱失败: {ConnId}", connDef.Id);
                        }
                    }

                    sw.Stop();
                    _logger.LogInformation("知识库图谱独立重建完成: KbId={KbId}, rebuilt={Rebuilt}, failed={Failed}, elapsed={Elapsed}ms",
                        targetKbId, rebuilt, failed, sw.ElapsedMilliseconds);
                  }

                  sw.Stop();
                  _logger.LogInformation("知识库图谱重建完成: scope={Scope}, kbs={Kbs}, rebuilt={Rebuilt}, failed={Failed}, elapsed={Elapsed}ms",
                      scope.KindText, string.Join(",", scope.Ids), rebuilt, failed, sw.ElapsedMilliseconds);

                  if (cts.Token.IsCancellationRequested)
                  {
                      job = job with { Status = JobStatus.Cancelled, FinishedAt = DateTime.UtcNow };
                      if (_catalogStore != null) await _catalogStore.SaveJobAsync(job);
                      await PublishJobEventAsync("IndexCompleted", jobId, rebuilt, rebuilt, "知识图谱重建已取消", 100);
                      await PublishJobEventAsync("ReindexJobCompleted", jobId, rebuilt, rebuilt, "知识图谱重建已取消", 100);
                  }
                  else
                  {
                      job = job with { Status = JobStatus.Succeeded, TotalUnits = rebuilt, ProcessedUnits = rebuilt, FinishedAt = DateTime.UtcNow };
                      if (_catalogStore != null) await _catalogStore.SaveJobAsync(job);
                      await PublishJobEventAsync("IndexCompleted", jobId, rebuilt, rebuilt, $"知识图谱重建完成（共 {rebuilt} 篇）", 100);
                      await PublishJobEventAsync("ReindexJobCompleted", jobId, rebuilt, rebuilt, $"知识图谱重建完成（共 {rebuilt} 篇）", 100);
                  }
                  return;
                }

                // 回退到旧全局重建
                if (_vectorStore == null || LegacyGraphUnavailable())
                {
                    _logger.LogWarning("图谱重建被中止：未配置旧存储或图谱处于不可用状态");
                    return;
                }

                var config = _configManager.Load();
                var enabledSources = config.Sources
                    .Where(s => s.Enabled)
                    .Select(s => s.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var files = (await _vectorStore.GetAllFilesAsync())
                    .Where(f => enabledSources.Contains(f.Source))
                    .ToList();

                var filenameMap = GraphFilenameMapper.BuildFilenameMap(files);
                Func<string, string?> resolver = shortId =>
                    filenameMap.TryGetValue(shortId, out var full) ? full : null;

                var fileIds = files.Select(f => f.Id).ToList();
                var chunksByFileId = (await _vectorStore.GetChunksByFileIdsAsync(fileIds))
                    .GroupBy(c => c.FileId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                await _graphStore.ClearAllAsync();

                foreach (var file in files)
                {
                    try
                    {
                        if (!System.IO.File.Exists(file.Path)) continue;
                        var content = await System.IO.File.ReadAllTextAsync(file.Path);
                        var chunks = chunksByFileId.GetValueOrDefault(file.Id, new List<ChunkRecord>());
                        await _graphIndexingService.UpdateGraphAsync(file, content, chunks, default, resolver);
                        rebuilt++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "图谱重建单文件失败: {Path}", file.Path);
                    }

                    await Task.Yield();
                }

                sw.Stop();
                _logger.LogInformation(
                    "图谱独立重建完成: rebuilt={Rebuilt}, failed={Failed}, elapsed={Elapsed}ms",
                    rebuilt, failed, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "图谱重建失败");
                job = job with { Status = JobStatus.Failed, ErrorMessage = ex.Message, FinishedAt = DateTime.UtcNow };
                if (_catalogStore != null) await _catalogStore.SaveJobAsync(job);
                await PublishJobEventAsync("IndexCompleted", jobId, 0, 0, $"知识图谱重建失败: {ex.Message}", 0);
                await PublishJobEventAsync("ReindexJobCompleted", jobId, 0, 0, $"知识图谱重建失败: {ex.Message}", 0);
            }
            finally
            {
                _jobCoordinator?.Unregister(jobId);
                cts.Dispose();
                foreach (var id in scope.Ids) _rebuildingByKb.TryRemove(id, out _);
                _rebuildLock.Release();
            }
        });

        return Accepted(new
        {
            jobId = jobId,
            message = scope.IsAll
                ? $"图谱重建已在后台启动（{scope.Ids.Count} 个知识库）"
                : "图谱重建已在后台启动",
            scope = scope.KindText,
            knowledgeBaseIds = scope.Ids
        });
    }

    private async Task PublishJobEventAsync(string eventName, string jobId, int processed, int total, string message, int percent)
    {
        if (_eventPublisher == null) return;
        try
        {
            var payload = new
            {
                jobId,
                status = percent >= 100 ? "Completed" : "Running",
                phase = "Building",
                processedFiles = processed,
                totalFiles = total,
                progressPercent = percent,
                percent = (double)percent,
                message,
                currentFile = message
            };
            await _eventPublisher.PublishAsync(eventName, payload);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "发布图谱作业事件失败（可忽略）: {Msg}", ex.Message);
        }
    }

    /// <summary>获取图谱重建状态（逐库）</summary>
    [HttpGet("rebuild/status")]
    public ActionResult<object> GetRebuildStatus()
        => Ok(new
        {
            isRebuilding = !_rebuildingByKb.IsEmpty,
            buildingKbs = _rebuildingByKb.Keys.ToArray()
        });

    [HttpGet("node")]
    public async Task<ActionResult<GraphNode>> GetNode([FromQuery] string nodeId, [FromQuery] string? kbId = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return BadRequest(new { error = "nodeId 不能为空" });

        KnowledgeBaseScope scope;
        try
        {
            scope = await KnowledgeBaseScopeResolver.ResolveReadAsync(_catalogStore, kbId);
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return NotFound(new { error = ex.Message, errorCode = "knowledge_base_not_found" });
        }

        if (_kbStorageManager != null && scope.Ids.Count > 0)
        {
            // 全量作用域下逐库查找，返回第一个命中的节点（并标注所属库）。
            foreach (var targetKbId in scope.Ids)
            {
                if (!await _kbStorageManager.ExistsAsync(targetKbId)) continue;
                await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
                var found = await lease.Store.GraphStore.GetNodeAsync(nodeId);
                if (found is null) continue;
                found.Metadata ??= new Dictionary<string, string>();
                found.Metadata["knowledgeBaseId"] = targetKbId;
                return Ok(found);
            }
            return NotFound(new { error = $"节点不存在: {nodeId}" });
        }

        if (LegacyGraphUnavailable()) return Conflict(new { error = "词法索引模式下旧知识图谱可能过期，暂不可用" });
        var legacyNode = await _graphStore.GetNodeAsync(nodeId);
        if (legacyNode == null) return NotFound(new { error = $"节点不存在: {nodeId}" });
        return Ok(legacyNode);
    }

    [HttpGet("neighbors")]
    public async Task<ActionResult<GraphTraversalResult>> GetNeighbors(
        [FromQuery] string nodeId,
        [FromQuery] int depth = 1,
        [FromQuery] string? edgeTypes = null,
        [FromQuery] string? kbId = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return BadRequest(new { error = "nodeId 不能为空" });

        depth = Math.Clamp(depth, 1, 3);
        var types = edgeTypes?.Split(',', StringSplitOptions.RemoveEmptyEntries);

        KnowledgeBaseScope scope;
        try
        {
            scope = await KnowledgeBaseScopeResolver.ResolveReadAsync(_catalogStore, kbId);
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return NotFound(new { error = ex.Message, errorCode = "knowledge_base_not_found" });
        }

        if (_kbStorageManager != null && scope.Ids.Count > 0)
        {
            var merged = new GraphTraversalResult { RootId = nodeId, Depth = depth };
            var seenNodes = new HashSet<string>(StringComparer.Ordinal);
            var seenEdges = new HashSet<string>(StringComparer.Ordinal);
            var nodeKb = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var id in scope.Ids)
            {
                if (!await _kbStorageManager.ExistsAsync(id)) continue;
                await using var lease = await _kbStorageManager.AcquireLeaseAsync(id, StorageAccess.ReadOnly);
                var result = await lease.Store.GraphStore.GetNeighborsAsync(nodeId, depth, types);
                foreach (var n in result.Nodes)
                {
                    if (!seenNodes.Add(n.Id)) continue;
                    // 节点同名可能来自不同库：记录归属，避免「找到了但不知道在哪个库」。
                    n.Metadata ??= new Dictionary<string, string>();
                    n.Metadata["knowledgeBaseId"] = id;
                    nodeKb[n.Id] = id;
                    merged.Nodes.Add(n);
                }
                foreach (var e in result.Edges)
                {
                    var key = $"{nodeKb.GetValueOrDefault(e.FromId, id)}>{e.ToId}>{e.Type}";
                    if (seenEdges.Add(key)) merged.Edges.Add(e);
                }
                // 单库时保留原 RootId 语义；全量时 root 可能只存在于某一个库，取第一个命中即可。
                if (!string.IsNullOrEmpty(result.RootId) && string.IsNullOrEmpty(merged.RootId))
                    merged.RootId = result.RootId;
            }
            merged.RootId = nodeId;
            return Ok(merged);
        }

        if (LegacyGraphUnavailable()) return Conflict(new { error = "词法索引模式下旧知识图谱可能过期，暂不可用" });
        var legacyResult = await _graphStore.GetNeighborsAsync(nodeId, depth, types);
        return Ok(legacyResult);
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<GraphNode>>> Search(
        [FromQuery] string q,
        [FromQuery] int limit = 10,
        [FromQuery] string? kbId = null)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "查询不能为空" });

        limit = Math.Clamp(limit, 1, 50);

        KnowledgeBaseScope scope;
        try
        {
            scope = await KnowledgeBaseScopeResolver.ResolveReadAsync(_catalogStore, kbId);
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return NotFound(new { error = ex.Message, errorCode = "knowledge_base_not_found" });
        }

        if (_kbStorageManager != null && scope.Ids.Count > 0)
        {
            // 逐个库查询后按 nodeId 去重（同名节点在不同库是不同实体，用 (kbId,nodeId) 区分）。
            var mergedList = new List<GraphNode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in scope.Ids)
            {
                if (!await _kbStorageManager.ExistsAsync(id)) continue;
                await using var lease = await _kbStorageManager.AcquireLeaseAsync(id, StorageAccess.ReadOnly);
                foreach (var n in await lease.Store.GraphStore.SearchNodesAsync(q, limit))
                {
                    if (!seen.Add($"{id}::{n.Id}")) continue;
                    n.Metadata ??= new Dictionary<string, string>();
                    n.Metadata["knowledgeBaseId"] = id;
                    mergedList.Add(n);
                    if (mergedList.Count >= limit) break;
                }
                if (mergedList.Count >= limit) break;
            }
            return Ok(mergedList);
        }

        if (LegacyGraphUnavailable()) return Conflict(new { error = "词法索引模式下旧知识图谱可能过期，暂不可用" });
        var legacyNodes = await _graphStore.SearchNodesAsync(q, limit);
        return Ok(legacyNodes);
    }

    [HttpPost("subgraph")]
    public async Task<ActionResult<GraphTraversalResult>> GetSubgraph([FromBody] SubgraphRequest request, [FromQuery] string? kbId = null)
    {
        if (request.RootIds == null || request.RootIds.Count == 0)
            return BadRequest(new { error = "rootIds 不能为空" });

        var depth = Math.Clamp(request.Depth, 1, 3);
        var mergedResult = new GraphTraversalResult { Depth = depth };
        var seenNodes = new HashSet<string>();
        var seenEdges = new HashSet<string>();

        KnowledgeBaseScope scope;
        try
        {
            scope = await KnowledgeBaseScopeResolver.ResolveReadAsync(_catalogStore, kbId ?? request.KnowledgeBaseId);
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return NotFound(new { error = ex.Message, errorCode = "knowledge_base_not_found" });
        }

        if (_kbStorageManager != null && scope.Ids.Count > 0)
        {
            foreach (var targetKbId in scope.Ids)
            {
                if (!await _kbStorageManager.ExistsAsync(targetKbId)) continue;
                await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
                foreach (var id in request.RootIds.Distinct())
                {
                    var sub = await lease.Store.GraphStore.GetNeighborsAsync(id, depth);
                    foreach (var n in sub.Nodes)
                    {
                        if (!seenNodes.Add($"{targetKbId}::{n.Id}")) continue;
                        n.Metadata ??= new Dictionary<string, string>();
                        n.Metadata["knowledgeBaseId"] = targetKbId;
                        mergedResult.Nodes.Add(n);
                    }
                    foreach (var e in sub.Edges)
                    {
                        var key = $"{targetKbId}::{e.FromId}>{e.ToId}>{e.Type}";
                        if (seenEdges.Add(key)) mergedResult.Edges.Add(e);
                    }
                }
            }
            return Ok(mergedResult);
        }

        if (LegacyGraphUnavailable()) return Conflict(new { error = "词法索引模式下旧知识图谱可能过期，暂不可用" });
        foreach (var id in request.RootIds.Distinct())
        {
            var sub = await _graphStore.GetNeighborsAsync(id, depth);
            foreach (var n in sub.Nodes)
                if (seenNodes.Add(n.Id)) mergedResult.Nodes.Add(n);
            foreach (var e in sub.Edges)
            {
                var key = $"{e.FromId}>{e.ToId}>{e.Type}";
                if (seenEdges.Add(key)) mergedResult.Edges.Add(e);
            }
        }

        return Ok(mergedResult);
    }

    [HttpGet("stats")]
    public async Task<ActionResult<GraphStatsEnvelope>> GetStats([FromQuery] string? kbId = null)
    {
        KnowledgeBaseScope scope;
        try
        {
            // 读接口：省略作用域 = 全部启用库（与 Elasticsearch 省略索引目标等价）。
            scope = await KnowledgeBaseScopeResolver.ResolveReadAsync(_catalogStore, kbId);
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return NotFound(new { error = ex.Message, errorCode = "knowledge_base_not_found" });
        }

        if (_kbStorageManager != null && scope.Ids.Count > 0)
        {
            var perKb = new List<GraphStatsPerKb>();
            var total = new GraphStats();

            foreach (var id in scope.Ids)
            {
                if (!await _kbStorageManager.ExistsAsync(id))
                    continue;

                await using var lease = await _kbStorageManager.AcquireLeaseAsync(id, StorageAccess.ReadOnly);
                var stats = await lease.Store.GraphStore.GetStatsAsync();
                perKb.Add(new GraphStatsPerKb(id, stats.NodeCount, stats.DocCount, stats.TagCount,
                    stats.HeadingCount, stats.ExternalCount, stats.EdgeCount));

                total.NodeCount += stats.NodeCount;
                total.DocCount += stats.DocCount;
                total.TagCount += stats.TagCount;
                total.HeadingCount += stats.HeadingCount;
                total.ExternalCount += stats.ExternalCount;
                total.EdgeCount += stats.EdgeCount;
            }

            return Ok(Wrap(scope, total, perKb));
        }

        if (LegacyGraphUnavailable()) return Conflict(new { error = "词法索引模式下旧知识图谱可能过期，暂不可用" });
        var legacyStats = await _graphStore.GetStatsAsync();
        return Ok(Wrap(scope, legacyStats, new List<GraphStatsPerKb>()));
    }

    /// <summary>
    /// 统一响应信封：聚合值 + 作用域 + 逐库明细。
    /// 只回一个裸数字时，调用方无法判断它是「某个库」还是「全部库之和」——这正是本次缺陷。
    /// </summary>
    private static GraphStatsEnvelope Wrap(KnowledgeBaseScope scope, GraphStats stats, List<GraphStatsPerKb> perKb) =>
        new(scope.KindText, scope.Ids, scope.RequestedRaw,
            stats.NodeCount, stats.DocCount, stats.TagCount, stats.HeadingCount,
            stats.ExternalCount, stats.EdgeCount, perKb);

    private bool LegacyGraphUnavailable()
    {
        var config = _configManager.Load();
        return !config.Indexing.EmbeddingEnabled || config.Search.EnableLexical ||
            LexicalIndexCoordinator.HasPersistedStaleVectors(Path.Combine(config.DataPath ?? "data", "vectors.db"));
    }

    /// <summary>
    /// 人工干预（节点/边增删）必须落到「一个具体的库」：全量作用域对写入没有意义。
    /// 返回 400 语义一致的错误（scope_required）。
    /// </summary>
    private async Task<(string? KbId, IActionResult? Error)> ResolveWriteKbAsync(string? kbId, CancellationToken ct)
    {
        try
        {
            var scope = await KnowledgeBaseScopeResolver.ResolveWriteAsync(_catalogStore, kbId, ct);
            if (scope.IsAll)
            {
                return (null, BadRequest(new
                {
                    error = "人工干预（节点/关系增删）必须指定具体知识库（kbId），不接受 __all__",
                    errorCode = "scope_required"
                }));
            }
            return (scope.Ids[0], null);
        }
        catch (KnowledgeBaseScopeRequiredException ex)
        {
            return (null, BadRequest(new { error = ex.Message, errorCode = "scope_required" }));
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return (null, NotFound(new { error = ex.Message, errorCode = "knowledge_base_not_found" }));
        }
    }

    // ==================== 实体与关系人工干预 CRUD ====================
    [HttpPost("nodes")]
    public async Task<IActionResult> CreateOrUpdateNode(
        [FromBody] CreateOrUpdateGraphNodeDto request,
        [FromQuery] string? kbId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request?.Id))
            return BadRequest(new { error = "节点 Id 不能为空" });

        var (targetKbId, scopeError) = await ResolveWriteKbAsync(kbId, ct);
        if (scopeError != null) return scopeError;

        var node = new GraphNode
        {
            Id = request.Id.Trim(),
            Title = string.IsNullOrWhiteSpace(request.Title) ? request.Id.Trim() : request.Title.Trim(),
            Type = string.IsNullOrWhiteSpace(request.Type) ? "entity" : request.Type.Trim(),
            Metadata = request.Metadata ?? new()
        };

        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadWrite, ct);
            await lease.Store.GraphStore.UpsertNodeAsync(node, ct);
            return Ok(node);
        }

        await _graphStore.UpsertNodeAsync(node, ct);
        return Ok(node);
    }

    [HttpPut("nodes/{nodeId}")]
    public async Task<IActionResult> UpdateNode(
        string nodeId,
        [FromBody] CreateOrUpdateGraphNodeDto request,
        [FromQuery] string? kbId = null,
        CancellationToken ct = default)
    {
        return await CreateOrUpdateNode(request with { Id = nodeId }, kbId, ct);
    }

    [HttpDelete("nodes/{nodeId}")]
    public async Task<IActionResult> DeleteNode(
        string nodeId,
        [FromQuery] string? kbId = null,
        CancellationToken ct = default)
    {
        var (targetKbId, scopeError) = await ResolveWriteKbAsync(kbId, ct);
        if (scopeError != null) return scopeError;

        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadWrite, ct);
            await lease.Store.GraphStore.DeleteNodeAsync(nodeId, ct);
            return NoContent();
        }

        await _graphStore.DeleteNodeAsync(nodeId, ct);
        return NoContent();
    }

    [HttpPost("edges")]
    public async Task<IActionResult> CreateEdge(
        [FromBody] CreateOrDeleteGraphEdgeDto request,
        [FromQuery] string? kbId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request?.FromId) || string.IsNullOrWhiteSpace(request?.ToId))
            return BadRequest(new { error = "FromId 与 ToId 不能为空" });

        var edge = new GraphEdge
        {
            FromId = request.FromId.Trim(),
            ToId = request.ToId.Trim(),
            Type = string.IsNullOrWhiteSpace(request.Type) ? "related" : request.Type.Trim(),
            LineNumber = request.LineNumber
        };

        var (targetKbId, scopeError) = await ResolveWriteKbAsync(kbId, ct);
        if (scopeError != null) return scopeError;

        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadWrite, ct);
            await lease.Store.GraphStore.UpsertEdgesAsync(new[] { edge }, ct);
            return Ok(edge);
        }

        await _graphStore.UpsertEdgesAsync(new[] { edge }, ct);
        return Ok(edge);
    }

    [HttpDelete("edges")]
    public async Task<IActionResult> DeleteEdge(
        [FromQuery] string fromId,
        [FromQuery] string toId,
        [FromQuery] string? type = null,
        [FromQuery] string? kbId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fromId) || string.IsNullOrWhiteSpace(toId))
            return BadRequest(new { error = "fromId 与 toId 不能为空" });

        var edgeType = string.IsNullOrWhiteSpace(type) ? "related" : type.Trim();
        var (targetKbId, scopeError) = await ResolveWriteKbAsync(kbId, ct);
        if (scopeError != null) return scopeError;

        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadWrite, ct);
            var ok = await lease.Store.GraphStore.DeleteEdgeAsync(fromId, toId, edgeType, ct);
            return Ok(new { success = ok });
        }

        var legacyOk = await _graphStore.DeleteEdgeAsync(fromId, toId, edgeType, ct);
        return Ok(new { success = legacyOk });
    }
}

public class SubgraphRequest
{
    public List<string> RootIds { get; set; } = new();
    public int Depth { get; set; } = 1;
    public string? KnowledgeBaseId { get; set; }
}

/// <summary>图谱统计信封：聚合值 + 作用域 + 逐库明细（让「全部」可被拆解核对）。</summary>
public sealed record GraphStatsEnvelope(
    string Scope,
    IReadOnlyList<string> KbIds,
    string? RequestedScope,
    int NodeCount,
    int DocCount,
    int TagCount,
    int HeadingCount,
    int ExternalCount,
    int EdgeCount,
    IReadOnlyList<GraphStatsPerKb> KnowledgeBases);

public sealed record GraphStatsPerKb(
    string KnowledgeBaseId, int NodeCount, int DocCount, int TagCount,
    int HeadingCount, int ExternalCount, int EdgeCount);

public sealed record CreateOrUpdateGraphNodeDto(string Id, string? Title, string? Type, Dictionary<string, string>? Metadata);
public sealed record CreateOrDeleteGraphEdgeDto(string FromId, string ToId, string? Type = "related", int LineNumber = 0);
