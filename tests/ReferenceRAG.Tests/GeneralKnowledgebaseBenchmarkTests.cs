using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReferenceRAG.Business.Features.Search.Benchmark.Adapters;
using ReferenceRAG.Business.Features.Search.Benchmark.Contracts;
using ReferenceRAG.Business.Features.Search.Benchmark.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Infrastructure.Features.Search.Fusion;
using ReferenceRAG.Infrastructure.Features.Search.Lexical;

namespace ReferenceRAG.Tests;

public class GeneralKnowledgebaseBenchmarkTests : IDisposable
{
    private readonly string _tempDir;

    public GeneralKnowledgebaseBenchmarkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rag_benchmark_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    #region 1. Benchmark Adapters & Metrics Tests

    [Fact]
    public async Task HotpotQaDatasetAdapter_StreamsCasesCorrectly()
    {
        var sampleJsonl = """
            {"input":"Which magazine was started first, Arthur's Magazine or First for Women?","context":"Passage 1:\nArthur's Magazine\nArthur's Magazine (1844-1846) was an American literary magazine.\nPassage 2:\nFirst for Women\nFirst for Women is a woman's magazine published by Bauer Media Group in the USA since 1989.","answers":["Arthur's Magazine"],"length":200,"dataset":"hotpotqa","language":"en","all_classes":null,"_id":"q1"}
            """;
        var samplePath = Path.Combine(_tempDir, "hotpot_sample.jsonl");
        await File.WriteAllTextAsync(samplePath, sampleJsonl);

        var adapter = new HotpotQaDatasetAdapter(samplePath);
        Assert.Equal("HotpotQA", adapter.DatasetName);

        var cases = new List<BenchmarkQuestionCase>();
        await foreach (var c in adapter.StreamTestCasesAsync())
        {
            cases.Add(c);
        }

        Assert.Single(cases);
        var q = cases[0];
        Assert.Equal("q1", q.QuestionId);
        Assert.Contains("Which magazine was started first", q.Query);
        Assert.Equal("Arthur's Magazine", q.GroundTruthAnswer);
        Assert.Equal(BenchmarkQuestionType.MultiHopComparison, q.QuestionType);
        Assert.NotNull(q.ContextCandidates);
        Assert.Equal(2, q.ContextCandidates.Count);
    }

    [Fact]
    public async Task Cmrc2018DatasetAdapter_StreamsCasesCorrectly()
    {
        var sampleJson = """
            [
              {
                "context_id": "DEV_0",
                "title": "战国无双3",
                "context_text": "《战国无双3》是由光荣和ω-force开发的战国无双系列正统续作。",
                "qas": [
                  {
                    "query_id": "DEV_0_QUERY_0",
                    "query_text": "《战国无双3》是由哪两个公司开发的？",
                    "answers": ["光荣和ω-force"]
                  }
                ]
              }
            ]
            """;
        var samplePath = Path.Combine(_tempDir, "cmrc_sample.json");
        await File.WriteAllTextAsync(samplePath, sampleJson);

        var adapter = new Cmrc2018DatasetAdapter(samplePath);
        Assert.Equal("CMRC-2018", adapter.DatasetName);

        var cases = new List<BenchmarkQuestionCase>();
        await foreach (var c in adapter.StreamTestCasesAsync())
        {
            cases.Add(c);
        }

        Assert.Single(cases);
        var q = cases[0];
        Assert.Equal("DEV_0_QUERY_0", q.QuestionId);
        Assert.Contains("战国无双3", q.Query);
        Assert.Equal("光荣和ω-force", q.GroundTruthAnswer);
        Assert.Equal(BenchmarkQuestionType.SingleHop, q.QuestionType);
        Assert.NotNull(q.ContextCandidates);
        Assert.Single(q.ContextCandidates);
        Assert.Contains("战国无双系列", q.ContextCandidates[0].FullText);
    }

