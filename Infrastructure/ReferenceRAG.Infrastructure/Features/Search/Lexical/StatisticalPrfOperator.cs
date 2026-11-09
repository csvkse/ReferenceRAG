using JiebaNet.Segmenter;

namespace ReferenceRAG.Core.Features.Search.Lexical;

/// <summary>
/// 纯统计伪相关反馈算子 (Statistical Pseudo-Relevance Feedback Operator)
/// 针对词汇鸿沟场景，从初检索 Top-3 种子候选切片中，通过 TF 与词项显著度无监督挖掘潜在高关联专有名词，
/// 在 100% 无向量模型的前提下实现领域语义泛化与二次精准重排。
/// </summary>
public static class StatisticalPrfOperator
{
    private static readonly JiebaSegmenter Segmenter = new();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "这个", "那个", "自己", "什么", "怎么", "如何", "为什么", "因为", "所以", "如果",
        "一个", "一些", "虽然", "但是", "然后", "而且", "没有", "不是", "不能", "可以",
        "已经", "正在", "还是", "还有", "为了", "关于", "对于", "由于", "如此", "这样"
    };

    /// <summary>
    /// 从初筛候选切片中挖掘高显著度扩展词项 (Expansion Terms)
    /// </summary>
    public static IReadOnlyList<string> MineExpansionTerms(
        IEnumerable<string> seedChunkContents,
        IReadOnlyCollection<string> existingQueryTerms,
        int maxTerms = 3)
    {
        if (seedChunkContents == null) return Array.Empty<string>();

        var existingSet = new HashSet<string>(existingQueryTerms ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var termFrequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int chunkCount = 0;
        foreach (var content in seedChunkContents.Take(3))
        {
            if (string.IsNullOrWhiteSpace(content)) continue;
            chunkCount++;

            var tokens = Segmenter.CutForSearch(content)
                .Select(t => t.Trim())
                .Where(t => t.Length >= 2 && !StopWords.Contains(t) && !existingSet.Contains(t) && t.Any(char.IsLetterOrDigit));

            foreach (var token in tokens)
            {
                termFrequency[token] = termFrequency.GetValueOrDefault(token, 0) + 1;
            }
        }

        if (chunkCount == 0 || termFrequency.Count == 0) return Array.Empty<string>();

        // 评分：局部词频 TF * 长度加权 (专名多为 2~4 字)
        return termFrequency
            .Where(kv => kv.Value >= 1)
            .OrderByDescending(kv => kv.Value * Math.Min(kv.Key.Length, 4))
            .Take(maxTerms)
            .Select(kv => kv.Key)
            .ToList();
    }
}
