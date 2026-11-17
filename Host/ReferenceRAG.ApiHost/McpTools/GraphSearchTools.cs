using ModelContextProtocol.Server;
using System;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.ApiHost.Services;
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
    [McpServerTool(Title = "图谱邻域遍历")]
    [Description("查找与给定文件节点通过 wiki-link 相关联的知识节点（知识图谱遍历）。返回直接链接和反向链接的相关文档列表，可用于发现主题关联内容。nodeId 为文件相对路径（如 'Projects/foo.md'），depth 为遍历深度（1-3，默认1）。")]
    public async Task<string> GraphSearch(
        [Description("节点 ID（文件相对路径，如 'Projects/foo.md'）")] string nodeId,
        [Description("遍历深度范围 1–3，默认 1（超出上限按 3 处理）")] int depth = 1,
        [Description("目标知识库 ID（可选）。留空或传 __all__ 表示遍历全部已启用知识库并合并结果；不要依赖「默认第一个库」")] string? knowledgeBaseId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GraphSearch), out var permErr))
        {
            return permErr!;
        }

        int requestedDepth;
        try
        {
            (requestedDepth, depth) = McpInputAdapters.ClampInt(nameof(depth), depth, 1, 3);
        }
        catch (McpInputArgumentException ex)
        {
            return McpResult.Fail(ex.Message, McpErrorCodes.InvalidArgument);
        }
        GraphTraversalResult? result = null;

        var kbStorage = scope.ServiceProvider.GetService<IKnowledgeBaseStorageManager>();
        var catalog = scope.ServiceProvider.GetService<ICatalogStore>();

        KnowledgeBaseScope kbScope;
        try
        {
            kbScope = await KnowledgeBaseScopeResolver.ResolveReadAsync(catalog, knowledgeBaseId);
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return McpResult.Fail(ex.Message, McpErrorCodes.KnowledgeBaseNotFound);
        }

        string? targetKbId = kbScope.IsAll ? null : kbScope.Ids[0];

        if (kbStorage != null && kbScope.Ids.Count > 0)
        {
            var merged = new GraphTraversalResult { RootId = nodeId, Depth = depth };
            var seenNodes = new HashSet<string>(StringComparer.Ordinal);
            var seenEdges = new HashSet<string>(StringComparer.Ordinal);

            foreach (var id in kbScope.Ids)
            {
                if (!await kbStorage.ExistsAsync(id)) continue;
                await using var lease = await kbStorage.AcquireLeaseAsync(id, StorageAccess.ReadOnly);
                var sub = await lease.Store.GraphStore.GetNeighborsAsync(nodeId, depth);
                foreach (var n in sub.Nodes)
                {
                    if (!seenNodes.Add($"{id}::{n.Id}")) continue;
                    // 回填所属库：否则模型拿到节点也无法判断它来自哪个库（同 chunkId 场景）。
                    n.Metadata ??= new Dictionary<string, string>();
                    n.Metadata["knowledgeBaseId"] = id;
                    merged.Nodes.Add(n);
                }
                foreach (var e in sub.Edges)
                {
                    if (seenEdges.Add($"{id}::{e.FromId}>{e.ToId}>{e.Type}")) merged.Edges.Add(e);
                }
            }
            result = merged;
            if (kbScope.IsAll) targetKbId = null;
        }
        else
        {
            if (LegacyGraphUnavailable(scope.ServiceProvider))
                return McpResult.Fail("词法索引模式下旧知识图谱可能过期，暂不可用", "legacy_graph_stale");
            var graphStore = scope.ServiceProvider.GetService<IGraphStore>();
            if (graphStore == null)
                return McpResult.Fail("知识图谱功能未启用", "graph_disabled");
            result = await graphStore.GetNeighborsAsync(nodeId, depth);
        }

        var response = new
        {
            rootId = nodeId,
            // 回显请求值与生效值：传入 99 被夹到 3，调用方必须能看出来。
            requestedDepth,
            effectiveDepth = depth,
            knowledgeBaseId = targetKbId,
            // 作用域事实：省略/__all__ = 全部启用库（合并结果），不要读成「就是这个库」。
            kbScope = kbScope.KindText,
            knowledgeBaseIds = kbScope.Ids,
            totalNodes = result.Nodes.Count,
            totalEdges = result.Edges.Count,
            nodes = result.Nodes.Select(n => new
            {
                id = n.Id,
                title = n.Title,
                type = n.Type,
                knowledgeBaseId = n.Metadata != null && n.Metadata.TryGetValue("knowledgeBaseId", out var kbOfNode) ? kbOfNode : null
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

    [McpServerTool(Title = "图谱按标题搜索")]
    [Description("在知识图谱中搜索节点（按标题模糊匹配）。返回匹配的文档节点列表，可结合 graph_search 进一步遍历。")]
    public async Task<string> GraphSearchByTitle(
        [Description("搜索关键词")] string query,
        [Description("最大返回数量，默认 10，上限 50（超出按 50 处理）")] int limit = 10,
        [Description("目标知识库 ID（可选）。留空或传 __all__ 表示搜全部已启用知识库；不要依赖「默认第一个库」")] string? knowledgeBaseId = null)
    {
        using var scope = _serviceProvider.CreateScope();
        if (!McpPermissionGuard.CheckPermission(scope.ServiceProvider, nameof(GraphSearchByTitle), out var permErr))
        {
            return permErr!;
        }

        int requestedLimit;
        try
        {
            (requestedLimit, limit) = McpInputAdapters.ClampInt(nameof(limit), limit, 1, 50);
        }
        catch (McpInputArgumentException ex)
        {
            return McpResult.Fail(ex.Message, McpErrorCodes.InvalidArgument);
        }
        List<GraphNode> nodes;

        var kbStorage = scope.ServiceProvider.GetService<IKnowledgeBaseStorageManager>();
        var catalog = scope.ServiceProvider.GetService<ICatalogStore>();

        KnowledgeBaseScope kbScope;
        try
        {
            kbScope = await KnowledgeBaseScopeResolver.ResolveReadAsync(catalog, knowledgeBaseId);
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return McpResult.Fail(ex.Message, McpErrorCodes.KnowledgeBaseNotFound);
        }

        string? targetKbId = kbScope.IsAll ? null : kbScope.Ids[0];

        if (kbStorage != null && kbScope.Ids.Count > 0)
        {
            var mergedNodes = new List<GraphNode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in kbScope.Ids)
            {
                if (!await kbStorage.ExistsAsync(id)) continue;
                await using var lease = await kbStorage.AcquireLeaseAsync(id, StorageAccess.ReadOnly);
                foreach (var n in await lease.Store.GraphStore.SearchNodesAsync(query, limit))
                {
                    if (!seen.Add($"{id}::{n.Id}")) continue;
                    n.Metadata ??= new Dictionary<string, string>();
                    n.Metadata["knowledgeBaseId"] = id;
                    mergedNodes.Add(n);
                    if (mergedNodes.Count >= limit) break;
                }
                if (mergedNodes.Count >= limit) break;
            }
            nodes = mergedNodes;
        }
        else
        {
            if (LegacyGraphUnavailable(scope.ServiceProvider))
                return McpResult.Fail("词法索引模式下旧知识图谱可能过期，暂不可用", "legacy_graph_stale");
            var graphStore = scope.ServiceProvider.GetService<IGraphStore>();
            if (graphStore == null)
                return McpResult.Fail("知识图谱功能未启用", "graph_disabled");
            nodes = await graphStore.SearchNodesAsync(query, limit);
        }

        return McpResult.Ok(new
        {
            query,
            requestedLimit,
            effectiveLimit = limit,
            knowledgeBaseId = targetKbId,
            kbScope = kbScope.KindText,
            knowledgeBaseIds = kbScope.Ids,
            count = nodes.Count,
            nodes = nodes.Select(n => new
            {
                id = n.Id,
                title = n.Title,
                type = n.Type,
                // 回填所属库：跨库聚合时模型必须能把节点回链到具体库。
                knowledgeBaseId = n.Metadata != null && n.Metadata.TryGetValue("knowledgeBaseId", out var kbOfNode)
                    ? kbOfNode
                    : (kbScope.IsAll ? null : targetKbId)
            })
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