    [Fact]
    public async Task RealDownloadedBenchmarkFiles_CanBeStreamedSuccessfully()
    {
        var hotpotPath = Path.GetFullPath("env/data/benchmark/hotpotqa/hotpotqa_dev.jsonl");
        if (File.Exists(hotpotPath))
        {
            var adapter = new HotpotQaDatasetAdapter(hotpotPath);
            int count = 0;
            await foreach (var testCase in adapter.StreamTestCasesAsync())
            {
                Assert.NotEmpty(testCase.QuestionId);
                Assert.NotEmpty(testCase.Query);
                count++;
                if (count >= 5) break; // Verify first 5 stream smoothly
            }
            Assert.Equal(5, count);
        }

        var cmrcPath = Path.GetFullPath("env/data/benchmark/cmrc2018/cmrc2018_dev.json");
        if (File.Exists(cmrcPath))
        {
            var adapter = new Cmrc2018DatasetAdapter(cmrcPath);
            int count = 0;
            await foreach (var testCase in adapter.StreamTestCasesAsync())
            {
                Assert.NotEmpty(testCase.QuestionId);
                Assert.NotEmpty(testCase.Query);
                count++;
                if (count >= 5) break;
            }
            Assert.Equal(5, count);
        }
    }

    [Fact]
    public void BenchmarkMetricsCalculator_ComputesAccurateMetrics()
    {
        var dummyCase1 = new BenchmarkQuestionCase("c1", "q1", BenchmarkQuestionType.SingleHop, new[] { "doc1" }, new[] { "Evidence1" });
        var dummyCase2 = new BenchmarkQuestionCase("c2", "q2", BenchmarkQuestionType.MultiHopBridge, new[] { "doc2" }, new[] { "Evidence2" });
        var dummyCase3 = new BenchmarkQuestionCase("c3", "q3", BenchmarkQuestionType.SingleHop, new[] { "missing" }, new[] { "Evidence3" });

        var results = new List<QueryEvaluationResult>
        {
            // Case 1: Gold doc ranked at #1
            new QueryEvaluationResult(
                QuestionId: "c1",
                TestCase: dummyCase1,
                RetrievedChunkIdsOrTitles: new[] { "doc1", "doc2", "doc3" },
                LatencyMs: 10.0,
                Success: true
            ),
            // Case 2: Gold doc ranked at #2
            new QueryEvaluationResult(
                QuestionId: "c2",
                TestCase: dummyCase2,
                RetrievedChunkIdsOrTitles: new[] { "other", "doc2", "doc3" },
                LatencyMs: 20.0,
                Success: true
            ),
            // Case 3: Gold doc missing
            new QueryEvaluationResult(
                QuestionId: "c3",
                TestCase: dummyCase3,
                RetrievedChunkIdsOrTitles: new[] { "x1", "x2" },
                LatencyMs: 30.0,
                Success: true
            )
        };

        var metrics = BenchmarkMetricsCalculator.CalculateMetrics(results, totalDurationSeconds: 1.0);
        Assert.Equal(3, metrics.TotalQueries);

        // MRR = (1/1 + 1/2 + 0) / 3 = 1.5 / 3 = 0.5
        Assert.Equal(0.5, metrics.Mrr10, 3);

        // Top-1 Hit = 1 / 3 = 0.333
        Assert.Equal(1.0 / 3.0, metrics.Top1HitRate, 3);
        // Top-3 Hit = 2 / 3 = 0.667
        Assert.Equal(2.0 / 3.0, metrics.Top3HitRate, 3);

        // Latency
        Assert.True(metrics.LatencyP50Ms > 0);
        Assert.True(metrics.LatencyP95Ms >= metrics.LatencyP50Ms);
        Assert.True(metrics.LatencyP99Ms >= metrics.LatencyP95Ms);
        Assert.True(metrics.Qps > 0);
    }

    #endregion

    #region 2. Vector Acceleration & Query Embedding Cache Tests

    [Fact]
    public void QueryVectorCacheProxy_NormalizesAndCachesVectors()
    {
        var proxy = new QueryVectorCacheProxy(maxEntries: 10);

        var q1 = "What is HTTP 451???";
        var q2 = "  what is http 451  ";
        var vec = new float[] { 0.1f, 0.2f, 0.3f, 0.4f };

        // Should normalize to identical key
        var k1 = QueryVectorCacheProxy.NormalizeAndHashQuery(q1, "modelA");
        var k2 = QueryVectorCacheProxy.NormalizeAndHashQuery(q2, "modelA");
        Assert.Equal(k1, k2);

        proxy.SetVector(q1, "modelA", vec);
        Assert.True(proxy.TryGetVector(q2, "modelA", out var fetched));
        Assert.NotNull(fetched);
        Assert.Equal(vec, fetched);

        // Different model spec should not hit
        Assert.False(proxy.TryGetVector(q2, "modelB", out _));
    }

