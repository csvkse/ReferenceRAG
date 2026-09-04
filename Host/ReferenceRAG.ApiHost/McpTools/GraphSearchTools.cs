using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Service.McpTools;

[McpServerToolType]
public class GraphSearchTools
{
    private readonly IServiceProvider _serviceProvider;

    public GraphSearchTools(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 通过 wiki-link 知识图谱查找与指定节点相关联的文档节点。
    /// 适用于探索 Obsidian 笔记的关联关系、发现相关文档。
    /// </summary>
    [McpServerTool, Description("查找与给定文件节点通过 wiki-link 相关联的知识节点（知识图谱遍历）。返回直接链接和反向链接的相关文档列表，可用于发现主题关联内容。nodeId 为文件相对路径（如 'Projects/foo.md'），depth 为遍历深度（1-3，默认1）。")]
    public async Task<string> GraphSearch(
        [Description("节点 ID（文件相对路径，如 'Projects/foo.md'）")] string nodeId,
        [Description("遍历深度，1-3，默认为 1")] int depth = 1)
    {
        using var scope = _serviceProvider.CreateScope();
        var graphStore = scope.ServiceProvider.GetService<IGraphStore>();
        if (graphStore == null)
            return JsonSerializer.Serialize(new { error = "知识图谱功能未启用" });

        depth = Math.Clamp(depth, 1, 3);
        var result = await graphStore.GetNeighborsAsync(nodeId, depth);

        var response = new
        {
            rootId = nodeId,
            depth,
            totalNodes = result.Nodes.Count,
            totalEdges = result.Edges.Count,
            nodes = result.Nodes.Select(n => new
            {
                id = n.Id,
                title = n.Title,
                type = n.Type
            }),
            edges = result.Edges.Select(e => new
            {
                from = e.FromId,
                to = e.ToId,
                type = e.Type
            })
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = false });
    }

    [McpServerTool, Description("在知识图谱中搜索节点（按标题模糊匹配）。返回匹配的文档节点列表，可结合 graph_search 进一步遍历。")]
    public async Task<string> GraphSearchByTitle(
        [Description("搜索关键词")] string query,
        [Description("最大返回数量，默认 10")] int limit = 10)
    {
        using var scope = _serviceProvider.CreateScope();
        var graphStore = scope.ServiceProvider.GetService<IGraphStore>();
        if (graphStore == null)
            return JsonSerializer.Serialize(new { error = "知识图谱功能未启用" });

        limit = Math.Clamp(limit, 1, 50);
        var nodes = await graphStore.SearchNodesAsync(query, limit);

        return JsonSerializer.Serialize(new
        {
            query,
            count = nodes.Count,
            nodes = nodes.Select(n => new { id = n.Id, title = n.Title, type = n.Type })
        });
    }
}
