using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

public interface IGraphStore
{
    Task UpsertNodeAsync(GraphNode node, CancellationToken ct = default);
    Task UpsertEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken ct = default);
    Task DeleteNodeAsync(string nodeId, CancellationToken ct = default);
    Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default);
    Task<GraphTraversalResult> GetNeighborsAsync(string nodeId, int depth = 1, string[]? edgeTypes = null, CancellationToken ct = default);
    Task<List<GraphNode>> SearchNodesAsync(string query, int limit = 10, CancellationToken ct = default);
    Task<GraphStats> GetStatsAsync(CancellationToken ct = default);
}
