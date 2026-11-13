using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Tracing;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Tests;

/// <summary>
/// 词法首查延迟 & 耗时链护栏测试。
///
/// 背景：历史事故中「首次词法查询耗时 16 秒」，根因是读路径 <c>RecallAsync</c> 在请求线程内
/// 同步执行 <c>CREATE VIRTUAL TABLE</c> + 全量 jieba 分词 + 逐行 INSERT。
/// 本测试文件锁定三条不变量，防止回归：
///   G1 读路径不得填充词法索引（只允许投递补齐意图）；
///   G2 写入路径必须无条件双写词法索引（不得依赖"表是否已存在"这类自锁条件）；
///   G3 耗时链必须能正确计算 SelfMs / 未归因时间 / 分类归因。
/// </summary>
public class LexicalFirstQueryLatencyGuardTests
{
    // ---------------- G1：读路径不得构建词法索引 ----------------

    [Fact]
    public async Task G1_ReadPath_MustNotPopulateLexicalIndex_WhenIndexMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-g1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g1", dbPath);
            await store.InitializeAsync();
            await CommitAsync(store, "doc1.md", "chunk_g1_1", "ReferenceRAG 词法倒排索引首查延迟优化方案");
            await CommitAsync(store, "doc2.md", "chunk_g1_2", "知识库向量检索降级通道与状态机");

            // 模拟"升级前的历史库"：词法表被删、manifest 代次归零
            await ExecuteSqlAsync(dbPath, "DROP TABLE IF EXISTS fts_chunks_lexical");
            await ExecuteSqlAsync(dbPath, "UPDATE kb_manifest SET lexical_generation = 0, lexical_indexed_rowid = 0");

            var before = await store.GetLexicalIndexStatusAsync();
            Assert.NotEqual(LexicalIndexStatus.Ready, before.Status);
            Assert.Equal(0, before.LexicalGeneration);

            var sw = Stopwatch.StartNew();
            var snapshot = await store.RecallAsync(new RecallRequest(
                "词法索引", TopK: 5, UseLexicalTokenizer: true, AllowDegrade: true));
            sw.Stop();

            // 1) 读路径只做 O(1) 裁决 + 降级召回，绝不填充索引
            var after = await store.GetLexicalIndexStatusAsync();
            Assert.NotEqual(LexicalIndexStatus.Ready, after.Status);
            Assert.Equal(0, after.LexicalGeneration);   // 代次未推进 == 未构建
            Assert.Equal(0, after.IndexedCount);        // 一行都没写
            Assert.True(sw.ElapsedMilliseconds < 3000,
                $"读路径耗时 {sw.ElapsedMilliseconds}ms，疑似又在请求线程内建索引（护栏上限 3000ms）");

