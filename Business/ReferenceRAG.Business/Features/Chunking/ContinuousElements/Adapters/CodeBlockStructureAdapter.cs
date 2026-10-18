using ReferenceRAG.Business.Features.Chunking.ContinuousElements.Models;

namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Adapters;

/// <summary>
/// Markdown 围栏代码块结构适配器
/// </summary>
public class CodeBlockStructureAdapter : IContinuousElementAdapter<CodeBlockElement>
{
    public bool CanAdapt(string[] lines, int startIndex, out int consumedLines)
    {
        consumedLines = 0;
        if (startIndex >= lines.Length) return false;

        var line = lines[startIndex].TrimStart();
        if (!line.StartsWith("```", StringComparison.Ordinal) && !line.StartsWith("~~~", StringComparison.Ordinal))
        {
            return false;
        }

        var fenceMarker = line.Substring(0, 3);
        int i = startIndex + 1;

        while (i < lines.Length)
        {
            var cur = lines[i].TrimStart();
            if (cur.StartsWith(fenceMarker, StringComparison.Ordinal))
            {
                i++;
                break;
            }
            i++;
        }

        consumedLines = i - startIndex;
        return true;
    }

    public CodeBlockElement Adapt(string[] lines, int startIndex, int consumedLines, int startLineNumber, string? precedingContext = null)
    {
        var openLine = lines[startIndex].TrimStart();
        var fenceMarker = openLine.Substring(0, 3);
        var lang = openLine.Length > 3 ? openLine.Substring(3).Trim() : string.Empty;

        var codeLines = new List<string>();
        bool isProperlyClosed = false;

        int endIndex = startIndex + consumedLines - 1;
        if (consumedLines > 1 && lines[endIndex].TrimStart().StartsWith(fenceMarker, StringComparison.Ordinal))
        {
            isProperlyClosed = true;
            // 内部行不包含开/闭围栏
            for (int i = startIndex + 1; i < endIndex; i++)
            {
                codeLines.Add(lines[i]);
            }
        }
        else
        {
            // EOF 漏闭合容错
            for (int i = startIndex + 1; i <= endIndex; i++)
            {
                codeLines.Add(lines[i]);
            }
        }

        var rawContent = string.Join("\n", lines.Skip(startIndex).Take(consumedLines));

        return new CodeBlockElement
        {
            Language = lang,
            OpenFence = lines[startIndex],
            CloseFence = isProperlyClosed ? lines[endIndex] : fenceMarker,
            CodeLines = codeLines,
            StartLine = startLineNumber,
            EndLine = startLineNumber + consumedLines - 1,
            IsProperlyClosed = isProperlyClosed,
            RawContent = rawContent
        };
    }
}
