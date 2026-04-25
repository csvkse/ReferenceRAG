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
    /// resolveLink：将 wiki-link 短文件名（如 "foo.md"）解析为完整路径节点 ID，
    ///   Obsidian 使用 filename-based resolution，需要由调用方提供映射。
    /// </summary>
    public async Task UpdateGraphAsync(
        FileRecord file,
        string markdownContent,
        IEnumerable<ChunkRecord> chunks,
        CancellationToken ct = default,
        Func<string, string?>? resolveLink = null)
    {
        var nodeId = NormalizeNodeId(file.Path);

        var node = new GraphNode
        {
            Id = nodeId,
            Title = file.Title ?? file.FileName,
            Type = "document",
            ChunkIds = chunks.Select(c => c.Id).ToList()
        };

        // Upsert 节点（保留节点本身和其他文件指向本节点的入边）
        await _graphStore.UpsertNodeAsync(node, ct);

        // 仅删除本节点的出边，然后用最新链接重建
        await _graphStore.DeleteOutgoingEdgesAsync(nodeId, ct);

        var links = _extractor.Extract(markdownContent);
        var edges = new List<GraphEdge>();

        foreach (var (target, type, lineNum) in links)
        {
            // 优先用 resolveLink 把短文件名解析成完整路径节点 ID；
            // 解析失败时保留短文件名（外链/未索引文件），确保不丢边
            var rawId = NormalizeNodeId(target);
            var resolvedId = resolveLink?.Invoke(rawId) ?? rawId;

            edges.Add(new GraphEdge
            {
                FromId = nodeId,
                ToId = resolvedId,
                Type = type,
                LineNumber = lineNum
            });
        }

        if (edges.Count > 0)
            await _graphStore.UpsertEdgesAsync(edges, ct);
    }

    /// <summary>
    /// 从已索引的文件列表构建 filename→fullNodeId 映射表。
    /// 同名文件（路径不同）记为模糊，不加入映射（保持短名不解析）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildFilenameMap(IEnumerable<FileRecord> files)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file.Path);   // "foo.md"
            if (ambiguous.Contains(filename)) continue;

            var nodeId = NormalizeNodeId(file.Path);
            if (map.ContainsKey(filename))
            {
                map.Remove(filename);
                ambiguous.Add(filename);
            }
            else
            {
                map[filename] = nodeId;
            }
        }

        return map;
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