            // 2) 必须如实暴露降级通道与原因，而不是静默返回空结果
            Assert.True(snapshot.Degraded);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.DegradationReason));
            Assert.Contains("词法索引", snapshot.DegradationReason);
            Assert.NotEmpty(snapshot.Items);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task G1_Backfill_MustBeIdempotentAndReachReady()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-g1b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g1b", dbPath);
            await store.InitializeAsync();
            await CommitAsync(store, "doc1.md", "chunk_b1", "词法索引补齐应当分批且可断点续传");
            await ExecuteSqlAsync(dbPath, "DROP TABLE IF EXISTS fts_chunks_lexical");
            await ExecuteSqlAsync(dbPath, "UPDATE kb_manifest SET lexical_generation = 0, lexical_indexed_rowid = 0");

            var absent = await store.GetLexicalIndexStatusAsync();
            Assert.NotEqual(LexicalIndexStatus.Ready, absent.Status);

            var first = await store.BackfillLexicalIndexAsync(500);
            Assert.True(first.Completed, first.Message);

            var ready = await store.GetLexicalIndexStatusAsync();
            Assert.Equal(LexicalIndexStatus.Ready, ready.Status);
            Assert.Equal(ready.ContentGeneration, ready.LexicalGeneration);

            // 幂等：重复补齐不应报错，也不应破坏已就绪状态
            var second = await store.BackfillLexicalIndexAsync(500);
            Assert.True(second.Completed, second.Message);
            Assert.Equal(LexicalIndexStatus.Ready, (await store.GetLexicalIndexStatusAsync()).Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task G1_Backfill_FastForwardsWhenIndexAlreadyComplete_WithoutDuplicatingRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-g1c-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g1c", dbPath);
            await store.InitializeAsync();
            await CommitAsync(store, "doc1.md", "chunk_c1", "升级兼容：历史库词法索引已完整");
            await CommitAsync(store, "doc2.md", "chunk_c2", "不应重复分词也不应产生重复行");

            // 模拟升级：代次列是新增的，历史库初值为 0，但 fts 内容其实是完整的
            await ExecuteSqlAsync(dbPath, "UPDATE kb_manifest SET lexical_generation = 0, lexical_indexed_rowid = 0");
            Assert.NotEqual(LexicalIndexStatus.Ready, (await store.GetLexicalIndexStatusAsync()).Status);

            var result = await store.BackfillLexicalIndexAsync(500);
            Assert.True(result.Completed, result.Message);
            Assert.Equal(0, result.Indexed);   // 快路径：一行都不需要重新分词

            Assert.Equal(LexicalIndexStatus.Ready, (await store.GetLexicalIndexStatusAsync()).Status);
            // 关键：不得产生重复行（FTS5 无唯一约束，重复插入会让检索结果出现同一分块多次）
            Assert.Equal(2, await ScalarAsync(dbPath, "SELECT COUNT(*) FROM fts_chunks_lexical"));
            Assert.Equal(2, await ScalarAsync(dbPath, "SELECT COUNT(DISTINCT chunk_id) FROM fts_chunks_lexical"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dir);
        }
    }

    // ---------------- G2：写入路径必须无条件双写 ----------------

    [Fact]
    public async Task G2_CommitDocument_MustDoubleWriteLexicalIndex_WithoutPrecheck()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-g2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g2", dbPath);
            await store.InitializeAsync();

            // 空库应为 Empty，而不是 Absent（对称建表生效）
            Assert.Equal(LexicalIndexStatus.Empty, (await store.GetLexicalIndexStatusAsync()).Status);

            await CommitAsync(store, "doc1.md", "chunk_g2_1", "提交文档时必须无条件双写词法倒排索引");

            var status = await store.GetLexicalIndexStatusAsync();
            Assert.Equal(LexicalIndexStatus.Ready, status.Status);
            Assert.Equal(status.ContentGeneration, status.LexicalGeneration);
            Assert.True(status.IndexedRowId > 0);
            Assert.Equal(100, status.ProgressPercent);

            // 二次写入后仍保持就绪（代次同步推进）
            await CommitAsync(store, "doc2.md", "chunk_g2_2", "第二次写入同样必须双写词法索引");
            var status2 = await store.GetLexicalIndexStatusAsync();
            Assert.Equal(LexicalIndexStatus.Ready, status2.Status);
            Assert.Equal(status2.ContentGeneration, status2.LexicalGeneration);

            // 真实命中验证：索引可用时不应降级
            var snapshot = await store.RecallAsync(new RecallRequest(
                "双写", TopK: 5, UseLexicalTokenizer: true, AllowDegrade: true));
            Assert.False(snapshot.Degraded);
            Assert.Contains(snapshot.Items, i => i.ChunkId == "chunk_g2_1");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task G2_ForceRebuild_ClearsHistoricalDuplicateRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-g2b-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "kb.db");
        try
        {
            using var store = new SqliteKnowledgeBaseStore("kb_g2b", dbPath);
            await store.InitializeAsync();
            await CommitAsync(store, "doc1.md", "chunk_d1", "强制重建应清理历史重复行");
            await CommitAsync(store, "doc2.md", "chunk_d2", "FTS5 无唯一约束容易产生重复");

            // 模拟历史缺陷留下的重复行（同一 chunk_id 出现两次）
            await ExecuteSqlAsync(dbPath,
                "INSERT INTO fts_chunks_lexical(chunk_id, document_id, title, heading, text) " +
                "SELECT chunk_id, document_id, title, heading, text FROM fts_chunks_lexical");
            Assert.Equal(4, await ScalarAsync(dbPath, "SELECT COUNT(*) FROM fts_chunks_lexical"));

            var result = await store.BackfillLexicalIndexAsync(500, null, default, force: true);
            Assert.True(result.Completed, result.Message);
            Assert.Equal(2, result.Indexed);
            Assert.Equal(2, await ScalarAsync(dbPath, "SELECT COUNT(*) FROM fts_chunks_lexical"));
            Assert.Equal(2, await ScalarAsync(dbPath, "SELECT COUNT(DISTINCT chunk_id) FROM fts_chunks_lexical"));
            Assert.Equal(LexicalIndexStatus.Ready, (await store.GetLexicalIndexStatusAsync()).Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(dir);
        }
    }

    // ---------------- G3：耗时链归因正确性 ----------------

    [Fact]
    public void G3_TimingChain_ComputesSelfMsAndCategoryAttribution()
    {
        var builder = SearchTimeline.BeginRequest(out var scope);
        try
        {
            using (var root = TimingScope.Begin("request.search", SpanCategory.Request, "检索请求"))
            {
                using (var phase = TimingScope.Begin("phase.retrieve", SpanCategory.Phase, "召回"))
                {
                    using (var io = TimingScope.Begin("kb.fts.match", SpanCategory.Io, "FTS5 匹配"))
                    {
                        Thread.Sleep(25);
                    }
                    Thread.Sleep(10);
                }
            }

            var chain = builder.Complete();

            Assert.Equal(3, chain.Spans.Count);
            var rootSpan = chain.Spans.Single(s => s.Name == "request.search");
            var phaseSpan = chain.Spans.Single(s => s.Name == "phase.retrieve");
            var ioSpan = chain.Spans.Single(s => s.Name == "kb.fts.match");

            // 层级：ParentId 串成树，而非靠顺序猜测
            Assert.Null(rootSpan.ParentId);
            Assert.Equal(rootSpan.Id, phaseSpan.ParentId);
            Assert.Equal(phaseSpan.Id, ioSpan.ParentId);

            // SelfMs = 自身耗时 - 子 Span 耗时
            Assert.Equal(Math.Max(0, phaseSpan.DurationMs - ioSpan.DurationMs), phaseSpan.SelfMs);
            Assert.Equal(ioSpan.DurationMs, ioSpan.SelfMs);

            // 分类归因
            Assert.Equal(SpanCategory.Io, ioSpan.Category);
            Assert.True(chain.Summary!.IoMs >= 20, $"IoMs={chain.Summary.IoMs}");
            Assert.Equal(chain.Summary.IoMs, ioSpan.DurationMs);

            // 旧契约投影：SearchPhaseStats 字段语义不变
            var phases = PhaseProjector.Project(chain);
            Assert.Equal(ioSpan.DurationMs, phases.Bm25Ms);
            Assert.Equal(ioSpan.DurationMs, phases.IoMs);
            Assert.Equal(chain.UnattributedMs, phases.UnattributedMs);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public void G3_TimingChain_ExposesUnattributedTime()
    {
        var builder = SearchTimeline.BeginRequest(out var scope);
        try
        {
            using (TimingScope.Begin("phase.assemble", SpanCategory.Assembly, "结果装配"))
            {
                Thread.Sleep(10);
            }

            // 人为把总耗时拉大到 5000ms：差额必须出现在"未归因"里，不允许被吞掉
            var chain = builder.Complete(5000);
            Assert.Equal(5000, chain.TotalMs);
            Assert.True(chain.UnattributedMs >= 4900, $"UnattributedMs={chain.UnattributedMs}");
            Assert.Equal("正常（长尾）", chain.Summary!.Verdict);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public void G3_TimingScope_IsNoOpOutsideRequest()
    {
        // 未开启请求级耗时链时，埋点必须是零开销的 no-op（不能抛异常、不能残留状态）
        var scope = TimingScope.Begin("kb.fts.match", SpanCategory.Io, "FTS5 匹配");
        Assert.False(scope.IsActive);
        scope.SetInput(1);
        scope.SetOutput(1);
        scope.SetDetail("noop");
        scope.SetWait(5);
        scope.AddAttribute("k", "v");
        scope.Dispose();
        Assert.False(SearchTimeline.IsActive);
    }

    [Fact]
    public void G3_TimingChain_ClassifiesIndexBuildAsColdStart()
    {
        var builder = SearchTimeline.BeginRequest(out var scope);
        try
        {
            using (var build = TimingScope.Begin("kb.lexical-index.backfill", SpanCategory.Io, "词法索引补齐"))
            {
                build.MarkColdStart();
                Thread.Sleep(20);
            }

            var chain = builder.Complete();
            Assert.True(chain.Summary!.IndexBuildMs >= 15, $"IndexBuildMs={chain.Summary.IndexBuildMs}");
            Assert.True(chain.Summary.ColdStartMs >= 15, $"ColdStartMs={chain.Summary.ColdStartMs}");
            Assert.Equal("词法索引构建（冷启动）", chain.Summary.Verdict);
            Assert.True(chain.Spans.Single().ColdStart);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public void G3_TimingChain_AggregatesModelLoadInsteadOfSwallowingIt()
    {
        // 真实事故形态：总 6227ms，向量推理 5636ms，但摘要只报 IO 589ms + CPU 0ms，
        // 判定还给「正常（长尾）」—— 用户看到的是"时间凭空消失"。
        var builder = SearchTimeline.BeginRequest(out var scope);
        try
        {
            using (TimingScope.Begin("embed.infer", SpanCategory.ModelLoad, "查询向量推理"))
            {
                Thread.Sleep(30);
            }

            using (TimingScope.Begin("kb.fts.match", SpanCategory.Io, "FTS5 匹配"))
            {
                Thread.Sleep(5);
            }

            var chain = builder.Complete();
            var summary = chain.Summary!;

            Assert.True(summary.ModelLoadMs >= 25, $"ModelLoadMs={summary.ModelLoadMs}");
            Assert.Equal(chain.Spans.Single(s => s.Name == "embed.infer").SelfMs, summary.ModelLoadMs);

            // 纯检索必须扣掉模型推理，否则"算法真实耗时"被模型慢掩盖
            Assert.True(summary.PureRetrievalMs < summary.ModelLoadMs + summary.IoMs + 60,
                $"PureRetrievalMs={summary.PureRetrievalMs} 未扣除模型推理");

            // 未标记 ColdStart 的推理慢也必须被判定出来（不能只有显式 MarkColdStart 才认）
            Assert.Equal("模型推理 / 加载", summary.Verdict);
        }
        finally
        {
            scope.Dispose();
        }
    }

    [Fact]
    public void G3_TimingChain_ModelLoadDoesNotDoubleSubtractColdStart()
    {
        // 冷启动标记通常就是模型首次加载：ColdStart 是 ModelLoad 的子集。
        // 若两者相加扣减，会把"纯检索"扣成负数并被 Math.Max 抹成 0，归因反而失真。
        var builder = SearchTimeline.BeginRequest(out var scope);
        try
        {
            using (var load = TimingScope.Begin("embed.infer", SpanCategory.ModelLoad, "查询向量推理"))
            {
                load.MarkColdStart();
                Thread.Sleep(20);
            }

            var chain = builder.Complete();
            var summary = chain.Summary!;

            Assert.True(summary.ColdStartMs >= 15, $"ColdStartMs={summary.ColdStartMs}");
            Assert.True(summary.ModelLoadMs >= 15, $"ModelLoadMs={summary.ModelLoadMs}");
            Assert.True(summary.PureRetrievalMs >= 0, $"PureRetrievalMs={summary.PureRetrievalMs}");
            Assert.Equal("冷启动", summary.Verdict);
        }
        finally
        {
            scope.Dispose();
        }
    }

    // ---------------- helpers ----------------

    private static async Task CommitAsync(
        SqliteKnowledgeBaseStore store, string sourceKey, string chunkId, string text)
    {
        var manifest = await store.GetManifestAsync();
        var result = await store.CommitDocumentAsync(new IndexBatch(
            KnowledgeBaseId: "kb",
            ExpectedIndexEpoch: manifest.IndexEpoch,
            ExpectedDocumentVersion: 0,
            ConnectorId: "c1",
            SourceKey: sourceKey,
            Locator: "/notes/" + sourceKey,
            SourceRevision: "rev-" + chunkId,
            ContentHash: "hash-" + chunkId,
            Chunks: new[] { new ChunkData(chunkId, 0, text, text.Length) }));
        Assert.True(result.Success, result.ErrorMessage);
    }

    private static async Task ExecuteSqlAsync(string dbPath, string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarAsync(string dbPath, string sql)
    {
        await using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static void TryDelete(string dir)
    {
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
