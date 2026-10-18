using System.Text.RegularExpressions;
using ReferenceRAG.Business.Features.Chunking.ContinuousElements.Models;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Adapters;

/// <summary>
/// Markdown 多级大纲/列表树结构适配器
/// </summary>
public class ListTreeStructureAdapter : IContinuousElementAdapter<ListTreeElement>
{
    private static readonly Regex ListItemRegex = new(
        @"^(\s*)([-*+]|\d+[\.)])\s+(.+)$",
        RegexOptions.Compiled);

    public bool CanAdapt(string[] lines, int startIndex, out int consumedLines)
    {
        consumedLines = 0;
        if (startIndex >= lines.Length) return false;

        var line = lines[startIndex];
        if (!ListItemRegex.IsMatch(line)) return false;

        int i = startIndex;
        int listCount = 0;

        while (i < lines.Length)
        {
            var cur = lines[i];
            if (string.IsNullOrWhiteSpace(cur))
            {
                // 若空行后紧接着缩进列表项，允许继续
                if (i + 1 < lines.Length && ListItemRegex.IsMatch(lines[i + 1]))
                {
                    var matchNext = ListItemRegex.Match(lines[i + 1]);
                    if (matchNext.Groups[1].Value.Length > 0)
                    {
                        i++;
                        continue;
                    }
                }
                break;
            }

            if (ListItemRegex.IsMatch(cur))
            {
                listCount++;
                i++;
            }
            else if (cur.StartsWith("   ") || cur.StartsWith("\t"))
            {
                // 列表项的多行延续内容
                i++;
            }
            else
            {
                break;
            }
        }

        // 至少有 2 个连续列表项才作为列表树结构处理
        if (listCount >= 2)
        {
            consumedLines = i - startIndex;
            return true;
        }

        return false;
    }

    public ListTreeElement Adapt(string[] lines, int startIndex, int consumedLines, int startLineNumber, string? precedingContext = null)
    {
        var items = new List<ListItemNode>();
        var ancestorStack = new Stack<(int Indent, string Text)>();

        for (int i = startIndex; i < startIndex + consumedLines; i++)
        {
            var line = lines[i];
            var match = ListItemRegex.Match(line);
            var actualLineNum = startLineNumber + (i - startIndex);

            if (match.Success)
            {
                var indentSpaces = match.Groups[1].Value.Length;
                var marker = match.Groups[2].Value;
                var content = match.Groups[3].Value.Trim();

                while (ancestorStack.Count > 0 && ancestorStack.Peek().Indent >= indentSpaces)
                {
                    ancestorStack.Pop();
                }

                var breadcrumbs = ancestorStack.Select(a => a.Text).Reverse().ToList();

                items.Add(new ListItemNode
                {
                    IndentLevel = indentSpaces / 2,
                    Marker = marker,
                    Content = content,
                    AncestorBreadcrumbs = breadcrumbs,
                    LineNumber = actualLineNum,
                    EstimatedTokens = TokenEstimator.EstimateTokens(line)
                });

                ancestorStack.Push((indentSpaces, $"{marker} {content}"));
            }
            else if (items.Count > 0 && (line.StartsWith(" ") || line.StartsWith("\t")))
            {
                // 追加到上一个节点
                items[^1].Content += "\n" + line.Trim();
                items[^1].EstimatedTokens = TokenEstimator.EstimateTokens(items[^1].Content);
            }
        }

        var rawContent = string.Join("\n", lines.Skip(startIndex).Take(consumedLines));

        return new ListTreeElement
        {
            Items = items,
            StartLine = startLineNumber,
            EndLine = startLineNumber + consumedLines - 1,
            RawContent = rawContent
        };
    }
}
