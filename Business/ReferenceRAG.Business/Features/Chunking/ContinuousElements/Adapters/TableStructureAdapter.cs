using System.Text.RegularExpressions;
using ReferenceRAG.Business.Features.Chunking.ContinuousElements.Models;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Adapters;

/// <summary>
/// Markdown 表格结构适配器
/// </summary>
public class TableStructureAdapter : IContinuousElementAdapter<TableElement>
{
    private static readonly Regex TableSeparatorRegex = new(
        @"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)+\|?\s*$",
        RegexOptions.Compiled);

    public bool CanAdapt(string[] lines, int startIndex, out int consumedLines)
    {
        consumedLines = 0;
        if (startIndex >= lines.Length) return false;

        var line = lines[startIndex].Trim();
        if (string.IsNullOrEmpty(line) || !line.Contains('|')) return false;

        // 情况 1: 单行表头 + 分隔行（常见情况）
        if (startIndex + 1 < lines.Length && TableSeparatorRegex.IsMatch(lines[startIndex + 1]))
        {
            consumedLines = CountTableLines(lines, startIndex, separatorOffset: 1);
            return consumedLines >= 2;
        }

        // 情况 2: 复合两行表头 + 分隔行
        if (startIndex + 2 < lines.Length 
            && lines[startIndex + 1].Trim().Contains('|') 
            && TableSeparatorRegex.IsMatch(lines[startIndex + 2]))
        {
            consumedLines = CountTableLines(lines, startIndex, separatorOffset: 2);
            return consumedLines >= 3;
        }

        return false;
    }

    private static int CountTableLines(string[] lines, int startIndex, int separatorOffset)
    {
        int i = startIndex + separatorOffset + 1;
        while (i < lines.Length)
        {
            var current = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(current) || !current.Contains('|'))
            {
                break;
            }
            i++;
        }
        return i - startIndex;
    }

    public TableElement Adapt(string[] lines, int startIndex, int consumedLines, int startLineNumber, string? precedingContext = null)
    {
        var headerRows = new List<string>();
        string separatorRow = string.Empty;
        var dataRows = new List<TableRowItem>();

        int currentIndex = startIndex;
        int currentLineNumber = startLineNumber;

        // 识别分隔行位置
        int separatorOffset = 1;
        if (startIndex + 1 < lines.Length && TableSeparatorRegex.IsMatch(lines[startIndex + 1]))
        {
            separatorOffset = 1;
            headerRows.Add(lines[startIndex]);
        }
        else if (startIndex + 2 < lines.Length && TableSeparatorRegex.IsMatch(lines[startIndex + 2]))
        {
            separatorOffset = 2;
            headerRows.Add(lines[startIndex]);
            headerRows.Add(lines[startIndex + 1]);
        }
        else
        {
            headerRows.Add(lines[startIndex]);
        }

        separatorRow = lines[startIndex + separatorOffset];

        // 提取数据行
        int dataStartIndex = startIndex + separatorOffset + 1;
        int endLineNumber = startLineNumber + consumedLines - 1;

        for (int i = dataStartIndex; i < startIndex + consumedLines; i++)
        {
            var rawRow = lines[i];
            dataRows.Add(new TableRowItem
            {
                RawContent = rawRow,
                LineNumber = startLineNumber + (i - startIndex),
                EstimatedTokens = TokenEstimator.EstimateTokens(rawRow)
            });
        }

        var headerTokens = TokenEstimator.EstimateTokens(string.Join("\n", headerRows) + "\n" + separatorRow);

        var rawContent = string.Join("\n", lines.Skip(startIndex).Take(consumedLines));

        return new TableElement
        {
            HeaderRows = headerRows,
            SeparatorRow = separatorRow,
            DataRows = dataRows,
            PrecedingContext = precedingContext,
            StartLine = startLineNumber,
            EndLine = endLineNumber,
            EstimatedHeaderTokens = headerTokens,
            RawContent = rawContent
        };
    }
}
