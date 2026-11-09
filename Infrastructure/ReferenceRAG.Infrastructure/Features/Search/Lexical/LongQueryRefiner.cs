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

        // 2. 词性标注与信息熵分析
        var weightedTokens = _posAdapter.AnalyzeAndWeight(rawQuery);

        // 3. 提取高权重词项 (名词与动作)
        var highEntropyTerms = weightedTokens
            .OrderByDescending(t => t.Weight)
            .Select(t => t.Word)
            .Concat(phoneticExpansions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        // 4. 构建 Tier 1: 物理短语邻近度约束 (NEAR & AND)
        string? tier1NearQuery = null;
        if (highEntropyTerms.Count >= 2)
        {
            var nearClauses = new List<string>();

            // 对最高权重的 2~4 个实体短语，构造两两邻近约束 NEAR("w1" "w2", 20)
            var coreNouns = highEntropyTerms.Take(4).ToList();
            for (int i = 0; i < coreNouns.Count - 1; i++)
            {
                var w1 = EscapeFts(coreNouns[i]);
                var w2 = EscapeFts(coreNouns[i + 1]);
                nearClauses.Add($"NEAR(\"{w1}\" \"{w2}\", 25)");
            }

            if (nearClauses.Count > 0)
            {
                tier1NearQuery = string.Join(" OR ", nearClauses);
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
            .Take(32);
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
