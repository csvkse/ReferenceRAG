using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Business.Features.Search.Services;

/// <summary>
/// 专有名词与高上下文 IDF 词项抽取器 (借鉴 SAG-Wiki/SAG2 name_phrases & expansion_terms)
/// 用于无向量环境下的多跳实体扩展与关联推理检索
/// </summary>
public static class LexicalEntityExtractor
{
    // 匹配英文大写专有名词短语 (如 J. R. Smith, SiliconFlow API, TypeSafe AI)
    private static readonly Regex EnglishNamePhraseRegex = new(
        @"(?<![\p{L}\p{N}])(?:\p{Lu}\.|\p{Lu}[\p{L}\p{M}]*(?:[-’']\p{Lu}[\p{L}\p{M}]*)*)(?:[ \t]+(?:\p{Lu}\.|\p{Lu}[\p{L}\p{M}]*(?:[-’']\p{Lu}[\p{L}\p{M}]*)*))+(?![\p{L}\p{N}])",
        RegexOptions.Compiled);

    // 匹配中文书名号、引号内的专有名词或特定专有短语 (如 《九阳真经》、"张三丰")
    private static readonly Regex ChineseQuotedEntityRegex = new(
        @"[《“""'「]([^\r\n《》“”""'「」]{2,15})[》”""'」]",
        RegexOptions.Compiled);

    /// <summary>
    /// 从文本中提取专有名词与实体短语
    /// </summary>
    public static List<string> ExtractNamePhrases(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 提取英文专有大写短语
        foreach (Match match in EnglishNamePhraseRegex.Matches(text))
        {
            var val = match.Value.Trim();
            if (val.Length >= 3)
            {
                entities.Add(val);
            }
        }

        // 2. 提取中文引号/书名号内实体
        foreach (Match match in ChineseQuotedEntityRegex.Matches(text))
        {
            var val = match.Groups[1].Value.Trim();
            if (val.Length >= 2 && !val.All(char.IsDigit))
            {
                entities.Add(val);
            }
        }

        return entities.ToList();
    }

    /// <summary>
    /// 基于 Hop 0 种子分块与查询词，构建多跳关联扩展词项 (Expansion Terms)
    /// </summary>
    public static List<string> BuildExpansionTerms(
        IReadOnlyList<(string Content, double Score)> seeds,
        IReadOnlySet<string> queryTerms,
        int limit = 5)
    {
        if (seeds == null || seeds.Count == 0) return new List<string>();

        var scoredTerms = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var maxScore = seeds.Max(s => s.Score);
        if (maxScore <= 0) maxScore = 1.0;

        foreach (var seed in seeds)
        {
            var seedWeight = 0.5 + 0.5 * (Math.Abs(seed.Score) / maxScore);

            // 1. 提取显式实体短语 (加权 2.0x)
            var entities = ExtractNamePhrases(seed.Content);
            foreach (var entity in entities)
            {
                if (queryTerms.Contains(entity)) continue;
                var current = scoredTerms.GetValueOrDefault(entity, 0.0);
                scoredTerms[entity] = Math.Max(current, seedWeight * 2.0);
            }

            // 2. 提取普通词项
            var tokens = TokenizationCacheProxy.Default.Tokenize(seed.Content);
            foreach (var token in tokens)
            {
                if (token.Length < 2 || queryTerms.Contains(token)) continue;
                var current = scoredTerms.GetValueOrDefault(token, 0.0);
                scoredTerms[token] = current + seedWeight * 0.5;
            }
        }

        return scoredTerms
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// 多路检索候选列表轮询交错合并 (Interleave Round-Robin)
    /// </summary>
    public static List<T> Interleave<T>(
        IReadOnlyList<IReadOnlyList<T>> candidateLists,
        Func<T, string> idSelector,
        int limit)
    {
        if (candidateLists == null || candidateLists.Count == 0) return new List<T>();
        if (candidateLists.Count == 1) return candidateLists[0].Take(limit).ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<T>(limit);

        int maxDepth = candidateLists.Max(l => l.Count);
        for (int rank = 0; rank < maxDepth; rank++)
        {
            foreach (var list in candidateLists)
            {
                if (rank < list.Count)
                {
                    var item = list[rank];
                    var id = idSelector(item);
                    if (seen.Add(id))
                    {
                        result.Add(item);
                        if (result.Count >= limit) return result;
                    }
                }
            }
        }

        return result;
    }
}