    [Fact]
    public void InMemoryTensorProjectionPool_PerformsSimdScanCorrectly()
    {
        var pool = InMemoryTensorProjectionPool.GetOrCreate("test_kb_simd_" + Guid.NewGuid().ToString("N"));

        int dimensions = 4;
        var items = new List<(string ChunkId, string DocumentId, string SourceKey, string Text, float[] Vector, long DocVersion, string ConnectorId, string? Locator, int StartLine, int EndLine, string? HeadingPath)>
        {
            ("c1", "d1", "file1.md", "Text 1", new float[] { 1.0f, 0.0f, 0.0f, 0.0f }, 1, "conn1", null, 1, 10, "# Section 1"),
            ("c2", "d1", "file2.md", "Text 2", new float[] { 0.0f, 1.0f, 0.0f, 0.0f }, 1, "conn1", null, 11, 20, "# Section 2"),
            ("c3", "d2", "file3.md", "Text 3", new float[] { 0.7071f, 0.7071f, 0.0f, 0.0f }, 1, "conn2", null, 1, 5, "# Section 3")
        };

        pool.SwapSnapshot(vectorGeneration: 1, dimensions, items);
        Assert.True(pool.IsLoaded);
        Assert.Equal(1, pool.CurrentGeneration);
        Assert.Equal(3, pool.LoadedVectorCount);

        // Query vector parallel to c1
        var query = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
        bool success = pool.TryScanSimd(query, targetConnectorId: null, authorizeDocument: null, topK: 2, out var results);

        Assert.True(success);
        Assert.Equal(2, results.Count);
        Assert.Equal("c1", results[0].ChunkId);
        Assert.Equal(1.0, results[0].Score, 3);
        Assert.Equal("c3", results[1].ChunkId);
        Assert.Equal(0.7071, results[1].Score, 3);

        // Invalidation test
        pool.Invalidate();
        Assert.False(pool.IsLoaded);
        Assert.Equal(-1, pool.CurrentGeneration);
    }

    #endregion

    #region 3. SAG-Wiki Style Multi-Column BM25F, NamePhrases & Multi-Hop

    [Fact]
    public void NamePhraseExtractor_ExtractsQuotesEntitiesAndBookTitles()
    {
        var query = "请查询“HTTP 451”与《天龙八部》中 GPT-4 和 v2.3.1 的关联";
        var phrases = NamePhraseExtractor.Extract(query);

        Assert.Contains("HTTP 451", phrases.StrictQuotes);
        Assert.Contains("天龙八部", phrases.BookTitles);
        Assert.Contains("GPT-4", phrases.EntityPhrases);
        Assert.Contains("v2.3.1", phrases.EntityPhrases);

        var enhanced = NamePhraseExtractor.BuildEnhancedFtsQuery(query, "天龙八部 OR 关联");
        Assert.Contains("\"HTTP 451\"", enhanced);
        Assert.Contains("\"天龙八部\"", enhanced);
        Assert.Contains("\"GPT-4\"", enhanced);
    }

    [Fact]
    public void InterleaveAlgorithm_InterleavesMultipleCandidateStreamsFairly()
    {
        var list1 = new List<RecallItem>
        {
            new("c1", "d1", "s1", "t1", 10.0, ScoreKind.BM25, 1, "c1", null),
            new("c2", "d1", "s1", "t2", 8.0, ScoreKind.BM25, 1, "c1", null)
        };
        var list2 = new List<RecallItem>
        {
            new("c3", "d2", "s2", "t3", 9.0, ScoreKind.BM25, 1, "c1", null),
            new("c1", "d1", "s1", "t1", 7.0, ScoreKind.BM25, 1, "c1", null), // duplicate c1
            new("c4", "d2", "s2", "t4", 6.0, ScoreKind.BM25, 1, "c1", null)
        };

        var interleaved = InterleaveAlgorithm.Interleave(new[] { list1, list2 }, limit: 10);
        Assert.Equal(4, interleaved.Count);
        // Turn 0: list1[0] (c1), list2[0] (c3)
        // Turn 1: list1[1] (c2), list2[1] (duplicate c1 skipped -> c4)
        Assert.Equal("c1", interleaved[0].ChunkId);
        Assert.Equal("c3", interleaved[1].ChunkId);
        Assert.Equal("c2", interleaved[2].ChunkId);
        Assert.Equal("c4", interleaved[3].ChunkId);
    }

