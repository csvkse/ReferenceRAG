using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Lexical;

namespace ReferenceRAG.Tests;

/// <summary>
/// 词法查询退化（Query Degeneration）护栏测试 —— 18 秒首查的本质门禁。
///
/// 事故形态：15 字中文自然句被 jieba 切成 10 个词元，旧实现直接拼成
/// <c>"他" OR "嘻嘻" OR "笑嘻嘻" OR "的" OR "走上" OR "前去" OR "拉" OR "着" OR "她" OR "手"</c>。
/// 生产库实测：「的」df=105,933(183%)、「他」df=92,100(159%)、「她」df=43,964(76%)，
/// MATCH 命中 107,363/107,966 行（>100% 语料，因为还有 50,233 行重复），
/// FTS5 必须对全部命中行计算 bm25 再排序 → 热缓存 0.9~1.1s/条，冷缓存 16.7s/条，
/// 三条查询叠加就是用户看到的 18s 首查。
///
/// 本文件锁定四条不变量：
///   G4 查询规划：高频词必须被剪枝，候选集必须有硬预算，且**不得随语料规模线性增长**；
///   G5 补齐幂等：并发/断点续传补齐不得产生重复行（FTS5 无唯一约束）；
///   G6 自愈：历史重复行必须能被一次性清理；
///   G7 正确性：两阶段召回（候选优先 + 回填）不得减少返回条数或打乱排序。
/// </summary>
public class LexicalQueryDegenerationGuardTests
{
    // ---------------- G4：查询规划 ----------------

    [Fact]
    public void G4_Planner_MustPruneHighFrequencyFunctionWords()
    {
        // 与生产事故完全同形的词元序列
        var tokens = new[] { "他", "嘻嘻", "笑嘻嘻", "的", "走上", "前去", "拉", "着", "她", "手" };
        const int corpus = 57_733;
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["他"] = 92_100, ["嘻嘻"] = 1_902, ["笑嘻嘻"] = 989, ["的"] = 105_933,
            ["走上"] = 1_853, ["前去"] = 1_936, ["拉"] = 4_060, ["着"] = 42_465,
            ["她"] = 43_964, ["手"] = 9_634
        };

        var plan = LexicalQueryPlanner.Plan("他笑嘻嘻的走上前去，拉着她的手", tokens, df, corpus);

        // 1) 虚词必须被剪掉（否则 MATCH 命中全库）
        Assert.DoesNotContain("的", plan.AnchorTerms);
        Assert.DoesNotContain("他", plan.AnchorTerms);
        Assert.DoesNotContain("她", plan.AnchorTerms);
        Assert.Contains("的", plan.PrunedCommonTerms);
        Assert.Contains("她", plan.PrunedCommonTerms);

        // 2) 稀有内容词必须被保留（否则召回塌陷）
        Assert.Contains("笑嘻嘻", plan.AnchorTerms);
        Assert.Contains("走上", plan.AnchorTerms);

        // 3) 表达式里绝不能出现被剪掉的虚词
        var expression = string.Join(" | ", plan.Queries);
        Assert.DoesNotContain("\"的\"", expression);
        Assert.DoesNotContain("\"她\"", expression);

