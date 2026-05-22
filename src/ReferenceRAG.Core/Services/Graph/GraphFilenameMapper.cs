using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services.Graph;

/// <summary>
/// 图谱文件名映射工具。从已索引文件列表构建 filename→fullNodeId 映射表。
/// 同名文件（路径不同）记为模糊，不加入映射（保持短名不解析）。
/// </summary>
public static class GraphFilenameMapper
{
    public static IReadOnlyDictionary<string, string> BuildFilenameMap(IEnumerable<FileRecord> files)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file.Path);
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

    private static string NormalizeNodeId(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
