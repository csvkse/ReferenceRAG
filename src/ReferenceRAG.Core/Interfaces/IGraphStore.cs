using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

public interface IGraphStore
{
    Task UpsertNodeAsync(GraphNode node, CancellationToken ct = default);
    Task UpsertEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken ct = default);
    /// <summary>删除节点及其所有关联边（入边+出边）。用于文件删除场景。</summary>
    Task DeleteNodeAsync(string nodeId, CancellationToken ct = default);
    /// <summary>仅删除该节点的出边（保留节点本身和其他文件指向它的入边）。用于重新索引时刷新链接。</summary>
    Task DeleteOutgoingEdgesAsync(string nodeId, CancellationToken ct = default);
    /// <summary>删除属于某文档的所有 heading 子节点（id 以 fileNodeId# 开头）。</summary>
    Task DeleteHeadingNodesAsync(string fileNodeId, CancellationToken ct = default);
    Task<GraphNode?> GetNodeAsync(string nodeId, CancellationToken ct = default);
    Task<GraphTraversalResult> GetNeighborsAsync(string nodeId, int depth = 1, string[]? edgeTypes = null, CancellationToken ct = default);
    Task<List<GraphNode>> SearchNodesAsync(string query, int limit = 10, CancellationToken ct = default);
    Task<GraphStats> GetStatsAsync(CancellationToken ct = default);
}