        // 4) 候选集必须落在硬预算内（否则 bm25 又要对全库打分）
        Assert.True(plan.EstimatedMatchedDocs <= corpus * 0.35,
            $"候选集 {plan.EstimatedMatchedDocs}/{corpus} 超出预算，查询会退化为全库扫描");
        Assert.False(plan.IsDegenerate);
    }

    [Fact]
    public void G4_Planner_KeepsRarestAnchorWhenEveryTermIsCommon()
    {
        // 全虚词查询（如「她的他」）：宁可慢一点也必须"有结果"，不能返回空
        var tokens = new[] { "她", "的", "他" };
        const int corpus = 1_000;
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["她"] = 900, ["的"] = 990, ["他"] = 800
        };

        var plan = LexicalQueryPlanner.Plan("她的他", tokens, df, corpus);

        Assert.NotEmpty(plan.AnchorTerms);
        Assert.True(plan.UsedFallbackAnchors);
        Assert.Equal("他", plan.AnchorTerms[0]);          // df 最小者优先
        Assert.NotEmpty(plan.Queries);
    }

    [Fact]
    public void G4_Planner_SmallCorpusKeepsLegacyOrSemantics()
    {
        // 小库（<256 块）不做剪枝：本来就在毫秒级，剪枝只会改变召回行为
        var tokens = new[] { "的", "她", "剑法" };
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["的"] = 10, ["她"] = 9, ["剑法"] = 3
        };

        var plan = LexicalQueryPlanner.Plan("她的剑法", tokens, df, 20);

        Assert.Equal(tokens.Length, plan.AnchorTerms.Count);
        Assert.Contains("的", plan.AnchorTerms);
        Assert.Single(plan.Queries);
        Assert.Contains("\"剑法\"", plan.Queries[0]);
    }

    [Fact]
    public void G4_Planner_EmitsNearTierOnlyForRareAdjacentPairs()
    {
        var tokens = new[] { "未婚", "夫妻", "公主", "闺房" };
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["未婚"] = 20, ["夫妻"] = 30, ["公主"] = 25, ["闺房"] = 10
        };

        var plan = LexicalQueryPlanner.Plan("未婚夫妻公主闺房", tokens, df, 10_000);

        Assert.True(plan.NearClauses.Count >= 2, "稀有相邻词应生成 NEAR 层以提升短语精度");
        Assert.Contains("NEAR", plan.Queries[0]);
    }

    [Fact]
    public void G4_Planner_DoesNotUseHighFrequencyTermsAsPhraseConstraints()
    {
        // 「的/她」这类词即使相邻也绝不能进 NEAR（那等价于全库短语扫描）
        var tokens = new[] { "的", "她", "手" };
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["的"] = 9_000, ["她"] = 8_000, ["手"] = 7_000
        };

        var plan = LexicalQueryPlanner.Plan("的她手", tokens, df, 10_000);

        Assert.Empty(plan.NearClauses);
        Assert.DoesNotContain(plan.Queries, q => q.Contains("NEAR"));
    }

    // ---------------- G4：端到端"规模不敏感"门禁 ----------------

    [Fact]
    public async Task G4_Recall_MustNotMatchWholeCorpus_OnFunctionWordHeavyQuery()
    {
        var dir = NewDir("g4-corpus");
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g4", dbPath);
            await store.InitializeAsync();
            const int chunks = 3_000;
            await SeedFunctionWordHeavyCorpusAsync(store, chunks);

            var snapshot = await store.RecallAsync(new RecallRequest(
                "他笑嘻嘻的走上前去，拉着她的手", TopK: 10, UseLexicalTokenizer: true, AllowDegrade: true));

            // 命中集必须显著小于语料：一旦有人把"全词 OR"改回来，这里会瞬间变成 100%
            var matched = await ScalarAsync(dbPath,
                "SELECT COUNT(*) FROM fts_chunks_lexical WHERE fts_chunks_lexical MATCH @m",
                "\"他\" OR \"嘻嘻\" OR \"笑嘻嘻\" OR \"的\" OR \"走上\" OR \"前去\" OR \"拉\" OR \"着\" OR \"她\" OR \"手\"");
            Assert.True(matched > chunks * 0.8, $"对照组失效：全词 OR 本应命中几乎全库，实际 {matched}/{chunks}");

            // 规划后的真实查询：命中数必须被预算约束住
            var plan = LexicalQueryPlanner.Plan(
                "他笑嘻嘻的走上前去，拉着她的手",
                new[] { "他", "嘻嘻", "笑嘻嘻", "的", "走上", "前去", "拉", "着", "她", "手" },
                await ReadDfAsync(dbPath, new[] { "他", "嘻嘻", "笑嘻嘻", "的", "走上", "前去", "拉", "着", "她", "手" }),
                chunks);
            Assert.False(plan.IsDegenerate);
            Assert.True(plan.EstimatedMatchedDocs <= chunks * 0.35);

            // 召回仍然有结果（剪枝不能把召回剪没）
            Assert.NotEmpty(snapshot.Items);
            Assert.Contains(snapshot.Items, i => i.Text.Contains("笑嘻嘻"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public async Task G4_Recall_LatencyMustNotScaleLinearlyWithCorpus()
    {
        var small = await MeasureRecallAsync(2_000, "g4-small");
        var large = await MeasureRecallAsync(8_000, "g4-large");

        // 语料 4 倍，耗时必须基本不变（候选集被预算约束 → 与语料规模解耦）
        Assert.True(large < Math.Max(300, small * 3.0),
            $"语料 4 倍放大后耗时 {small}ms → {large}ms，疑似查询又退化为全库扫描");
    }

    /// <summary>
    /// G4-8：df 统计不得退化为「全词表扫描」。
    /// 历史实现用 <c>fts5vocab</c> 虚表按 term 过滤，而该虚表不支持 term 索引，
    /// 任何 <c>WHERE term IN (...)</c> 都是对 (词 × 文档) 全表的线性扫描：
    /// 生产 57,733 块 / ~1.7M 条记录实测 1,060ms —— 在修好 MATCH 之后它成了新的首查瓶颈
    /// （首查 1,214ms 里 1,060ms 是它，真正的 FTS 匹配只有 12ms）。
    /// 本门禁同时锁「结构」（源码不得再出现 fts5vocab）与「行为」（冷统计首查必须亚秒）。
    /// </summary>
    [Fact]
    public async Task G4_DfStatistics_MustNotScanWholeVocabulary()
    {
        // ── 结构约束 ────────────────────────────────────────────────────────
        // 测试内的 ReadDfAsync 仍用 fts5vocab 作为 ground truth，这里只约束生产实现。
        var sourcePath = Path.Combine(
            FindRepoRoot(),
            "Infrastructure", "ReferenceRAG.Infrastructure", "Features", "KnowledgeBase",
            "Storage", "SqliteKnowledgeBaseStore.cs");
        Assert.True(File.Exists(sourcePath), $"未找到存储实现: {sourcePath}");

        var source = File.ReadAllText(sourcePath);

        // 只约束可执行代码：注释里说明"为什么弃用 fts5vocab"是允许且必要的
        var codeLines = source
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith("*", StringComparison.Ordinal))
            .ToList();
        var code = string.Join("\n", codeLines);

        Assert.DoesNotContain("fts5vocab", code);
        Assert.Contains("LIMIT @cap", code);

        // ── 行为约束 ────────────────────────────────────────────────────────
        // 新库 = 新缓存键，因此这一次召回必须自己承担全部统计成本。
        var dir = NewDir("g4-df-cold");
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g4df", dbPath);
            await store.InitializeAsync();
            await SeedFunctionWordHeavyCorpusAsync(store, 8_000);

            var sw = Stopwatch.StartNew();
            var snapshot = await store.RecallAsync(new RecallRequest(
                "他笑嘻嘻的走上前去，拉着她的手", TopK: 10, UseLexicalTokenizer: true, AllowDegrade: true));
            sw.Stop();

            Assert.NotEmpty(snapshot.Items);
            Assert.True(sw.ElapsedMilliseconds < 1_000,
                $"冷统计首查 {sw.ElapsedMilliseconds}ms，疑似 df 统计又退化为全词表扫描");
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// G4-9：分词器预热必须挂在宿主启动上。
    /// 修好"全库 OR"与重复行之后，冷启动首查 1,837ms 里仍有 1,210ms 落在 <c>kb.lexical.plan</c>，
    /// 而 FTS 匹配只有 157ms —— 这笔成本是 jieba 词典（进程级静态单例）的首次加载。
    /// 它必须发生在启动期，而不是"用户第一次点检索"。
    /// </summary>
    [Fact]
    public void G4_SegmenterWarmup_MustBeRegisteredAtStartup()
    {
        var root = FindRepoRoot();

        var extensions = File.ReadAllText(Path.Combine(
            root, "Business", "ReferenceRAG.Business", "Features", "KnowledgeBase",
            "KnowledgeBaseBusinessExtensions.cs"));
        Assert.Contains("AddHostedService<LexicalSegmenterWarmupService>", extensions);

        var service = File.ReadAllText(Path.Combine(
            root, "Business", "ReferenceRAG.Business", "Features", "KnowledgeBase", "Services",
            "LexicalSegmenterWarmupService.cs"));
        Assert.Contains("LexicalSegmenterWarmup.Warm()", service);

        // 幂等 + 预热后分词必须是毫秒级
        LexicalSegmenterWarmup.Warm();
        LexicalSegmenterWarmup.Warm();
        Assert.True(LexicalSegmenterWarmup.IsWarmed);

        var sw = Stopwatch.StartNew();
        var tokens = new JiebaNet.Segmenter.JiebaSegmenter().CutForSearch("他笑嘻嘻的走上前去，拉着她的手");
        sw.Stop();

        Assert.NotEmpty(tokens);
        Assert.True(sw.ElapsedMilliseconds < 300,
            $"预热后分词仍耗时 {sw.ElapsedMilliseconds}ms，预热未生效");
    }

    // ---------------- G5：补齐幂等（重复行根因） ----------------

    [Fact]
    public async Task G5_ConcurrentBackfill_MustNotProduceDuplicateRows()
    {
        var dir = NewDir("g5-concurrent");
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var a = new SqliteKnowledgeBaseStore("kb_g5", dbPath);
            await a.InitializeAsync();
            // 1,500 块 = 3 个 500 行批次：必须跨批次才能暴露"两个写者从同一检查点出发"的重复插入
            await SeedFunctionWordHeavyCorpusAsync(a, 1_500);

            await ExecuteSqlAsync(dbPath, "DROP TABLE IF EXISTS fts_chunks_lexical");
            await ExecuteSqlAsync(dbPath, "UPDATE kb_manifest SET lexical_generation = 0, lexical_indexed_rowid = 0");

            using var b = new SqliteKnowledgeBaseStore("kb_g5", dbPath);
            await b.InitializeAsync();

            // 两个实例（模拟后台 Worker + 手动触发/对账作业）同时补齐
            await Task.WhenAll(
                a.BackfillLexicalIndexAsync(500),
                b.BackfillLexicalIndexAsync(500));

            var rows = await ScalarAsync(dbPath, "SELECT COUNT(*) FROM fts_chunks_lexical");
            var distinct = await ScalarAsync(dbPath, "SELECT COUNT(DISTINCT chunk_id) FROM fts_chunks_lexical");
            var chunks = await ScalarAsync(dbPath, "SELECT COUNT(*) FROM chunks");

            Assert.Equal(chunks, rows);
            Assert.Equal(distinct, rows);
        }
        finally { Cleanup(dir); }
    }

    // ---------------- G6：历史重复行自愈 ----------------

    [Fact]
    public async Task G6_Repair_ClearsHistoricalDuplicateAndOrphanRows()
    {
        var dir = NewDir("g6-repair");
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g6", dbPath);
            await store.InitializeAsync();
            await SeedFunctionWordHeavyCorpusAsync(store, 40);

            // 复刻生产形态：整表复制一遍（rowid 偏移一个批次），再塞一条孤儿行
            await ExecuteSqlAsync(dbPath,
                "INSERT INTO fts_chunks_lexical(chunk_id, document_id, title, heading, text) " +
                "SELECT chunk_id, document_id, title, heading, text FROM fts_chunks_lexical");
            await ExecuteSqlAsync(dbPath,
                "INSERT INTO fts_chunks_lexical(chunk_id, document_id, title, heading, text) " +
                "VALUES('ghost_chunk','ghost_doc','','','孤儿行')");

            var before = await ScalarAsync(dbPath, "SELECT COUNT(*) FROM fts_chunks_lexical");
            Assert.Equal(81, before);

            var removed = await store.RepairLexicalIndexAsync();
            Assert.Equal(41, removed);

            var after = await ScalarAsync(dbPath, "SELECT COUNT(*) FROM fts_chunks_lexical");
            var distinct = await ScalarAsync(dbPath, "SELECT COUNT(DISTINCT chunk_id) FROM fts_chunks_lexical");
            Assert.Equal(40, after);
            Assert.Equal(after, distinct);
            Assert.Equal(0, await ScalarAsync(dbPath,
                "SELECT COUNT(*) FROM fts_chunks_lexical WHERE chunk_id = 'ghost_chunk'"));
        }
        finally { Cleanup(dir); }
    }

    // ---------------- G7：两阶段召回的正确性 ----------------

    [Fact]
    public async Task G7_TwoPhaseRecall_ReturnsTopKInDescendingScoreOrder()
    {
        var dir = NewDir("g7-topk");
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g7", dbPath);
            await store.InitializeAsync();
            await SeedFunctionWordHeavyCorpusAsync(store, 2_000);

            var snapshot = await store.RecallAsync(new RecallRequest(
                "山水画与剑法", TopK: 10, UseLexicalTokenizer: true, AllowDegrade: true));

            Assert.Equal(10, snapshot.Items.Count);
            var scores = snapshot.Items.Select(i => i.Score).ToList();
            for (int i = 1; i < scores.Count; i++)
            {
                Assert.True(scores[i] <= scores[i - 1] + 1e-9,
                    $"第 {i} 条分数 {scores[i]} 高于前一条 {scores[i - 1]}，两阶段回填打乱了排序");
            }
            Assert.Equal(snapshot.Items.Count, snapshot.Items.Select(i => i.ChunkId).Distinct().Count());
        }
        finally { Cleanup(dir); }
    }

    // ---------------- helpers ----------------

    private async Task<long> MeasureRecallAsync(int chunkCount, string tag)
    {
        var dir = NewDir(tag);
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_" + tag, dbPath);
            await store.InitializeAsync();
            await SeedFunctionWordHeavyCorpusAsync(store, chunkCount);

            // 预热一次（排除首次 JIT / 连接建立的固定成本），再测第二次
            await store.RecallAsync(new RecallRequest(
                "他笑嘻嘻的走上前去，拉着她的手", TopK: 10, UseLexicalTokenizer: true, AllowDegrade: true));

            var sw = Stopwatch.StartNew();
            var snapshot = await store.RecallAsync(new RecallRequest(
                "他笑嘻嘻的走上前去，拉着她的手", TopK: 10, UseLexicalTokenizer: true, AllowDegrade: true));
            sw.Stop();

            Assert.NotEmpty(snapshot.Items);
            return sw.ElapsedMilliseconds;
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// 构造"虚词高频 + 内容词稀有"的中文语料：每块都含 他/她/的/手/着（df≈100%），
    /// 仅 1% 的块含「笑嘻嘻」，其余块含「山水画」「剑法」。
    /// 这正是小说/对话类知识库的真实分布，也是 18s 事故的语料条件。
    /// </summary>
    private static async Task SeedFunctionWordHeavyCorpusAsync(SqliteKnowledgeBaseStore store, int chunkCount)
    {
        const int perDoc = 200;
        for (int offset = 0; offset < chunkCount; offset += perDoc)
        {
            var take = Math.Min(perDoc, chunkCount - offset);
            var chunks = new List<ChunkData>(take);
            for (int i = 0; i < take; i++)
            {
                var idx = offset + i;
                var marker = idx % 100 == 0 ? "笑嘻嘻" : "山水画";
                var text = $"他看着她手上的剑，笑着说道这句话不要紧。{marker}第{idx}段，剑法随风而动。";
                chunks.Add(new ChunkData($"chunk_{idx}", idx, text, text.Length));
            }

            var manifest = await store.GetManifestAsync();
            var result = await store.CommitDocumentAsync(new IndexBatch(
                KnowledgeBaseId: "kb",
                ExpectedIndexEpoch: manifest.IndexEpoch,
                ExpectedDocumentVersion: 0,
                ConnectorId: "c1",
                SourceKey: $"doc_{offset}.md",
                Locator: $"/notes/doc_{offset}.md",
                SourceRevision: $"rev_{offset}",
                ContentHash: $"hash_{offset}",
                Chunks: chunks));
            Assert.True(result.Success, result.ErrorMessage);
        }
    }

    private static async Task<Dictionary<string, int>> ReadDfAsync(string dbPath, IReadOnlyList<string> terms)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS temp.v USING fts5vocab(main, 'fts_chunks_lexical', 'row')";
            await create.ExecuteNonQueryAsync();
        }
        foreach (var term in terms)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT doc FROM temp.v WHERE term = @t";
            cmd.Parameters.AddWithValue("@t", term);
            var value = await cmd.ExecuteScalarAsync();
            if (value != null && value != DBNull.Value) result[term] = Convert.ToInt32(value);
        }
        return result;
    }

    private static async Task ExecuteSqlAsync(string dbPath, string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string dbPath, string sql, string? match = null)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (match != null) cmd.Parameters.AddWithValue("@m", match);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static string NewDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到代码仓根目录 (ReferenceRAG.slnx)");
    }

    private static void Cleanup(string dir)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch
        {
            // 临时目录清理失败不影响断言结论
        }
    }
}
