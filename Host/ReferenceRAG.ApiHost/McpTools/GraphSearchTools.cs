using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 知识图谱检索工具（归属 search 业务模块）
/// </summary>
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
        [Description("遍历深度，1-3，默认为 1")] int depth = 1,
        [Description("目标知识库 ID（可选）。若留空则自动查询首个有效知识库")] string? knowledgeBaseId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GraphSearch), out var permErr))
        {
            return permErr!;
        }

        depth = Math.Clamp(depth, 1, 3);
        GraphTraversalResult? result = null;

        var kbStorage = scope.ServiceProvider.GetService<IKnowledgeBaseStorageManager>();
        var catalog = scope.ServiceProvider.GetService<ICatalogStore>();

        string? targetKbId = knowledgeBaseId;
        if (string.IsNullOrWhiteSpace(targetKbId) && catalog != null)
        {
            var kbs = await catalog.ListKnowledgeBasesAsync();
            targetKbId = kbs.FirstOrDefault(k => k.Enabled)?.Id ?? kbs.FirstOrDefault()?.Id;
        }

        if (!string.IsNullOrEmpty(targetKbId) && kbStorage != null && await kbStorage.ExistsAsync(targetKbId))
        {
            await using var lease = await kbStorage.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
            result = await lease.Store.GraphStore.GetNeighborsAsync(nodeId, depth);
        }
        else
        {
            if (LegacyGraphUnavailable(scope.ServiceProvider))
                return JsonSerializer.Serialize(new { error = "词法索引模式下旧知识图谱可能过期，暂不可用" });
            var graphStore = scope.ServiceProvider.GetService<IGraphStore>();
            if (graphStore == null)
                return JsonSerializer.Serialize(new { error = "知识图谱功能未启用" });
            result = await graphStore.GetNeighborsAsync(nodeId, depth);
        }

        var response = new
        {
            rootId = nodeId,
            depth,
            knowledgeBaseId = targetKbId,
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
        [Description("最大返回数量，默认 10")] int limit = 10,
        [Description("目标知识库 ID（可选）。若留空则自动查询首个有效知识库")] string? knowledgeBaseId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GraphSearchByTitle), out var permErr))
        {
            return permErr!;
        }

        limit = Math.Clamp(limit, 1, 50);
        List<GraphNode> nodes;

        var kbStorage = scope.ServiceProvider.GetService<IKnowledgeBaseStorageManager>();
        var catalog = scope.ServiceProvider.GetService<ICatalogStore>();

        string? targetKbId = knowledgeBaseId;
        if (string.IsNullOrWhiteSpace(targetKbId) && catalog != null)
        {
            var kbs = await catalog.ListKnowledgeBasesAsync();
            targetKbId = kbs.FirstOrDefault(k => k.Enabled)?.Id ?? kbs.FirstOrDefault()?.Id;
        }

        if (!string.IsNullOrEmpty(targetKbId) && kbStorage != null && await kbStorage.ExistsAsync(targetKbId))
        {
            await using var lease = await kbStorage.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
            nodes = await lease.Store.GraphStore.SearchNodesAsync(query, limit);
        }
        else
        {
            if (LegacyGraphUnavailable(scope.ServiceProvider))
                return JsonSerializer.Serialize(new { error = "词法索引模式下旧知识图谱可能过期，暂不可用" });
            var graphStore = scope.ServiceProvider.GetService<IGraphStore>();
            if (graphStore == null)
                return JsonSerializer.Serialize(new { error = "知识图谱功能未启用" });
            nodes = await graphStore.SearchNodesAsync(query, limit);
        }

        return JsonSerializer.Serialize(new
        {
            query,
            knowledgeBaseId = targetKbId,
            count = nodes.Count,
            nodes = nodes.Select(n => new { id = n.Id, title = n.Title, type = n.Type })
        });
    }

    private static bool LegacyGraphUnavailable(IServiceProvider services)
    {
        var configManager = services.GetService<ConfigManager>();
        if (configManager == null) return true;
        var config = configManager.Load();
        return !config.Indexing.EmbeddingEnabled || config.Search.EnableLexical ||
            LexicalIndexCoordinator.HasPersistedStaleVectors(Path.Combine(config.DataPath ?? "data", "vectors.db"));
    }
}
