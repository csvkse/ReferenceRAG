namespace ReferenceRAG.Core.Features.Search.Lexical;

/// <summary>
/// 词法查询计划器配置。
///
/// 设计依据：Elasticsearch 的 <c>common terms query</c>（cutoff_frequency + low_freq_operator）
/// 与 Lucene Block-Max WAND 的共同前提——**高频词项不能参与打分、更不能作为强约束**，
/// 否则候选集退化为全库，top-k 评估退化为全量评估。
/// </summary>
public sealed record LexicalQueryPlannerOptions
{
    /// <summary>语料小于该规模时不做剪枝（小库本来就在毫秒级，剪枝只会改变召回行为）。</summary>
    public int MinCorpusSizeForPruning { get; init; } = 256;

    /// <summary>文档频率占比超过该阈值即视为 common term（默认 15%，对齐 ES 的 cutoff_frequency 量级）。</summary>
    public double CommonTermCutoff { get; init; } = 0.15;

    /// <summary>参与 OR 的锚点词上限。</summary>
    public int MaxAnchorTerms { get; init; } = 12;

    /// <summary>全部词项都是 common 时，退化兜底保留的词数（保证"至少还能搜到东西"）。</summary>
    public int MaxCommonFallbackTerms { get; init; } = 3;

    /// <summary>相邻稀有词 NEAR 子句上限。</summary>
    public int MaxNearClauses { get; init; } = 3;

    /// <summary>是否启用 NEAR 短语层（仅在参与词全部稀有且至少两对相邻时启用）。</summary>
    public bool EnableNearTier { get; init; } = true;

    /// <summary>
    /// 候选集硬预算：锚点词的 df 之和（OR 的并集上界）不得超过语料的比例。
    /// 这是"查询耗时不得随语料线性增长"的可执行保证，而不只是一条设计原则。
    /// </summary>
    public double MaxCandidateRatio { get; init; } = 0.35;

    public static LexicalQueryPlannerOptions Default { get; } = new();
}

/// <summary>
/// 词法查询计划：把「用户输入 → FTS5 MATCH 表达式」这一步从字符串拼接升级为**带统计信息的规划**。
/// </summary>
public sealed record LexicalQueryPlan(
    IReadOnlyList<string> AnchorTerms,
    IReadOnlyList<string> PrunedCommonTerms,
    IReadOnlyList<string> NearClauses,
    IReadOnlyList<string> Queries,
    int CorpusSize,
    long EstimatedMatchedDocs,
    double CommonTermCutoff,
    bool UsedFallbackAnchors)
{
    public static LexicalQueryPlan Empty(string? rawQuery = null)
    {
        var queries = string.IsNullOrWhiteSpace(rawQuery)
            ? Array.Empty<string>()
            : new[] { LexicalQueryPlanner.QuotePhrase(rawQuery) };
        return new LexicalQueryPlan(
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            queries, 0, 0, 0, false);
    }

    /// <summary>候选集是否已经退化（估算命中超过语料一半）。用于门禁与埋点告警。</summary>
    public bool IsDegenerate => CorpusSize > 0 && EstimatedMatchedDocs * 2 >= CorpusSize;

    public string Describe()
    {
        var pruned = PrunedCommonTerms.Count == 0 ? "-" : string.Join("/", PrunedCommonTerms.Take(6));
        return $"anchors={AnchorTerms.Count} pruned={PrunedCommonTerms.Count}({pruned}) " +
               $"est={EstimatedMatchedDocs}/{CorpusSize} near={NearClauses.Count}" +
               (UsedFallbackAnchors ? " fallback=common" : "");
    }
}

