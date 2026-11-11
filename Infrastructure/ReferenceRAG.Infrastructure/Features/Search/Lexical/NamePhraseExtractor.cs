using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReferenceRAG.Infrastructure.Features.Search.Lexical;

/// <summary>
/// 正则专有名词短语抽取器 (参考 SAG-Wiki name_phrases)
/// 识别大写短语、中英文专有名词、双引号字面量、版本号与特定协议码
/// </summary>
public static class NamePhraseExtractor
{
    // 双引号字面量匹配: "..." 或 “...”
    private static readonly Regex QuotedPhraseRegex = new(@"[""“]([^""”]+)[""”]", RegexOptions.Compiled);

    // 英文专有名词、协议号、版本号、复合连字符词: HTTP 451, GPT-4, BGE-M3, .NET 10, v2.3.1
    private static readonly Regex WesternEntityRegex = new(
        @"\b(?:[A-Z][a-zA-Z0-9_\-\.]*(?:\s+[A-Z0-9][a-zA-Z0-9_\-\.]*)+|[A-Z]{2,}(?:[-_][A-Z0-9]+)*|v?\d+\.\d+(?:\.\d+)*)\b",
        RegexOptions.Compiled);

    // 中文书名号与专有实体标记: 《...》、〈...〉、［...］
    private static readonly Regex BookTitleRegex = new(@"[《〈]([^》〉]+)[》〉]", RegexOptions.Compiled);

    public sealed record ExtractedPhrases(
        IReadOnlyList<string> StrictQuotes,
        IReadOnlyList<string> EntityPhrases,
        IReadOnlyList<string> BookTitles
    );

    /// <summary>
    /// 从用户查询中提取专有名词短语集合
    /// </summary>
    public static ExtractedPhrases Extract(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new ExtractedPhrases(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }

        var quotes = new List<string>();
        foreach (Match m in QuotedPhraseRegex.Matches(query))
        {
            var val = m.Groups[1].Value.Trim();
            if (val.Length >= 2) quotes.Add(val);
        }

        var entities = new List<string>();
        foreach (Match m in WesternEntityRegex.Matches(query))
        {
            var val = m.Value.Trim();
            if (val.Length >= 2 && !quotes.Contains(val, StringComparer.OrdinalIgnoreCase))
            {
                entities.Add(val);
            }
        }

        var bookTitles = new List<string>();
        foreach (Match m in BookTitleRegex.Matches(query))
        {
            var val = m.Groups[1].Value.Trim();
            if (val.Length >= 2) bookTitles.Add(val);
        }

        return new ExtractedPhrases(quotes, entities, bookTitles);
    }

    /// <summary>
    /// 构造增强版 FTS5 MATCH 表达式 (融入短语锁定与 NEAR 邻近)
    /// </summary>
    public static string BuildEnhancedFtsQuery(string? rawQuery, string baseSanitizedQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery)) return baseSanitizedQuery;

        var extracted = Extract(rawQuery);
        var boostClauses = new List<string>();

        // 1. 双引号精确强制词
        foreach (var q in extracted.StrictQuotes)
        {
            boostClauses.Add($"\"{q.Replace("\"", "\"\"")}\"");
        }

        // 2. 书名与核心实体
        foreach (var b in extracted.BookTitles)
        {
            boostClauses.Add($"\"{b.Replace("\"", "\"\"")}\"");
        }

        // 3. 英文专有名词与版本
        foreach (var e in extracted.EntityPhrases)
        {
            boostClauses.Add($"\"{e.Replace("\"", "\"\"")}\"");
        }

        if (boostClauses.Count == 0)
        {
            return baseSanitizedQuery;
        }

        // 格式: (原分词查询) OR ("实体1" OR "实体2")
        var phrasePart = string.Join(" OR ", boostClauses);
        if (string.IsNullOrWhiteSpace(baseSanitizedQuery))
        {
            return phrasePart;
        }

        return $"({baseSanitizedQuery}) OR ({phrasePart})";
    }
}
