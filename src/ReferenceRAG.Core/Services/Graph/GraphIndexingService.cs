using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services.Graph;

public class GraphIndexingService
{
    private readonly IGraphStore _graphStore;
    private readonly WikiLinkExtractor _extractor;

    public GraphIndexingService(IGraphStore graphStore, WikiLinkExtractor extractor)
    {
        _graphStore = graphStore;
        _extractor = extractor;
    }

    /// <summary>
    /// 根据文件记录和内容更新图节点及边。
    /// </summary>
    public async Task UpdateGraphAsync(FileRecord file, string markdownContent, IEnumerable<ChunkRecord> chunks, CancellationToken ct = default)
    {
        var nodeId = NormalizeNodeId(file.Path);

        var node = new GraphNode
        {
            Id = nodeId,
            Title = file.Title ?? file.FileName,
            Type = "document",
            ChunkIds = chunks.Select(c => c.Id).ToList()
        };

        await _graphStore.UpsertNodeAsync(node, ct);

        // 删除该节点的旧出边，重建
        await _graphStore.DeleteNodeAsync(nodeId, ct);
        await _graphStore.UpsertNodeAsync(node, ct);

        var links = _extractor.Extract(markdownContent);
        var edges = new List<GraphEdge>();

        foreach (var (target, type, lineNum) in links)
        {
            var targetId = NormalizeNodeId(target);
            edges.Add(new GraphEdge
            {
                FromId = nodeId,
                ToId = targetId,
                Type = type,
                LineNumber = lineNum
            });
        }

        if (edges.Count > 0)
            await _graphStore.UpsertEdgesAsync(edges, ct);
    }

    /// <summary>
    /// 删除节点及其所有关联边。
    /// </summary>
    public Task RemoveAsync(string filePath, CancellationToken ct = default)
        => _graphStore.DeleteNodeAsync(NormalizeNodeId(filePath), ct);

    private static string NormalizeNodeId(string path)
    {
        // 统一用正斜杠、去掉前导斜杠
        return path.Replace('\\', '/').TrimStart('/');
    }
}
