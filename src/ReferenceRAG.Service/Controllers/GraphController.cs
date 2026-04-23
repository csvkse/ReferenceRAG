using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Service.Controllers;

[ApiController]
[Route("api/graph")]
public class GraphController : ControllerBase
{
    private readonly IGraphStore _graphStore;

    public GraphController(IGraphStore graphStore)
    {
        _graphStore = graphStore;
    }

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
