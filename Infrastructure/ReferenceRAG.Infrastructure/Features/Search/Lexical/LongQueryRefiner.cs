using System.Text.RegularExpressions;

namespace ReferenceRAG.Core.Features.Search.Lexical;

/// <summary>
/// 分层布尔检索规划 (Tiered Lexical Search Plan)
/// </summary>
public sealed record TieredLexicalPlan(
    string? Tier1StrictNearQuery,
    string Tier2WeightedQuery,
    string Tier3FallbackQuery,
    IReadOnlyList<string> DistilledTerms
);

/// <summary>
/// 纯词法长句查询优化与分层规划器 (Long Query Refiner)
/// 针对长句输入，执行词性信息熵蒸馏、空间物理邻近短语生成与阶梯式松弛规划
/// </summary>
public sealed class LongQueryRefiner
{
    private static readonly Lazy<LongQueryRefiner> _defaultInstance =
        new(() => new LongQueryRefiner());

    public static LongQueryRefiner Default => _defaultInstance.Value;

    private readonly IPosAnalysisAdapter _posAdapter;

    public LongQueryRefiner(IPosAnalysisAdapter? posAdapter = null)
    {
        _posAdapter = posAdapter ?? JiebaPosAnalysisAdapter.Default;
    }

    /// <summary>
    /// 构建长句的分层检索执行计划
    /// </summary>
    public TieredLexicalPlan PlanQuery(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return new TieredLexicalPlan(null, string.Empty, string.Empty, Array.Empty<string>());
        }

        // 1. 提取领域音近错字与泛化同义词
        var phoneticExpansions = PhoneticQueryExpander.GetExpansionTerms(rawQuery);

        // 2. 词性标注与信息熵分析 (保留原句时序)
        var weightedTokens = _posAdapter.AnalyzeAndWeight(rawQuery);

        // 3. 提取原句自然顺序的高权重词元序列 (用于短语滑动窗口)
        var orderedWords = weightedTokens
            .Where(t => t.Weight >= 1.2 && t.Word.Length >= 2)
            .Select(t => t.Word)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 提取按信息熵加权降序排序的高权重词项 (用于 Tier 2 加权检索与 PRF 反馈)
        var highEntropyTerms = weightedTokens
            .OrderByDescending(t => t.Weight)
            .Select(t => t.Word)
            .Concat(phoneticExpansions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        // 4. 构建 Tier 1: 物理短语邻近度与复合约束 (保持原句自然时序，消除权重倒置)
        string? tier1NearQuery = null;
        if (orderedWords.Count >= 2)
        {
            var adjacentBiGrams = new List<string>();

            // 4.1 提取原句相邻紧邻词偶 (Slop <= 5)
            for (int i = 0; i < orderedWords.Count - 1; i++)
            {
                var w1 = EscapeFts(orderedWords[i]);
                var w2 = EscapeFts(orderedWords[i + 1]);
                adjacentBiGrams.Add($"NEAR(\"{w1}\" \"{w2}\", 5)");
            }

            // 4.2 提取首尾跨距实体词对 (Slop <= 25)
            var spanClauses = new List<string>();
            if (orderedWords.Count >= 3)
            {
                var wFirst = EscapeFts(orderedWords[0]);
                var wMid = EscapeFts(orderedWords[Math.Min(2, orderedWords.Count - 1)]);
                spanClauses.Add($"NEAR(\"{wFirst}\" \"{wMid}\", 20)");
            }
            if (orderedWords.Count >= 4)
            {
                var wFirst = EscapeFts(orderedWords[0]);
                var wLast = EscapeFts(orderedWords[^1]);
                spanClauses.Add($"NEAR(\"{wFirst}\" \"{wLast}\", 30)");
            }

            // 4.3 构造强复合检索表达式：多词偶 AND 优选，两两词偶 OR 兜底
            var tier1Parts = new List<string>();
            if (adjacentBiGrams.Count >= 2)
            {
                // 组合前两个相邻短语形成联合约束，如 (NEAR("未婚" "夫妻", 5) AND NEAR("公主" "闺房", 5))
                tier1Parts.Add($"({adjacentBiGrams[0]} AND {adjacentBiGrams[1]})");
            }
            tier1Parts.AddRange(adjacentBiGrams);
            tier1Parts.AddRange(spanClauses);

            if (tier1Parts.Count > 0)
            {
                tier1NearQuery = string.Join(" OR ", tier1Parts.Distinct());
            }
        }

        // 5. 构建 Tier 2: 蒸馏高熵词与纠错词的加权 OR 集合
        var tier2Tokens = highEntropyTerms.Count > 0 ? highEntropyTerms : new List<string> { rawQuery.Trim() };
        var tier2WeightedQuery = string.Join(" OR ", tier2Tokens.Select(t => $"\"{EscapeFts(t)}\""));

        // 6. 构建 Tier 3: 全词切分兜底
        var allTokens = rawQuery.Split(new[] { ' ', '\t', '，', '。', '？', '！', '、' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .Concat(phoneticExpansions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(64);
        var tier3FallbackQuery = string.Join(" OR ", allTokens.Select(t => $"\"{EscapeFts(t)}\""));

        if (string.IsNullOrWhiteSpace(tier3FallbackQuery))
        {
            tier3FallbackQuery = $"\"{EscapeFts(rawQuery.Trim())}\"";
        }

        return new TieredLexicalPlan(
            tier1NearQuery,
            tier2WeightedQuery,
            tier3FallbackQuery,
            highEntropyTerms
        );
    }

    private static string EscapeFts(string text)
    {
        return text.Replace("\"", "\"\"");
    }
}
