using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Infrastructure.Features.Search.Lexical;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Infrastructure.Features.Search.Fusion;

/// <summary>
/// 动态意图自适应加权与跨模态 Z-Score 正态融合器
/// 根据问句熵、实体密度、编号模式动态调节权重，并保留原始置信度极值
/// </summary>
public static class AdaptiveQueryAwareFusion
{
    private static readonly Regex NumberPatternRegex = new(@"\b(?:\d{4,}|v?\d+\.\d+(?:\.\d+)*|HTTP\s*\d{3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 计算动态融合权重 (wBm25, wVector)
    /// </summary>
    public static (double WeightBm25, double WeightVector) CalculateDynamicWeights(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (0.5, 0.5);
        }

        var phrases = NamePhraseExtractor.Extract(query);
        bool hasExactCodeOrNumber = NumberPatternRegex.IsMatch(query);
        bool hasStrictQuotes = phrases.StrictQuotes.Count > 0;
        bool hasEntities = phrases.EntityPhrases.Count > 0 || phrases.BookTitles.Count > 0;

        // 1. 若包含双引号强制词、4位以上编号、版本号、HTTP状态码 -> 词法强先验
        if (hasStrictQuotes || hasExactCodeOrNumber)
        {
            return (0.85, 0.15);
        }

        // 2. 包含核心专有名词实体
        if (hasEntities)
        {
            return (0.75, 0.25);
        }

        // 3. 计算字符多样性香农熵 (Shannon Entropy)
        double entropy = CalculateShannonEntropy(query);
        if (query.Length > 20 && entropy > 3.8)
        {
            // 抽象复杂长句且熵高 -> 稠密语义向量泛化
            return (0.25, 0.75);
        }

        // 默认均权
        return (0.50, 0.50);
    }

    /// <summary>
    /// 跨模态 Z-Score 标准化线性融合
    /// </summary>
    public static IReadOnlyList<RecallItem> FuseWithZScore(
        IReadOnlyList<RecallItem> bm25Items,
        IReadOnlyList<RecallItem> vectorItems,
        string? query,
        int topK)
    {
        if (bm25Items == null || bm25Items.Count == 0) return vectorItems?.Take(topK).ToList() ?? new List<RecallItem>();
        if (vectorItems == null || vectorItems.Count == 0) return bm25Items?.Take(topK).ToList() ?? new List<RecallItem>();

        var (wBm25, wVector) = CalculateDynamicWeights(query);

        // 分别计算两路的均值与标准差
        var (meanB, stdB) = ComputeMeanAndStd(bm25Items.Select(x => x.Score));
        var (meanV, stdV) = ComputeMeanAndStd(vectorItems.Select(x => x.Score));

        var fusedMap = new Dictionary<string, (double FusedScore, RecallItem BaseItem)>(StringComparer.Ordinal);

        foreach (var b in bm25Items)
        {
            double zScore = stdB > 1e-6 ? (b.Score - meanB) / stdB : 0.0;
            double normScore = Math.Max(0.0, zScore + 2.0); // 偏移保证非负
            fusedMap[b.ChunkId] = (wBm25 * normScore, b);
        }

        foreach (var v in vectorItems)
        {
            double zScore = stdV > 1e-6 ? (v.Score - meanV) / stdV : 0.0;
            double normScore = Math.Max(0.0, zScore + 2.0);

            if (fusedMap.TryGetValue(v.ChunkId, out var existing))
            {
                fusedMap[v.ChunkId] = (existing.FusedScore + wVector * normScore, existing.BaseItem);
            }
            else
            {
                fusedMap[v.ChunkId] = (wVector * normScore, v);
            }
        }

        return fusedMap.Values
            .OrderByDescending(x => x.FusedScore)
            .Take(topK)
            .Select(x => x.BaseItem with { Score = x.FusedScore, ScoreKind = ScoreKind.RRF })
            .ToList();
    }

    private static (double Mean, double Std) ComputeMeanAndStd(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count == 0) return (0, 0);
        double mean = list.Average();
        double variance = list.Sum(x => Math.Pow(x - mean, 2)) / list.Count;
        return (mean, Math.Sqrt(variance));
    }

    private static double CalculateShannonEntropy(string s)
    {
        var freq = new Dictionary<char, int>();
        foreach (char c in s) freq[c] = freq.GetValueOrDefault(c, 0) + 1;
        double entropy = 0.0;
        int len = s.Length;
        foreach (var count in freq.Values)
        {
            double p = (double)count / len;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }
}
