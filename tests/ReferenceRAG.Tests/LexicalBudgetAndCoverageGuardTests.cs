using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Lexical;

namespace ReferenceRAG.Tests;

/// <summary>
/// 长句查询同类退化的 P0 门禁（G8 系列）。
///
/// 上一轮（G4）锁的是"高频词必须被剪枝"；这一轮锁的是**剪枝之后仍然存在的四类缺口**：
///   G8-1/2 候选预算必须有**绝对上限**——比例预算会随语料线性放大，1M 语料时 35% = 35 万行；
///           兜底层与召回地板层同样必须守预算（上一轮实测「她的他」est=45% 语料、181ms）；
///   G8-3/4 AND 分层必须存在且不得过严（多约束查询的唯一降本手段，实测 5000 命中 → 163）；
///   G8-5   覆盖率必须能作为排序主键（bm25 对"命中几个查询词"完全不敏感）；
///   G8-6   地板层结果必须被逐条标记（否则"宽松兜底"被伪装成"精确命中"）。
///
/// 方法论延续：**结构门禁优先于时间门禁**。能断言常量/结构就不要跑 1M 语料的行为测试，
/// 但每条结构门禁都必须配一个"若回归会怎样"的行为对照。
/// </summary>
public class LexicalBudgetAndCoverageGuardTests
{
    // ---------------- G8-1：候选预算绝对上限 ----------------

    [Fact]
    public void G8_CandidateBudget_MustHaveAbsoluteCap()
    {
        // 结构：绝对上限常量必须存在且为 5000
        Assert.Equal(5_000, LexicalQueryPlannerOptions.Default.MaxCandidateDocs);

        // 行为：100 万语料 + 12 个"每个 3000 文档"的稀有词。
        // 只有比例预算时 = 350,000 → 12 个词全部进锚点，estimated = 36,000，退化必然复现；
        // 有绝对上限时 → budget = 5,000，贪心只能留下第 1 个词（第 2 个就会超）。
        var tokens = Enumerable.Range(0, 12).Select(i => $"term{i:00}").ToArray();
        var df = tokens.ToDictionary(t => t, _ => 3_000, StringComparer.OrdinalIgnoreCase);

        var plan = LexicalQueryPlanner.Plan(string.Join(" ", tokens), tokens, df, 1_000_000);

        Assert.Equal(5_000, plan.CandidateBudget);
        Assert.True(plan.EstimatedMatchedDocs <= 5_000,
            $"1M 语料候选集 {plan.EstimatedMatchedDocs} 超出绝对上限，退化会原样复现");
        Assert.Single(plan.AnchorTerms);
        Assert.False(plan.BudgetExceeded);
    }

    // ---------------- G8-2：兜底层必须守预算 ----------------

    [Fact]
    public void G8_FallbackAnchors_MustRespectBudget()
    {
        // 纯虚词查询「她的他」：57,733 块的金庸类语料，三个词 df 全部远超 15% 阈值。
        // 上一轮行为：3 个词全进兜底 OR → est=25,983（45% 语料）→ kb.fts.match 181ms。
        var tokens = new[] { "她", "的", "他" };
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["她"] = 43_964, ["的"] = 105_933, ["他"] = 92_100
        };

        var plan = LexicalQueryPlanner.Plan("她的他", tokens, df, 57_733);

