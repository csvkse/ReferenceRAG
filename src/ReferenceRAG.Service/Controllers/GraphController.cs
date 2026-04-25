using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services.Graph;
using System.Diagnostics;

namespace ReferenceRAG.Service.Controllers;

[ApiController]
[Route("api/graph")]
public class GraphController : ControllerBase
{
    private readonly IGraphStore _graphStore;
    private readonly IVectorStore _vectorStore;
    private readonly GraphIndexingService _graphIndexingService;
    private readonly ILogger<GraphController> _logger;

    // 控制器是 Scoped，用 static 字段跨请求共享重建状态
    private static volatile bool _isRebuilding;
    private static readonly SemaphoreSlim _rebuildLock = new(1, 1);

    public GraphController(
        IGraphStore graphStore,
        IVectorStore vectorStore,
        GraphIndexingService graphIndexingService,
        ILogger<GraphController> logger)
    {
        _graphStore = graphStore;
        _vectorStore = vectorStore;
        _graphIndexingService = graphIndexingService;
        _logger = logger;
    }

    /// <summary>
    /// 独立重建知识图谱（不触发 GPU 推理，仅扫描 wiki-link）
    /// </summary>
    [HttpPost("rebuild")]
    public ActionResult<object> StartRebuild()
    {
        if (_isRebuilding)
            return Conflict(new { error = "图谱正在重建中，请稍候" });

        _ = Task.Run(async () =>
        {
            if (!await _rebuildLock.WaitAsync(0)) return; // 已有任务在跑
            _isRebuilding = true;
            var sw = Stopwatch.StartNew();
            int rebuilt = 0, failed = 0;

            try
            {
                var files = await _vectorStore.GetAllFilesAsync();
                foreach (var file in files)
                {
                    try
                    {
                        if (!System.IO.File.Exists(file.Path)) continue;
                        var content = await System.IO.File.ReadAllTextAsync(file.Path);
                        var chunks = await _vectorStore.GetChunksByFileAsync(file.Id);
                        await _graphIndexingService.UpdateGraphAsync(file, content, chunks);
                        rebuilt++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogWarning(ex, "图谱重建单文件失败: {Path}", file.Path);
                    }
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

    [HttpGet("node/{*nodeId}")]
    public async Task<ActionResult<GraphNode>> GetNode(string nodeId)
    {
        var node = await _graphStore.GetNodeAsync(nodeId);
        if (node == null) return NotFound(new { error = $"节点不存在: {nodeId}" });
        return Ok(node);
    }

    [HttpGet("neighbors/{*nodeId}")]
    public async Task<ActionResult<GraphTraversalResult>> GetNeighbors(
        string nodeId,
        [FromQuery] int depth = 1,
        [FromQuery] string? edgeTypes = null)
    {
        depth = Math.Clamp(depth, 1, 3);
        var types = edgeTypes?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var result = await _graphStore.GetNeighborsAsync(nodeId, depth, types);
        return Ok(result);
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<GraphNode>>> Search(
        [FromQuery] string q,
        [FromQuery] int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "查询不能为空" });
        limit = Math.Clamp(limit, 1, 50);
        var nodes = await _graphStore.SearchNodesAsync(q, limit);
        return Ok(nodes);
    }

    [HttpPost("subgraph")]
    public async Task<ActionResult<GraphTraversalResult>> GetSubgraph([FromBody] SubgraphRequest request)
    {
        if (request.RootIds == null || request.RootIds.Count == 0)
            return BadRequest(new { error = "rootIds 不能为空" });

        var depth = Math.Clamp(request.Depth, 1, 3);
        var mergedResult = new GraphTraversalResult { Depth = depth };
        var seenNodes = new HashSet<string>();
        var seenEdges = new HashSet<string>();

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
    public async Task<ActionResult<GraphStats>> GetStats()
    {
        var stats = await _graphStore.GetStatsAsync();
        return Ok(stats);
    }
}

public class SubgraphRequest
{
    public List<string> RootIds { get; set; } = new();
    public int Depth { get; set; } = 1;
}
