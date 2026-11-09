using System.Text.RegularExpressions;

namespace ReferenceRAG.Core.Services.Graph;

public class WikiLinkExtractor
{
    // [[page]], [[page|alias]], [[page#heading]], [[page#heading|alias]]
    // Group 1: file name (before # or |)
    // Group 2: heading (between # and | or ]])，可为空
    private static readonly Regex WikiLinkRegex =
        new(@"\[\[([^\[\]|#]+)(?:#([^\[\]|]*))?(?:\|[^\[\]]*)?\]\]", RegexOptions.Compiled);

    // ![[embed#heading]]
    private static readonly Regex EmbedRegex =
        new(@"!\[\[([^\[\]|#]+)(?:#([^\[\]|]*))?(?:\|[^\[\]]*)?\]\]", RegexOptions.Compiled);

    // #tag (word boundary, not inside code blocks)
    private static readonly Regex TagRegex =
        new(@"(?<!\w)#([a-zA-Z一-龥][a-zA-Z0-9一-龥_/-]*)", RegexOptions.Compiled);

    /// <summary>
    /// 返回 (target 文件名, heading 章节(可为 null), type, line)
    /// </summary>
    public IReadOnlyList<(string target, string? heading, string type, int line)> Extract(string markdown)
    {
        var results = new List<(string, string?, string, int)>();
        var lines = markdown.Split('\n');
        bool inCodeBlock = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNum = i + 1;

            if (line.TrimStart().StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }
            if (inCodeBlock) continue;

            // Embed links first (before wikilinks to avoid double-match)
            foreach (Match m in EmbedRegex.Matches(line))
            {
                var target = NormalizeTarget(m.Groups[1].Value);
                var heading = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
                if (!string.IsNullOrEmpty(target))
                    results.Add((target, string.IsNullOrEmpty(heading) ? null : heading, "embed", lineNum));
            }

            // Wiki links (skip embed matches)
            var lineWithoutEmbeds = EmbedRegex.Replace(line, "");
            foreach (Match m in WikiLinkRegex.Matches(lineWithoutEmbeds))
            {
                var target = NormalizeTarget(m.Groups[1].Value);
                var heading = m.Groups[2].Success ? m.Groups[2].Value.Trim() : null;
                if (!string.IsNullOrEmpty(target))
                    results.Add((target, string.IsNullOrEmpty(heading) ? null : heading, "wikilink", lineNum));
            }

            // Tags（无 heading 概念）
            foreach (Match m in TagRegex.Matches(line))
            {
                results.Add((m.Groups[1].Value, null, "tag", lineNum));
            }
        }

        return results;
    }

    private static string NormalizeTarget(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.Contains('.') && !string.IsNullOrEmpty(trimmed))
            trimmed += ".md";
        return trimmed;
    }
}
