using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly WikiLinkExtractor _wikiLinkExtractor = new();

    // 控制器是 Scoped，用 static 字段跨请求共享重建状态
    private static volatile bool _isRebuilding;
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
        ICatalogStore? catalogStore = null)
    {
        _graphStore = graphStore;
        _vectorStore = vectorStore ?? serviceProvider?.GetService<IVectorStore>();
        _graphIndexingService = graphIndexingService;
        _configManager = configManager;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _kbStorageManager = kbStorageManager ?? serviceProvider?.GetService<IKnowledgeBaseStorageManager>();
        _catalogStore = catalogStore ?? serviceProvider?.GetService<ICatalogStore>();
    }

    private async Task<string?> ResolveKbIdAsync(string? kbId)
    {
        if (!string.IsNullOrWhiteSpace(kbId) && kbId != "__all__")
            return kbId;

        if (_catalogStore != null)
        {
            var kbs = await _catalogStore.ListKnowledgeBasesAsync();
            return kbs.FirstOrDefault(k => k.Enabled)?.Id ?? kbs.FirstOrDefault()?.Id;
        }

        return null;
    }

    /// <summary>
    /// 独立重建知识图谱（支持指定知识库或全局）
    /// </summary>
    [HttpPost("rebuild")]
    public async Task<IActionResult> StartRebuild([FromQuery] string? kbId = null)
    {
        var targetKbId = await ResolveKbIdAsync(kbId);
        if (_isRebuilding)
            return Conflict(new { error = "图谱正在重建中，请稍候" });

        if (string.IsNullOrEmpty(targetKbId) && LegacyGraphUnavailable())
            return Conflict(new { error = "词法索引模式下旧知识图谱可能过期，暂不可用" });

        _ = Task.Run(async () =>
        {
            if (!await _rebuildLock.WaitAsync(0)) return;
            _isRebuilding = true;
            var sw = Stopwatch.StartNew();
            int rebuilt = 0, failed = 0;

            try
            {
                if (BeforeGraphClearHook != null)
                    await BeforeGraphClearHook();

                // 如果指定了知识库，则执行单知识库图谱重建
                if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null && _catalogStore != null)
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
            }
            finally
            {
                _isRebuilding = false;
                _rebuildLock.Release();
            }
        });

        return Accepted(new { message = "图谱重建已在后台启动，可通过统计接口观察进度" });
    }

    /// <summary>获取图谱重建状态</summary>
    [HttpGet("rebuild/status")]
    public ActionResult<object> GetRebuildStatus()
        => Ok(new { isRebuilding = _isRebuilding });

    [HttpGet("node")]
    public async Task<ActionResult<GraphNode>> GetNode([FromQuery] string nodeId, [FromQuery] string? kbId = null)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return BadRequest(new { error = "nodeId 不能为空" });

        var targetKbId = await ResolveKbIdAsync(kbId);
        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
            var node = await lease.Store.GraphStore.GetNodeAsync(nodeId);
            if (node == null) return NotFound(new { error = $"节点不存在: {nodeId}" });
            return Ok(node);
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

        var targetKbId = await ResolveKbIdAsync(kbId);
        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
            var result = await lease.Store.GraphStore.GetNeighborsAsync(nodeId, depth, types);
            return Ok(result);
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
        var targetKbId = await ResolveKbIdAsync(kbId);
        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
            var nodes = await lease.Store.GraphStore.SearchNodesAsync(q, limit);
            return Ok(nodes);
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

        var targetKbId = await ResolveKbIdAsync(kbId ?? request.KnowledgeBaseId);
        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
            foreach (var id in request.RootIds.Distinct())
            {
                var sub = await lease.Store.GraphStore.GetNeighborsAsync(id, depth);
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
    public async Task<ActionResult<GraphStats>> GetStats([FromQuery] string? kbId = null)
    {
        var targetKbId = await ResolveKbIdAsync(kbId);
        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
            var stats = await lease.Store.GraphStore.GetStatsAsync();
            return Ok(stats);
        }

        if (LegacyGraphUnavailable()) return Conflict(new { error = "词法索引模式下旧知识图谱可能过期，暂不可用" });
        var legacyStats = await _graphStore.GetStatsAsync();
        return Ok(legacyStats);
    }

    private bool LegacyGraphUnavailable()
    {
        var config = _configManager.Load();
        return !config.Indexing.EmbeddingEnabled || config.Search.EnableLexical ||
            LexicalIndexCoordinator.HasPersistedStaleVectors(Path.Combine(config.DataPath ?? "data", "vectors.db"));
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

        var targetKbId = await ResolveKbIdAsync(kbId);
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
        var targetKbId = await ResolveKbIdAsync(kbId);
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

        var targetKbId = await ResolveKbIdAsync(kbId);
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
        var targetKbId = await ResolveKbIdAsync(kbId);
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

public sealed record CreateOrUpdateGraphNodeDto(string Id, string? Title, string? Type, Dictionary<string, string>? Metadata);
public sealed record CreateOrDeleteGraphEdgeDto(string FromId, string ToId, string? Type = "related", int LineNumber = 0);
