using System.Text.RegularExpressions;

namespace ReferenceRAG.Core.Services.Graph;

public class WikiLinkExtractor
{
    // [[page]], [[page|alias]], [[page#heading]], [[page#heading|alias]]
    private static readonly Regex WikiLinkRegex =
        new(@"\[\[([^\[\]|#]+)(?:#[^\[\]|]*)?\|?[^\[\]]*\]\]", RegexOptions.Compiled);

    // ![[embed]]
    private static readonly Regex EmbedRegex =
        new(@"!\[\[([^\[\]|#]+)(?:#[^\[\]|]*)?\|?[^\[\]]*\]\]", RegexOptions.Compiled);

    // #tag (word boundary, not inside code blocks)
    private static readonly Regex TagRegex =
        new(@"(?<!\w)#([a-zA-Z一-龥][a-zA-Z0-9一-龥_/-]*)", RegexOptions.Compiled);

    public IReadOnlyList<(string target, string type, int line)> Extract(string markdown)
    {
        var results = new List<(string, string, int)>();
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
                if (!string.IsNullOrEmpty(target))
                    results.Add((target, "embed", lineNum));
            }

            // Wiki links (skip embed matches)
            var lineWithoutEmbeds = EmbedRegex.Replace(line, "");
            foreach (Match m in WikiLinkRegex.Matches(lineWithoutEmbeds))
            {
                var target = NormalizeTarget(m.Groups[1].Value);
                if (!string.IsNullOrEmpty(target))
                    results.Add((target, "wikilink", lineNum));
            }

            // Tags
            foreach (Match m in TagRegex.Matches(line))
            {
                results.Add((m.Groups[1].Value, "tag", lineNum));
            }
        }

        return results;
    }

    private static string NormalizeTarget(string raw)
    {
        var trimmed = raw.Trim();
        // Ensure .md extension for document links
        if (!trimmed.Contains('.') && !string.IsNullOrEmpty(trimmed))
            trimmed += ".md";
        return trimmed;
    }
}
