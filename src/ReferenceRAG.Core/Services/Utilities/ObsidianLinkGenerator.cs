using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// Obsidian 链接生成器
/// </summary>
public class ObsidianLinkGenerator
{
    private readonly IVectorStore _vectorStore;

    public ObsidianLinkGenerator(IVectorStore vectorStore)
    {
        _vectorStore = vectorStore;
    }

    /// <summary>
    /// 生成 Obsidian 链接
    /// </summary>
    public string GenerateLink(string filePath, int startLine, int endLine, string? text = null)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var link = startLine == endLine
            ? $"[[{fileName}#L{startLine}]]"
            : $"[[{fileName}#L{startLine}-L{endLine}]]";

        if (!string.IsNullOrEmpty(text))
        {
            link = $"[[{fileName}#L{startLine}-L{endLine}|{text}]]";
        }

        return link;
    }

    /// <summary>
    /// 生成章节链接
    /// </summary>
    public string GenerateSectionLink(string filePath, string headingPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var headingAnchor = headingPath.Replace("/", " > ").Replace(" ", "%20");
        return $"[[{fileName}#{headingAnchor}]]";
    }

    /// <summary>
    /// 生成块引用
    /// </summary>
    public string GenerateBlockRef(string filePath, string blockId)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return $"[[{fileName}#^{blockId}]]";
    }

    /// <summary>
    /// 批量生成链接
    /// </summary>
    public List<ChunkLink> GenerateLinks(List<SearchResult> results)
    {
        return results.Select(r => new ChunkLink
        {
            ChunkId = r.ChunkId,
            FilePath = r.FilePath,
            Link = GenerateLink(r.FilePath, r.StartLine, r.EndLine),
            SectionLink = !string.IsNullOrEmpty(r.HeadingPath)
                ? GenerateSectionLink(r.FilePath, r.HeadingPath)
                : null,
            Title = r.Title,
            HeadingPath = r.HeadingPath,
            StartLine = r.StartLine,
            EndLine = r.EndLine
        }).ToList();
    }

    /// <summary>
    /// 解析 Obsidian 链接
    /// </summary>
    public ObsidianLink ParseLink(string link)
    {
        // 格式: [[fileName#L10-L20|text]]
        var result = new ObsidianLink();

        // 移除 [[ 和 ]]
        var content = link.Trim('[', ']');

        // 提取显示文本
        var pipeIndex = content.IndexOf('|');
        if (pipeIndex >= 0)
        {
            result.DisplayText = content.Substring(pipeIndex + 1);
            content = content.Substring(0, pipeIndex);
        }

        // 提取锚点
        var hashIndex = content.IndexOf('#');
        if (hashIndex >= 0)
        {
            result.FileName = content.Substring(0, hashIndex);
            var anchor = content.Substring(hashIndex + 1);

            // 解析行号
            if (anchor.StartsWith("L"))
            {
                var lineMatch = System.Text.RegularExpressions.Regex.Match(anchor, @"L(\d+)(?:-L(\d+))?");
                if (lineMatch.Success)
                {
                    result.StartLine = int.Parse(lineMatch.Groups[1].Value);
                    result.EndLine = lineMatch.Groups[2].Success
                        ? int.Parse(lineMatch.Groups[2].Value)
                        : result.StartLine;
                }
            }
            else if (anchor.StartsWith("^"))
            {
                result.BlockId = anchor.Substring(1);
            }
            else
            {
                result.Heading = anchor;
            }
        }
        else
        {
            result.FileName = content;
        }

        return result;
    }

    /// <summary>
    /// 生成 Markdown 引用列表
    /// </summary>
    public string GenerateReferenceList(List<SearchResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 相关引用");
        sb.AppendLine();

        var grouped = results.GroupBy(r => r.FilePath);

        foreach (var group in grouped)
        {
            var fileName = Path.GetFileNameWithoutExtension(group.Key);
            sb.AppendLine($"### {fileName}");

            foreach (var result in group)
            {
                var link = GenerateLink(result.FilePath, result.StartLine, result.EndLine);
                var preview = result.Content.Length > 50
                    ? result.Content.Substring(0, 50) + "..."
                    : result.Content;

                sb.AppendLine($"- {link}: {preview}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 生成嵌入查询结果
    /// </summary>
    public string GenerateEmbedResult(AIQueryResponse response)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```obsidian-rag");
        sb.AppendLine($"query: {response.Query}");
        sb.AppendLine($"mode: {response.Mode}");
        sb.AppendLine($"results: {response.Chunks.Count}");
        sb.AppendLine("```");
        sb.AppendLine();

        foreach (var chunk in response.Chunks)
        {
            sb.AppendLine($"> [!quote] {chunk.ObsidianLink}");
            sb.AppendLine($"> {chunk.Content.Substring(0, Math.Min(200, chunk.Content.Length))}...");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

/// <summary>
/// 分段链接
/// </summary>
public class ChunkLink
{
    public string ChunkId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public string? SectionLink { get; set; }
    public string? Title { get; set; }
    public string? HeadingPath { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}

/// <summary>
/// Obsidian 链接解析结果
/// </summary>
public class ObsidianLink
{
    public string FileName { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? Heading { get; set; }
    public string? BlockId { get; set; }
    public string? DisplayText { get; set; }
}
