using System;
using System.Collections.Generic;
using System.Linq;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using JiebaNet.Segmenter;

namespace ReferenceRAG.Infrastructure.Features.Search.Lexical;

/// <summary>
/// 基于上下文共现 IDF 的多跳词项扩展选择器 (参考 SAG-Wiki expansion_terms)
/// 从 Hop 0 种子切片中提取与原问句同句强共现的实体短语与高价值词项
/// </summary>
public static class ContextIdfTermSelector
{
    private static readonly JiebaSegmenter LexicalSegmenter = new();
    private static readonly char[] SentenceDelimiters = new[] { '。', '！', '？', '；', '\n', '\r', '.', '!', '?' };

    public sealed record ExpansionTerm(
        string Term,
        double Score,
        bool IsNamePhrase
    );

    /// <summary>
    /// 从候选种子切片中挖掘扩展词项
    /// </summary>
    public static IReadOnlyList<ExpansionTerm> SelectExpansionTerms(
        string query,
        IReadOnlyList<RecallItem> seedItems,
        int limit = 3)
    {
        if (string.IsNullOrWhiteSpace(query) || seedItems == null || seedItems.Count == 0 || limit <= 0)
        {
            return Array.Empty<ExpansionTerm>();
        }

        var queryTerms = LexicalSegmenter.CutForSearch(query)
            .Where(t => t.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var termTf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var termContextStrength = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var namePhrasesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 取前 3 个种子切片
        var topSeeds = seedItems.Take(3).ToList();
        for (int seedIdx = 0; seedIdx < topSeeds.Count; seedIdx++)
        {
            var seed = topSeeds[seedIdx];
            var seedWeight = 1.0 / (seedIdx + 1); // 种子位次递减权重

            // 提取专有名词短语
            var phrases = NamePhraseExtractor.Extract(seed.Text);
            foreach (var p in phrases.StrictQuotes.Concat(phrases.BookTitles).Concat(phrases.EntityPhrases))
            {
                if (!queryTerms.Contains(p))
                {
                    namePhrasesSet.Add(p);
                }
            }

            // 分句分析
            var sentences = seed.Text.Split(SentenceDelimiters, StringSplitOptions.RemoveEmptyEntries);
            foreach (var sentence in sentences)
            {
                if (sentence.Length < 4) continue;

                // 统计此句命中原查询词的共现强度
                int queryOverlapCount = 0;
                foreach (var qt in queryTerms)
                {
                    if (sentence.Contains(qt, StringComparison.OrdinalIgnoreCase))
                    {
                        queryOverlapCount++;
                    }
                }

                if (queryOverlapCount == 0) continue;

                double contextIdfBonus = Math.Log(1 + queryOverlapCount * 2.0);

                // 分词
                var words = LexicalSegmenter.CutForSearch(sentence);
                foreach (var word in words)
                {
                    if (word.Length < 2) continue;
                    if (queryTerms.Contains(word)) continue; // 过滤原查询自身词

                    termTf[word] = termTf.GetValueOrDefault(word, 0) + 1;
                    var oldStrength = termContextStrength.GetValueOrDefault(word, 0.0);
                    termContextStrength[word] = Math.Max(oldStrength, contextIdfBonus * seedWeight);
                }
            }
        }

        // 计算扩展打分
        var candidates = new List<ExpansionTerm>();
        foreach (var (term, tf) in termTf)
        {
            var contextStrength = termContextStrength.GetValueOrDefault(term, 0.0);
            if (contextStrength <= 0.0) continue;

            bool isName = namePhrasesSet.Contains(term);
            // 打分公式: (1 + ln(TF)) * (1 + ContextStrength) * (isName ? 2.5 : 1.0)
            double score = (1.0 + Math.Log(tf)) * (1.0 + contextStrength) * (isName ? 2.5 : 1.0);

            candidates.Add(new ExpansionTerm(term, score, isName));
        }

        // 优先按分数降序截断
        return candidates
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .ToList();
    }
}