/// <summary>
/// 词法查询计划器（Strategy + Adapter）。
///
/// 背景事故：15 字中文自然句「他笑嘻嘻的走上前去，拉着她的手」被 jieba 切成
/// 他/嘻嘻/笑嘻嘻/的/走上/前去/拉/着/她/手 十个词元，旧实现直接拼成
/// <c>"他" OR "嘻嘻" OR ... OR "手"</c>。其中「的」df=183%、「他」df=159%、「她」df=76%，
/// 导致 MATCH 命中 107,363/107,966 行（>100% 语料，因为还存在重复行）。
/// FTS5 必须先对**全部命中行**算 bm25 再排序，LIMIT 完全失效 →
/// 热缓存 0.9~1.1s/条、冷缓存 16.7s/条，三条查询叠加即用户看到的 18s 首查。
///
/// 本类的职责：用 df 统计把"全库命中"收敛成"稀有词锚点 + 候选预算内"，
/// 并且保留阶梯松弛（稀有 NEAR → 稀有 OR → common 兜底 → 原文短语）以免召回塌陷。
/// </summary>
public static class LexicalQueryPlanner
{
    public static LexicalQueryPlan Plan(
        string rawQuery,
        IReadOnlyList<string> orderedTokens,
        IReadOnlyDictionary<string, int> documentFrequency,
        int corpusSize,
        LexicalQueryPlannerOptions? options = null)
    {
        var opt = options ?? LexicalQueryPlannerOptions.Default;
        var tokens = Normalize(orderedTokens);
        if (tokens.Count == 0)
        {
            return LexicalQueryPlan.Empty(rawQuery);
        }

        // 小库 / 无统计：保持旧的"全词 OR"行为，零召回风险（本来也只要几毫秒）。
        if (corpusSize < opt.MinCorpusSizeForPruning || documentFrequency.Count == 0)
        {
            var legacy = Or(tokens);
            return new LexicalQueryPlan(
                tokens, Array.Empty<string>(), Array.Empty<string>(),
                new[] { legacy }, corpusSize, 0, opt.CommonTermCutoff, false);
        }

        int Df(string t) => documentFrequency.TryGetValue(t, out var v) && v > 0 ? v : 0;
        var cutoffDocs = opt.CommonTermCutoff * corpusSize;

        // 1) 稀有词 = 可做锚点；单字词（她/的/手/他）一律不进锚点层，中文单字在 BM25 里几乎没有区分度。
        var rareByDf = tokens
            .Where(t => t.Length >= 2 && Df(t) <= cutoffDocs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Df)
            .ThenByDescending(t => t.Length)
            .ToList();

        // 2) 候选集硬预算：按 df 升序贪心累加，并集上界不得超过 MaxCandidateRatio。
        //    至少保留 1 个（最稀有的那个），否则宁可返回空也不返回全库。
        var budget = Math.Max(1.0, opt.MaxCandidateRatio * corpusSize);
        var anchors = new List<string>();
        long estimated = 0;
        foreach (var term in rareByDf)
        {
            if (anchors.Count >= opt.MaxAnchorTerms) break;
            var df = Df(term);
            if (anchors.Count > 0 && estimated + df > budget) continue;
            anchors.Add(term);
            estimated += df;
        }

        var usedFallbackAnchors = false;
        if (anchors.Count == 0)
        {
            // 3) 全句都是虚词（如「她的他」）：退回 df 最小的若干词，保证"有结果"优先于"够快"。
            anchors = tokens
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(Df)
                .ThenByDescending(t => t.Length)
                .Take(opt.MaxCommonFallbackTerms)
                .ToList();
            estimated = anchors.Sum(t => (long)Df(t));
            usedFallbackAnchors = true;
        }

        var anchorSet = new HashSet<string>(anchors, StringComparer.OrdinalIgnoreCase);
        var pruned = tokens.Where(t => !anchorSet.Contains(t)).ToList();

        // 4) NEAR 层：只在"原句相邻且两词都稀有"时生成，避免用高频词做短语约束（那是 18s 的另一种形态）。
        var nearClauses = new List<string>();
        if (opt.EnableNearTier)
        {
            var orderedAnchors = tokens.Where(anchorSet.Contains).ToList();
            for (int i = 0; i < orderedAnchors.Count - 1 && nearClauses.Count < opt.MaxNearClauses; i++)
            {
                var a = orderedAnchors[i];
                var b = orderedAnchors[i + 1];
                if (a.Length < 2 || b.Length < 2) continue;
                nearClauses.Add($"NEAR(\"{Escape(a)}\" \"{Escape(b)}\", 5)");
            }
            if (nearClauses.Count < 2) nearClauses.Clear();
        }

        // 5) 阶梯查询：严格 → 放宽 → 原文短语（短语只作最后的保底，绝不与全词 OR 并列）。
        var queries = new List<string>();
        if (nearClauses.Count >= 2)
        {
            var head = nearClauses.Take(2).ToList();
            queries.Add($"({string.Join(" AND ", head)}) OR {string.Join(" OR ", nearClauses)}");
        }
        var orQuery = Or(anchors);
        if (!string.IsNullOrWhiteSpace(orQuery) && !queries.Contains(orQuery))
        {
            queries.Add(orQuery);
        }
        if (queries.Count == 0 && !string.IsNullOrWhiteSpace(rawQuery))
        {
            queries.Add(QuotePhrase(rawQuery));
        }

        return new LexicalQueryPlan(
            anchors, pruned, nearClauses, queries,
            corpusSize, Math.Min(corpusSize, estimated), opt.CommonTermCutoff, usedFallbackAnchors);
    }

    /// <summary>把一组词元拼成 FTS5 的 OR 表达式（每个词加引号锁定，防止被当成语法）。</summary>
    public static string Or(IEnumerable<string> terms)
        => string.Join(" OR ", terms
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => $"\"{Escape(t)}\""));

    public static string QuotePhrase(string text) => $"\"{Escape(text.Trim())}\"";

    private static string Escape(string text) => text.Replace("\"", "\"\"");

    private static List<string> Normalize(IReadOnlyList<string> tokens)
    {
        var result = new List<string>(tokens.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tokens)
        {
            var t = raw?.Trim();
            if (string.IsNullOrEmpty(t) || t.Length > 64) continue;
            if (!t.Any(char.IsLetterOrDigit)) continue;   // 丢弃纯标点
            if (seen.Add(t)) result.Add(t);
        }
        return result;
    }
}