        Assert.True(plan.UsedFallbackAnchors);
        // 3 词之和 242,997 ≫ budget(5,000) → 必须收缩到 df 最小的 1 个词
        Assert.Single(plan.AnchorTerms);
        Assert.Equal("她", plan.AnchorTerms[0]);
        Assert.Equal(43_964, plan.EstimatedMatchedDocs);
        // 单个词的 df 仍然超预算 → 必须触发有界预取路径，否则 bm25 还是要对 4.4 万行打分
        Assert.True(plan.BudgetExceeded, "单词语料占比超预算时必须标记 BudgetExceeded");
        Assert.Contains("bounded=prefetch", plan.Describe());
    }

    // ---------------- G8-3：AND 层必须先于 OR 层 ----------------

    [Fact]
    public void G8_AndTier_MustPrecedeOrTier()
    {
        // 两个双字稀有词（避免触发 NEAR 层，让阶梯只剩 AND / OR 两层）
        var tokens = new[] { "江湖", "客栈" };
        var df = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["江湖"] = 800, ["客栈"] = 600
        };

        var plan = LexicalQueryPlanner.Plan("江湖客栈", tokens, df, 57_733);
        var list = plan.Queries.ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal("\"客栈\" AND \"江湖\"", list[0]);   // 按 df 升序 → 客栈在前
        Assert.Equal("\"客栈\" OR \"江湖\"", list[1]);

        var andIndex = list.FindIndex(q => q.Contains(" AND ") && !q.Contains(" OR "));
        var orIndex = list.FindIndex(q => !q.Contains(" AND ") && q.Contains(" OR "));
        Assert.True(andIndex >= 0, "缺少 AND 层");
        Assert.True(orIndex > andIndex, "AND 层必须排在纯 OR 层之前，否则更严格的那层永远不会被执行");
    }

    // ---------------- G8-4：AND 层不得过严（对照组） ----------------

    [Fact]
    public async Task G8_AndTier_MustNotReturnEmpty_WhenTermsCoexist()
    {
        var dir = NewDir("g8-and-coexist");
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g8and", dbPath);
            await store.InitializeAsync();
            await SeedFunctionWordHeavyCorpusAsync(store, 400);

            // 对照组：两个词在语料里确实共现 → AND 层必须非空。
            // 若有人把 AND 层写成了"所有词元（含虚词）AND"，这条会立刻变红。
            var hits = await ScalarAsync(dbPath,
                "SELECT COUNT(*) FROM fts_chunks_lexical WHERE fts_chunks_lexical MATCH '\"山水画\" AND \"剑法\"'");
            Assert.True(hits > 0, "两词共现时 AND 层返回 0 命中，说明 AND 层过严");

            var snapshot = await store.RecallAsync(new RecallRequest(
                "山水画剑法", TopK: 10, UseLexicalTokenizer: true, AllowDegrade: true));
            Assert.NotEmpty(snapshot.Items);
        }
        finally { Cleanup(dir); }
    }

    // ---------------- G8-5：覆盖率必须能当排序主键 ----------------

    [Fact]
    public async Task G8_Coverage_MustRankDescending()
    {
        var dir = NewDir("g8-coverage");
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g8cov", dbPath);
            await store.InitializeAsync();
            await SeedCoverageCorpusAsync(store, 400);

            // 诊断：先确认倒排索引里确实有这些词
            var total = await ScalarAsync(dbPath, "SELECT COUNT(*) FROM fts_chunks_lexical");
            var orHits = await ScalarAsync(dbPath,
                "SELECT COUNT(*) FROM fts_chunks_lexical WHERE fts_chunks_lexical MATCH '\"江湖\" OR \"客栈\" OR \"秘籍\"'");
            Assert.True(total > 0, $"词法索引为空（{total} 行），seed 未写入 fts_chunks_lexical");
            Assert.True(orHits > 0, $"OR 层 0 命中（索引 {total} 行），说明分词与索引不一致");

            var status = await store.GetLexicalIndexStatusAsync();
            Assert.True(status.Status is LexicalIndexStatus.Ready or LexicalIndexStatus.Empty,
                $"词法索引未就绪：{status.Status}");

            // 对照：单词查询必须非空（用于区分"索引/降级通道问题"与"阶梯表达式问题"）
            var single = await store.RecallAsync(new RecallRequest(
                "江湖", TopK: 10, UseLexicalTokenizer: true, AllowDegrade: true));
            Assert.NotEmpty(single.Items);

            var snapshot = await store.RecallAsync(new RecallRequest(
                "江湖 客栈 秘籍 师父 徒弟 内功", TopK: 10, UseLexicalTokenizer: true, AllowDegrade: true));

            Assert.NotEmpty(snapshot.Items);

            var coverages = snapshot.Items
                .Where(i => i.Coverage.HasValue)
                .Select(i => i.Coverage!.Value)
                .ToList();

            Assert.NotEmpty(coverages);   // 锚点 >= 3 时必须计算覆盖率
            // 非递增：覆盖率是主键
            for (int i = 1; i < coverages.Count; i++)
            {
                Assert.True(coverages[i] <= coverages[i - 1] + 1e-9,
                    $"覆盖率未作为主键排序：第 {i} 项 {coverages[i]:F2} > 第 {i - 1} 项 {coverages[i - 1]:F2}");
            }
            // 对照组：语料里存在"命中全部 6 个锚点"的块 → 必须出现 coverage == 1.0
            Assert.Contains(coverages, c => c >= 0.999);
        }
        finally { Cleanup(dir); }
    }

    // ---------------- G8-6：地板层结果必须被标记 ----------------

    [Fact]
    public async Task G8_FloorResult_MustBeFlagged()
    {
        var dir = NewDir("g8-floor-flag");
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g8floor", dbPath);
            await store.InitializeAsync();
            // 语料只含虚词，查询的内容词（笑嘻嘻/走上/前去）全部不存在 → 必然走地板层
            await SeedFunctionWordOnlyCorpusAsync(store, 400);

            var snapshot = await store.RecallAsync(new RecallRequest(
                "他笑嘻嘻的走上前去，拉着她的手", TopK: 10, UseLexicalTokenizer: true, AllowDegrade: true));

            Assert.NotEmpty(snapshot.Items);
            Assert.True(snapshot.UsedRecallFloor, "内容词全不存在时必须标记 UsedRecallFloor");
            Assert.All(snapshot.Items, i => Assert.True(i.IsFloor,
                "地板层结果必须逐条标记 IsFloor，否则宽松兜底会被当成精确命中"));
            // 覆盖率同样要能识别出"这些结果其实不匹配"（锚点内容词一个都不在正文里）
            Assert.All(snapshot.Items.Where(i => i.Coverage.HasValue), i =>
                Assert.True(i.Coverage <= 0.001, $"地板层候选覆盖率应接近 0，实际 {i.Coverage}"));
        }
        finally { Cleanup(dir); }
    }

    // ---------------- G8-7：有界预取必须端到端可用且不慢 ----------------

    [Fact]
    public async Task G8_BoundedPrefetch_MustStayFast_OnPureFunctionWordQuery()
    {
        var dir = NewDir("g8-bounded");
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g8bound", dbPath);
            await store.InitializeAsync();
            await SeedFunctionWordHeavyCorpusAsync(store, 2_000);

            // 预热
            await store.RecallAsync(new RecallRequest(
                "她的他", TopK: 10, UseLexicalTokenizer: true, AllowDegrade: true));

            var sw = Stopwatch.StartNew();
            var snapshot = await store.RecallAsync(new RecallRequest(
                "她的他", TopK: 10, UseLexicalTokenizer: true, AllowDegrade: true));
            sw.Stop();

            Assert.NotEmpty(snapshot.Items);
            // 纯虚词查询在 2,000 块语料上必须远低于全库 bm25 打分的量级。
            // 阈值取 800ms 是给 CI 抖动留余量（实测个位数毫秒）；真正会红的场景是
            // 有人把 BudgetExceeded → bounded prefetch 的分支删掉，退回全量打分。
            Assert.True(sw.ElapsedMilliseconds < 800,
                $"纯虚词查询耗时 {sw.ElapsedMilliseconds}ms，疑似退回全量 bm25 打分");
        }
        finally { Cleanup(dir); }
    }

    // ---------------- G8-8：两条召回路径的列序契约 ----------------

    [Theory]
    [InlineData(60)]    // 小库 → 走 bm25 排序路径
    [InlineData(400)]   // 400 块全含查询词 → est 超预算 → 走有界预取路径
    public async Task G8_RecallItemColumns_MustNotShift(int chunkCount)
    {
        // 历史真实 bug：bm25 路径的 f.score 在第 4 列，而有界预取路径第 4 列是 document_version。
        // 两条路径共用 ReadRecallItem 后，ConnectorId 读到了 document_version、StartLine 读到了 locator，
        // 表现是"授权过滤把所有结果判为无权限 → 检索静默返回空"和"行号恒为 0"。
        var dir = NewDir($"g8-columns-{chunkCount}");
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore($"kb_g8col{chunkCount}", dbPath);
            await store.InitializeAsync();

            const int perDoc = 200;
            for (int offset = 0; offset < chunkCount; offset += perDoc)
            {
                var take = Math.Min(perDoc, chunkCount - offset);
                var chunks = new List<ChunkData>(take);
                for (int i = 0; i < take; i++)
                {
                    var idx = offset + i;
                    var text = $"剑法随风而动第{idx}段。";
                    chunks.Add(new ChunkData($"chunk_{idx}", idx, text, text.Length,
                        StartLine: 5, EndLine: 9));
                }
                var manifest = await store.GetManifestAsync();
                var result = await store.CommitDocumentAsync(new IndexBatch(
                    KnowledgeBaseId: "kb",
                    ExpectedIndexEpoch: manifest.IndexEpoch,
                    ExpectedDocumentVersion: 0,
                    ConnectorId: "conn-xyz",
                    SourceKey: $"doc_{offset}.md",
                    Locator: $"/notes/doc_{offset}.md",
                    SourceRevision: $"rev_{offset}",
                    ContentHash: $"hash_{offset}",
                    Chunks: chunks));
                Assert.True(result.Success, result.ErrorMessage);
            }

            var snapshot = await store.RecallAsync(new RecallRequest(
                "剑法", TopK: 5, UseLexicalTokenizer: true, AllowDegrade: true));

            Assert.NotEmpty(snapshot.Items);
            Assert.All(snapshot.Items, i =>
            {
                Assert.Equal("conn-xyz", i.ConnectorId);   // 错位时会读到 document_version
                Assert.Equal(5, i.StartLine);              // 错位时会读到 locator 或 0
                Assert.Equal(9, i.EndLine);
            });
        }
        finally { Cleanup(dir); }
    }

    // ---------------- helpers ----------------

    /// <summary>
    /// 构造"虚词高频 + 内容词稀有"的语料：每块都含 他/她/的/手/着（df≈100%），
    /// 仅 1% 的块含「笑嘻嘻」，其余含「山水画」「剑法」。
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

    /// <summary>构造"只有虚词、没有查询内容词"的语料（不含 笑嘻嘻/走上/前去/拉）。</summary>
    private static async Task SeedFunctionWordOnlyCorpusAsync(SqliteKnowledgeBaseStore store, int chunkCount)
    {
        const int perDoc = 200;
        for (int offset = 0; offset < chunkCount; offset += perDoc)
        {
            var take = Math.Min(perDoc, chunkCount - offset);
            var chunks = new List<ChunkData>(take);
            for (int i = 0; i < take; i++)
            {
                var idx = offset + i;
                var text = $"他看着她手上的剑，笑着说道这句话不要紧。山水画第{idx}段，剑法随风而动。";
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

    /// <summary>
    /// 构造"覆盖率有区分度"的语料，三条约束缺一不可：
    /// <list type="number">
    /// <item>锚点必须**稀有**（df &lt; 15% 语料），否则会被判成 common term 剪掉，覆盖率无从计算；</item>
    /// <item>锚点在正文里必须**不相邻**（间隔 &gt; 5 个词元），否则 NEAR 层会一次填满 topK，OR 层永远不执行；</item>
    /// <item>必须同时存在 coverage=6/6 与 1/6 的块，否则"降序"无法验证。</item>
    /// </list>
    /// </summary>
    private static async Task SeedCoverageCorpusAsync(SqliteKnowledgeBaseStore store, int chunkCount)
    {
        const int perDoc = 200;
        // 「剑法随风而动得非常快而且稳」= 10 个词元，确保任意两个锚点间隔 > NEAR 的 5
        const string filler = "剑法随风而动得非常快而且稳";
        for (int offset = 0; offset < chunkCount; offset += perDoc)
        {
            var take = Math.Min(perDoc, chunkCount - offset);
            var chunks = new List<ChunkData>(take);
            for (int i = 0; i < take; i++)
            {
                var idx = offset + i;
                string text;
                if (idx % 80 == 0)
                {
                    // 覆盖率 6/6
                    text = $"江湖，{filler}，客栈，{filler}，秘籍，{filler}，师父，徒弟，内功。第{idx}段。";
                }
                else if (idx % 80 == 40)
                {
                    // 覆盖率 1/6
                    text = $"江湖，{filler}。第{idx}段。";
                }
                else
                {
                    // 不含任何锚点
                    text = $"{filler}。第{idx}段。";
                }
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

    private static async Task<long> ScalarAsync(string dbPath, string sql, string? match = null)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (match != null) cmd.Parameters.AddWithValue("@match", match);
        var value = await cmd.ExecuteScalarAsync();
        return value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
    }

    private static string NewDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"refrag_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 忽略清理失败 */ }
    }
}