    [Fact]
    public void ContextIdfTermSelector_SelectsHighValueExpansionTerms()
    {
        var query = "萧峰 丐帮";
        var items = new List<RecallItem>
        {
            new("c1", "d1", "s1", "萧峰是丐帮帮主。阿朱是他一生最心爱的女人。雁门关是他的宿命之地。", 10.0, ScoreKind.BM25, 1, "c1", null)
        };

        var expansions = ContextIdfTermSelector.SelectExpansionTerms(query, items, limit: 3);
        Assert.NotNull(expansions);
        Assert.True(expansions.Count > 0);
        Assert.All(expansions, exp => Assert.False(query.Contains(exp.Term, StringComparison.OrdinalIgnoreCase)));
    }

    #endregion

    #region 4. Cascade Early-Pruning & Adaptive Query-Aware Fusion

    [Fact]
    public void CascadeEarlyPruningStateMachine_ShortCircuitsOnHighMargin()
    {
        var candidatesHighMargin = new List<RecallItem>
        {
            new("c1", "d1", "s1", "绝对匹配文本内容", 100.0, ScoreKind.BM25, 1, "c1", null),
            new("c2", "d1", "s1", "普通相关内容", 20.0, ScoreKind.BM25, 1, "c1", null)
        };

        // Margin = (100 - 20) / 100 = 0.80 >= 0.55
        var (shouldExit, reason) = CascadeEarlyPruningStateMachine.CheckEarlyExit("一般查询", candidatesHighMargin);
        Assert.True(shouldExit);
        Assert.NotNull(reason);
        Assert.Contains("相对优势比", reason);

        // Low margin test: Margin = (100 - 90) / 100 = 0.10 < 0.55
        var candidatesLowMargin = new List<RecallItem>
        {
            new("c1", "d1", "s1", "文本 1", 100.0, ScoreKind.BM25, 1, "c1", null),
            new("c2", "d1", "s1", "文本 2", 90.0, ScoreKind.BM25, 1, "c1", null)
        };

        var (shouldExitLow, _) = CascadeEarlyPruningStateMachine.CheckEarlyExit("一般查询", candidatesLowMargin);
        Assert.False(shouldExitLow);

        // Strict quote match test
        var (shouldExitQuote, quoteReason) = CascadeEarlyPruningStateMachine.CheckEarlyExit(
            "请查询“关键密码”",
            new List<RecallItem>
            {
                new("c1", "d1", "s1", "这里是关键密码的解析", 50.0, ScoreKind.BM25, 1, "c1", null),
                new("c2", "d1", "s1", "这里没有密码", 48.0, ScoreKind.BM25, 1, "c1", null)
            });
        Assert.True(shouldExitQuote);
        Assert.Contains("双引号", quoteReason);
    }

    [Fact]
    public void AdaptiveQueryAwareFusion_DynamicallyWeightsAndPreservesConfidence()
    {
        var bm25Items = new List<RecallItem>
        {
            new("c1", "d1", "s1", "文本 1", 20.0, ScoreKind.BM25, 1, "c1", null),
            new("c2", "d1", "s1", "文本 2", 15.0, ScoreKind.BM25, 1, "c1", null)
        };
        var vectorItems = new List<RecallItem>
        {
            new("c2", "d1", "s1", "文本 2", 0.95, ScoreKind.Cosine, 1, "c1", null),
            new("c3", "d2", "s2", "文本 3", 0.80, ScoreKind.Cosine, 1, "c1", null)
        };

        var fused = AdaptiveQueryAwareFusion.FuseWithZScore(bm25Items, vectorItems, "标准自然问句", topK: 3);

        Assert.Equal(3, fused.Count);
        // c2 appeared in both with high scores, should rank top
        Assert.Equal("c2", fused[0].ChunkId);
        Assert.Equal(ScoreKind.RRF, fused[0].ScoreKind);
    }

    #endregion

