using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 「这次检索到底有没有命中」的判定器。
///
/// 背景：生产实测 <c>semantic_search(query="zzzzqqqxxx 完全不存在的词")</c> 仍返回 5 条、
/// <c>usedRecallFloor=false</c>，模型无从判断这批结果是命中还是凑数。
/// 判定规则与召回侧同源：查询里的内容词（CJK 二元组 / ASCII 词）必须在结果正文里出现，
/// 命中数 ≥ 1 才算命中；全部未命中且未触发召回地板时视为 NoMatch。
/// </summary>
public static class QueryMatchSignals
{
    private static readonly Regex AsciiWord = new(@"[A-Za-z0-9_]{2,}", RegexOptions.Compiled);
    private static readonly Regex CjkRun = new(@"[\u4e00-\u9fff]+", RegexOptions.Compiled);

    /// <summary>内容词里常见的高频虚词二元组，判定时忽略（否则任意中文正文都会「命中」）。</summary>
    private static readonly HashSet<string> StopBigrams = new(StringComparer.Ordinal)
    {
        "一个", "这个", "那个", "可以", "需要", "用于", "通过", "以及", "并且",
        "我们", "你的", "如果", "就是", "还有", "什么", "哪些", "一下", "怎么",
        "是否", "多少", "目前", "当前", "所有", "如何", "为什么", "是不是"
    };

    public static (bool NoMatch, string? Reason) Evaluate(
        string? query,
        IReadOnlyList<RecallItem>? items,
        bool usedRecallFloor,
        IReadOnlyList<string>? extraTokens = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return (false, null);
        if (items is null || items.Count == 0) return (true, "没有召回任何切片");

        var cjkTokens = TokenizeCjk(query);
        var asciiTokens = TokenizeAscii(query);
        if (extraTokens is not null)
        {
            asciiTokens.AddRange(extraTokens.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));
        }
        if (cjkTokens.Count == 0 && asciiTokens.Count == 0) return (false, null);

        var haystack = new StringBuilder();
        foreach (var item in items)
        {
            if (item.Text is { Length: > 0 }) haystack.Append(item.Text);
            if (item.SourceKey is { Length: > 0 }) haystack.Append('\n').Append(item.SourceKey);
            if (item.HeadingPath is { Length: > 0 }) haystack.Append('\n').Append(item.HeadingPath);
        }
        var hay = haystack.ToString();

        // 判定分两类：
        //  1) 查询含 ASCII 标识（zzzzqqqxxx / E1001 / McpServerTool）：这是意图锚点，
        //     任一个都没出现 → 没命中（中文二元组偶然重叠不算数）。
        //  2) 纯中文查询：二元组覆盖率低于 40% 视为没命中（"存在/完全/可能"几乎处处出现）。
        if (asciiTokens.Count > 0)
        {
            var asciiHits = asciiTokens.Count(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (asciiHits == 0)
            {
                return (true, $"查询标识（{string.Join("、", asciiTokens.Take(4))}）未出现在任何返回切片中；结果与查询无关，请改写查询或确认语料覆盖范围");
            }
            return (false, null);
        }

        var hit = cjkTokens.Count(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
        var requiredHits = cjkTokens.Count <= 2 ? 1 : (int)Math.Ceiling(cjkTokens.Count * 0.4);
        if (hit >= requiredHits) return (false, null);

        if (usedRecallFloor)
        {
            return (true, "查询内容词在语料中不存在，返回的是召回地板的宽松兜底结果");
        }

        return (true, $"查询内容词（{string.Join("、", cjkTokens.Take(4))}）多数未出现在返回切片中；结果与查询相关性不足，请改写查询或确认语料覆盖范围");
    }

    /// <summary>ASCII 标识：长度 ≥3 才算意图锚点（"MCP"、"E1001"）。</summary>
    public static List<string> TokenizeAscii(string text)
    {
        var tokens = new List<string>();
        foreach (Match m in AsciiWord.Matches(text))
        {
            if (m.Value.Length >= 3) tokens.Add(m.Value.ToLowerInvariant());
        }
        return tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>CJK 二元组，剔除高频虚词。</summary>
    public static List<string> TokenizeCjk(string text)
    {
        var tokens = new List<string>();
        foreach (Match run in CjkRun.Matches(text))
        {
            var s = run.Value;
            if (s.Length == 1)
            {
                tokens.Add(s);
                continue;
            }
            for (var i = 0; i < s.Length - 1; i++)
            {
                var bg = s.Substring(i, 2);
                if (!StopBigrams.Contains(bg)) tokens.Add(bg);
            }
        }
        return tokens.Distinct(StringComparer.Ordinal).ToList();
    }
}
