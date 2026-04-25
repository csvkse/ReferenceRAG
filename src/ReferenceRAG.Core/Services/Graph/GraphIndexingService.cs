using System.Text.RegularExpressions;
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

        var chunkList = chunks.ToList();

        // ── CPU 侧：收集所有节点和边，不触碰 DB ──
        var extraNodes  = new List<GraphNode>();
        var seenNodeIds = new HashSet<string>();  // 去重，避免同文件多次 upsert 同一 tag
        var edges       = new List<GraphEdge>();

        // heading 节点（主动扫描文档标题）
        foreach (var (heading, _) in ExtractHeadings(markdownContent))
        {
            var headingNodeId = $"{nodeId}#{heading}";
            if (seenNodeIds.Add(headingNodeId))
            {
                var headingChunks = chunkList
                    .Where(c => c.HeadingPath != null && c.HeadingPath.Contains(heading))
                    .Select(c => c.Id).ToList();
                extraNodes.Add(new GraphNode
                {
                    Id = headingNodeId, Title = heading, Type = "heading", ChunkIds = headingChunks
                });
            }
        }

        // 链接处理：tag / heading-ref / external
        foreach (var (target, heading, type, lineNum) in _extractor.Extract(markdownContent))
        {
            string resolvedId;

            if (type == "tag")
            {
                resolvedId = $"#{target}";
                if (seenNodeIds.Add(resolvedId))
                    extraNodes.Add(new GraphNode { Id = resolvedId, Title = resolvedId, Type = "tag", ChunkIds = new() });
            }
            else
            {
                var rawFileId      = NormalizeNodeId(target);
                var resolvedFileId = resolveLink?.Invoke(rawFileId);

                if (resolvedFileId != null)
                {
                    resolvedId = string.IsNullOrEmpty(heading)
                        ? resolvedFileId
                        : $"{resolvedFileId}#{heading}";

                    if (!string.IsNullOrEmpty(heading) && seenNodeIds.Add(resolvedId))
                        extraNodes.Add(new GraphNode { Id = resolvedId, Title = heading, Type = "heading", ChunkIds = new() });
                }
                else
                {
                    resolvedId = string.IsNullOrEmpty(heading) ? rawFileId : $"{rawFileId}#{heading}";
                    if (resolveLink != null && seenNodeIds.Add(resolvedId))
                        extraNodes.Add(new GraphNode
                        {
                            Id = resolvedId, Title = resolvedId.Replace(".md", ""), Type = "external", ChunkIds = new()
                        });
                }
            }

            edges.Add(new GraphEdge { FromId = nodeId, ToId = resolvedId, Type = type, LineNumber = lineNum });
        }

        // ── 单次锁 + 单个事务写入所有图数据 ──
        await _graphStore.UpsertFileGraphAsync(nodeId, node, extraNodes, edges, ct);
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
        => path.Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// 从 Markdown 文本中提取所有标题行（跳过代码块）。
    /// 返回 (标题文本, 级别) 列表，标题文本已去除前后空白。
    /// </summary>
    private static IEnumerable<(string heading, int level)> ExtractHeadings(string markdown)
    {
        var lines = markdown.Split('\n');
        bool inCode = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```")) { inCode = !inCode; continue; }
            if (inCode) continue;
            var m = Regex.Match(line, @"^(#{1,6})\s+(.+)");
            if (m.Success)
                yield return (m.Groups[2].Value.Trim(), m.Groups[1].Length);
        }
    }
}