    #region 5. End-to-End SqliteKnowledgeBaseStore Integration Test

    [Fact]
    public async Task SqliteKnowledgeBaseStore_BM25F_And_Simd_EndToEndIntegration()
    {
        var dbPath = Path.Combine(_tempDir, "kb_e2e_test.db");
        var kbStore = new SqliteKnowledgeBaseStore("test_kb_01", dbPath);

        // 1. 初始化并保证 5 列 BM25F 倒排表创建
        await kbStore.InitializeAsync();
        await kbStore.EnsureLexicalIndexAsync();

        // 2. 插入两篇测试文档，一篇在标题强匹配，一篇在正文匹配
        var doc1 = new IndexBatch(
            KnowledgeBaseId: "test_kb_01",
            ExpectedIndexEpoch: 0,
            ExpectedDocumentVersion: 0,
            ConnectorId: "conn_test",
            SourceKey: "jinyong_characters.md",
            Locator: "loc1",
            SourceRevision: "rev1",
            ContentHash: "hash1",
            Chunks: new[]
            {
                new ChunkData(
                    ChunkId: "chunk_doc1_1",
                    Ordinal: 1,
                    Text: "# 萧峰传奇\n萧峰，原名乔峰，为金庸武侠小说《天龙八部》中的男主角。",
                    TokenCount: 30
                )
            },
            Vectors: new[] { new float[] { 1.0f, 0.0f, 0.0f, 0.0f } }
        );

        var doc2 = new IndexBatch(
            KnowledgeBaseId: "test_kb_01",
            ExpectedIndexEpoch: 0,
            ExpectedDocumentVersion: 0,
            ConnectorId: "conn_test",
            SourceKey: "general_notes.md",
            Locator: "loc2",
            SourceRevision: "rev2",
            ContentHash: "hash2",
            Chunks: new[]
            {
                new ChunkData(
                    ChunkId: "chunk_doc2_1",
                    Ordinal: 1,
                    Text: "# 杂谈笔记\n昨天在图书馆看书，偶尔读到了萧峰这个名字。",
                    TokenCount: 25
                )
            },
            Vectors: new[] { new float[] { 0.0f, 1.0f, 0.0f, 0.0f } }
        );

        var res1 = await kbStore.CommitDocumentAsync(doc1);
        Assert.True(res1.Success);
        var res2 = await kbStore.CommitDocumentAsync(doc2);
        Assert.True(res2.Success);

        // 3. 词法查询：验证 Title 权重加成（doc1 的 title 包含“萧峰传奇”，doc2 仅正文包含）
        var recallResp = await kbStore.RecallAsync(new RecallRequest(
            Query: "萧峰",
            TopK: 5,
            UseLexicalTokenizer: true
        ));

        Assert.NotNull(recallResp);
        Assert.True(recallResp.Items.Count >= 2);
        // doc1 在标题命中，由于 BM25F 标题 5.0 权重，分数必然大幅领先
        Assert.Equal("chunk_doc1_1", recallResp.Items[0].ChunkId);

        // 4. 双引号短语精确匹配触发级联早停测试
        var earlyExitResp = await kbStore.RecallAsync(new RecallRequest(
            Query: "“萧峰传奇”",
            TopK: 5,
            UseLexicalTokenizer: true
        ));
        Assert.NotNull(earlyExitResp);
        Assert.True(earlyExitResp.Items.Count >= 1);
        Assert.Equal("chunk_doc1_1", earlyExitResp.Items[0].ChunkId);
        if (earlyExitResp.Items.Count > 1)
        {
            Assert.True(earlyExitResp.Items[0].Score > earlyExitResp.Items[1].Score * 2);
        }

        // 5. 向量检索 SIMD 内存池测试
        var queryVector = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
        var vecRecallResp = await kbStore.RecallAsync(new RecallRequest(
            Query: "向量语义匹配",
            TopK: 2,
            QueryVector: queryVector,
            UseLexicalTokenizer: true
        ));
        Assert.NotNull(vecRecallResp);
        Assert.True(vecRecallResp.Items.Count > 0);
        // doc1 向量与 queryVector 完全重合（sim=1.0）
        Assert.Equal("chunk_doc1_1", vecRecallResp.Items[0].ChunkId);
    }

    #endregion
}
