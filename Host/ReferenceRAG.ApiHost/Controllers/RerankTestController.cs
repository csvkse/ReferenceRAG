using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using System.Diagnostics;
using System.Text.Json;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 重排测试 API
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/[controller]")]
public class RerankTestController : ControllerBase
{
    private readonly IRerankService _rerankService;
    private readonly TestRecordStore _recordStore;
    private readonly ILogger<RerankTestController> _logger;

    public RerankTestController(
        IRerankService rerankService,
        TestRecordStore recordStore,
        ILogger<RerankTestController> logger)
    {
        _rerankService = rerankService;
        _recordStore = recordStore;
        _logger = logger;
    }

    /// <summary>
    /// 执行重排测试
    /// </summary>
    [HttpPost("test")]
    public async Task<ActionResult<RerankTestResult>> Test([FromBody] RerankTestRequest request)
    {
        var result = new RerankTestResult
        {
            TestType = "rerank",
            ModelName = request.ModelName ?? "default",
            Timestamp = DateTime.UtcNow
        };

        try
        {
            var sw = Stopwatch.StartNew();

            // 调用重排服务 - 使用 RerankBatchAsync
            var documentTexts = request.Documents.Select(d => d.Text).ToList();
            var rerankResult = await _rerankService.RerankBatchAsync(request.Query, documentTexts);
            result.QueryMs = sw.ElapsedMilliseconds;

            // 构建结果
            var documentResults = new List<RerankDocumentResult>();
            for (int i = 0; i < rerankResult.Documents.Count; i++)
            {
                var rerankDoc = rerankResult.Documents[i];
                var originalDoc = request.Documents.FirstOrDefault(d => d.Text == rerankDoc.Document);
                var originalIndex = request.Documents.IndexOf(originalDoc ?? new RerankDocumentInput());

                documentResults.Add(new RerankDocumentResult
                {
                    Id = originalDoc?.Id ?? Guid.NewGuid().ToString(),
                    Tag = originalDoc?.Tag,
                    Text = rerankDoc.Document.Length > 200 ? rerankDoc.Document[..200] + "..." : rerankDoc.Document,
                    RelevanceScore = rerankDoc.RelevanceScore,
                    OriginalIndex = rerankDoc.Index >= 0 ? rerankDoc.Index : originalIndex,
                    NewRank = i + 1,
                    ExpectedRelevance = originalDoc?.ExpectedRelevance
                });
            }

            result.Documents = documentResults;

            // 计算评估指标（如果有期望相关性分数）
            var hasExpectedScores = request.Documents.Any(d => d.ExpectedRelevance.HasValue);
            if (hasExpectedScores)
            {
                var expectedRelevance = request.Documents
                    .Select(d => d.ExpectedRelevance ?? 0)
                    .ToList();
                var predictedRelevance = documentResults
                    .OrderBy(d => d.OriginalIndex)
                    .Select(d => d.RelevanceScore)
                    .ToList();

                result.Ndcg = CalculateNdcg(expectedRelevance, documentResults);
                result.Mrr = CalculateMrr(expectedRelevance, documentResults);
                result.Map = CalculateMap(expectedRelevance, documentResults);
                result.RankingAccuracy = CalculateRankingAccuracy(expectedRelevance, documentResults);
                result.MeanAbsoluteError = CalculateMae(expectedRelevance, documentResults);
            }

            // 保存记录
            var record = new TestRecord
            {
                Id = Guid.NewGuid().ToString(),
                TestType = "rerank",
                ModelName = result.ModelName,
                Timestamp = result.Timestamp,
                Query = request.Query,
                Results = new Dictionary<string, object>
                {
                    ["documentCount"] = request.Documents.Count,
                    ["queryMs"] = result.QueryMs,
                    ["ndcg"] = result.Ndcg,
                    ["mrr"] = result.Mrr,
                    ["map"] = result.Map,
                    ["rankingAccuracy"] = result.RankingAccuracy,
                    ["meanAbsoluteError"] = result.MeanAbsoluteError
                }
            };
            await _recordStore.SaveAsync(record);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rerank test failed");
            return StatusCode(500, new { error = "重排测试执行失败，请查看服务日志" });
        }
    }

    /// <summary>
    /// 运行预设测试套件
    /// </summary>
    [HttpPost("preset/{suiteName}")]
    public async Task<ActionResult<RerankTestResult>> RunPresetTest(string suiteName)
    {
        var suite = GetPresetSuite(suiteName);
        if (suite == null)
        {
            return NotFound(new { error = $"测试套件 '{suiteName}' 不存在" });
        }

        var result = await Test(suite);
        return result.Result!;
    }

    /// <summary>
    /// 获取可用测试套件
    /// </summary>
    [HttpGet("presets")]
    public ActionResult<List<RerankPresetSuiteInfo>> GetPresetSuites()
    {
        return Ok(new List<RerankPresetSuiteInfo>
        {
            new() { Name = "chinese-rerank", Description = "中文编程与通识问答", DocumentCount = 10 },
            new() { Name = "english-rerank", Description = "英文开发最佳实践检索", DocumentCount = 10 },
            new() { Name = "mixed-rerank", Description = "向量数据库与技术选型中英混合", DocumentCount = 10 },
            new() { Name = "code-rerank", Description = "快速排序与代码实现精排", DocumentCount = 10 },
            new() { Name = "hard-negatives-rerank", Description = "对抗性硬负例与条件互斥", DocumentCount = 10 },
            new() { Name = "long-context-rerank", Description = "真实长文本Chunk与事实定位", DocumentCount = 10 },
            new() { Name = "tech-troubleshooting-rerank", Description = "技术栈报错排查与版本演进", DocumentCount = 10 },
            new() { Name = "cross-lingual-rerank", Description = "跨语言与跨境支付回调验签", DocumentCount = 10 },
            new() { Name = "enterprise-faq-rerank", Description = "电商售后生鲜理赔业务 FAQ", DocumentCount = 10 },
            new() { Name = "antonym-polarity-rerank", Description = "反义极性对立与真值冲突精排", DocumentCount = 10 },
            new() { Name = "version-temporal-rerank", Description = "版本演进与时效废弃规范精排", DocumentCount = 10 },
            new() { Name = "numerical-entity-rerank", Description = "精确数值参数与实体区间约束精排", DocumentCount = 10 }
        });
    }

    /// <summary>
    /// 获取测试记录
    /// </summary>
    [HttpGet("records")]
    public async Task<ActionResult<List<TestRecord>>> GetRecords(
        [FromQuery] string? modelName = null,
        [FromQuery] string? testType = null,
        [FromQuery] int limit = 100)
    {
        // 只返回 rerank 类型的记录
        var records = await _recordStore.GetAsync(modelName, "rerank", limit);
        return Ok(records);
    }

    /// <summary>
    /// 清除测试记录
    /// </summary>
    [HttpDelete("records")]
    public async Task<ActionResult> ClearRecords([FromQuery] string? modelName = null)
    {
        await _recordStore.ClearAsync(modelName, "rerank");
        return NoContent();
    }

    /// <summary>
    /// 获取测试统计
    /// </summary>
    [HttpGet("statistics")]
    public async Task<ActionResult<RerankTestStatistics>> GetStatistics([FromQuery] string? modelName = null)
    {
        var records = await _recordStore.GetAsync(null, "rerank", 1000);
        var filtered = modelName != null ? records.Where(r => r.ModelName == modelName).ToList() : records;

        // Helper function to safely extract double from object (handles JsonElement)
        static double ExtractDouble(object? value)
        {
            if (value == null) return 0;
            if (value is JsonElement jsonElement)
            {
                return jsonElement.ValueKind switch
                {
                    JsonValueKind.Number => jsonElement.GetDouble(),
                    JsonValueKind.String => double.TryParse(jsonElement.GetString(), out var d) ? d : 0,
                    _ => 0
                };
            }
            return Convert.ToDouble(value);
        }

        var stats = new RerankTestStatistics
        {
            TotalTests = filtered.Count,
            ByModel = filtered
                .GroupBy(r => r.ModelName)
                .Select(g => new RerankModelStatistics
                {
                    ModelName = g.Key,
                    TotalTests = g.Count(),
                    AvgNdcg = g.Where(r => r.Results.ContainsKey("ndcg"))
                        .Select(r => ExtractDouble(r.Results["ndcg"]))
                        .DefaultIfEmpty(0)
                        .Average(),
                    AvgMrr = g.Where(r => r.Results.ContainsKey("mrr"))
                        .Select(r => ExtractDouble(r.Results["mrr"]))
                        .DefaultIfEmpty(0)
                        .Average(),
                    AvgMap = g.Where(r => r.Results.ContainsKey("map"))
                        .Select(r => ExtractDouble(r.Results["map"]))
                        .DefaultIfEmpty(0)
                        .Average(),
                    AvgRankingAccuracy = g.Where(r => r.Results.ContainsKey("rankingAccuracy"))
                        .Select(r => ExtractDouble(r.Results["rankingAccuracy"]))
                        .DefaultIfEmpty(0)
                        .Average(),
                    AvgMae = g.Where(r => r.Results.ContainsKey("meanAbsoluteError"))
                        .Select(r => ExtractDouble(r.Results["meanAbsoluteError"]))
                        .DefaultIfEmpty(0)
                        .Average(),
                    AvgQueryMs = g.Where(r => r.Results.ContainsKey("queryMs"))
                        .Select(r => ExtractDouble(r.Results["queryMs"]))
                        .DefaultIfEmpty(0)
                        .Average()
                })
                .ToList()
        };

        return Ok(stats);
    }

    /// <summary>
    /// 性能基准测试
    /// </summary>
    [HttpPost("benchmark")]
    public async Task<ActionResult<RerankBenchmarkResult>> RunBenchmark([FromBody] RerankBenchmarkRequest request)
    {
        var result = new RerankBenchmarkResult
        {
            Concurrency = request.Concurrency,
            TotalRequests = request.TotalRequests,
            DocumentCount = request.DocumentCount,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            // 生成测试数据
            var testQueries = GenerateBenchmarkQueries(request.TotalRequests, request.DocumentCount);

            var latencies = new List<long>();
            var successCount = 0;
            var failCount = 0;
            var errors = new List<string>();

            var totalSw = Stopwatch.StartNew();

            // 使用信号量控制并发
            var semaphore = new SemaphoreSlim(request.Concurrency);
            var tasks = new List<Task>();

            foreach (var testQuery in testQueries)
            {
                var task = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        var rerankResult = await _rerankService.RerankBatchAsync(testQuery.Query, testQuery.Documents);
                        sw.Stop();

                        lock (latencies)
                        {
                            latencies.Add(sw.ElapsedMilliseconds);
                            Interlocked.Increment(ref successCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failCount);
                        lock (errors)
                        {
                            if (errors.Count < 5) // 只保留前5个错误
                            {
                                errors.Add(ex.Message);
                            }
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                tasks.Add(task);
            }

            await Task.WhenAll(tasks);
            totalSw.Stop();

            // 计算统计数据
            latencies.Sort();
            result.SuccessCount = successCount;
            result.FailCount = failCount;
            result.Errors = errors;
            result.TotalMs = totalSw.ElapsedMilliseconds;

            if (latencies.Count > 0)
            {
                result.Qps = (double)successCount / (totalSw.ElapsedMilliseconds / 1000.0);
                result.AvgLatencyMs = latencies.Average();
                result.MinLatencyMs = latencies.First();
                result.MaxLatencyMs = latencies.Last();
                result.P50LatencyMs = GetPercentile(latencies, 50);
                result.P95LatencyMs = GetPercentile(latencies, 95);
                result.P99LatencyMs = GetPercentile(latencies, 99);
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rerank benchmark test failed");
            return StatusCode(500, new { error = "性能基准测试执行失败", details = ex.Message });
        }
    }

    private long GetPercentile(List<long> sortedLatencies, int percentile)
    {
        if (sortedLatencies.Count == 0) return 0;
        var index = (int)Math.Ceiling(sortedLatencies.Count * percentile / 100.0) - 1;
        return sortedLatencies[Math.Max(0, Math.Min(index, sortedLatencies.Count - 1))];
    }

    private List<BenchmarkTestQuery> GenerateBenchmarkQueries(int count, int docCount)
    {
        var queries = new List<BenchmarkTestQuery>();
        var random = new Random();

        var sampleQueries = new[]
        {
            "如何学习编程",
            "机器学习算法原理",
            "数据库优化方法",
            "前端开发技术",
            "后端架构设计",
            "Python 编程入门",
            "深度学习模型训练",
            "API 接口设计",
            "代码重构技巧",
            "软件测试方法"
        };

        var sampleDocs = new[]
        {
            "编程是一种创造性的活动，需要不断学习和实践来提高技能水平",
            "机器学习是人工智能的重要分支，通过算法让计算机从数据中学习",
            "数据库优化涉及索引设计、查询优化和存储配置等多个方面",
            "前端开发包括HTML、CSS、JavaScript等技术，用于构建用户界面",
            "后端架构需要考虑可扩展性、可靠性和性能等因素",
            "Python是一种简洁易学的编程语言，适合初学者入门",
            "深度学习使用神经网络处理复杂模式识别任务",
            "API设计应遵循RESTful原则，保证接口的一致性和可用性",
            "代码重构是改善代码结构而不改变其功能的技术",
            "软件测试包括单元测试、集成测试和端到端测试等多个层次"
        };

        for (int i = 0; i < count; i++)
        {
            var query = sampleQueries[random.Next(sampleQueries.Length)];
            var docs = new List<string>();

            for (int j = 0; j < docCount; j++)
            {
                docs.Add(sampleDocs[random.Next(sampleDocs.Length)]);
            }

            queries.Add(new BenchmarkTestQuery { Query = query, Documents = docs });
        }

        return queries;
    }

    // ==================== 辅助方法 ====================

    private double CalculateNdcg(List<double> expectedRelevance, List<RerankDocumentResult> results)
    {
        if (expectedRelevance.Count == 0 || results.Count == 0) return 0;

        // 计算 DCG
        double dcg = 0;
        for (int i = 0; i < results.Count; i++)
        {
            var doc = results[i];
            var expected = doc.OriginalIndex >= 0 && doc.OriginalIndex < expectedRelevance.Count
                ? expectedRelevance[doc.OriginalIndex]
                : 0;
            dcg += expected / Math.Log2(i + 2);
        }

        // 计算 IDCG（理想排序）
        var idealRelevance = expectedRelevance.OrderByDescending(r => r).ToList();
        double idcg = 0;
        for (int i = 0; i < idealRelevance.Count; i++)
        {
            idcg += idealRelevance[i] / Math.Log2(i + 2);
        }

        return idcg > 0 ? dcg / idcg : 0;
    }

    private double CalculateMrr(List<double> expectedRelevance, List<RerankDocumentResult> results)
    {
        if (expectedRelevance.Count == 0 || results.Count == 0) return 0;

        // 找到第一个期望相关性最高的文档的新排名
        var maxExpected = expectedRelevance.Max();
        if (maxExpected <= 0) return 0;

        var topDocOriginalIndex = expectedRelevance.IndexOf(maxExpected);
        var topDocRank = results
            .Select((r, i) => new { Result = r, Rank = i + 1 })
            .FirstOrDefault(x => x.Result.OriginalIndex == topDocOriginalIndex);

        return topDocRank != null ? 1.0 / topDocRank.Rank : 0;
    }

    private double CalculateMap(List<double> expectedRelevance, List<RerankDocumentResult> results)
    {
        if (expectedRelevance.Count == 0 || results.Count == 0) return 0;

        // 定义相关性阈值
        const double relevanceThreshold = 0.5;
        var relevantCount = expectedRelevance.Count(r => r >= relevanceThreshold);
        if (relevantCount == 0) return 0;

        double sumPrecision = 0;
        int foundRelevant = 0;

        for (int i = 0; i < results.Count; i++)
        {
            var doc = results[i];
            var expected = doc.OriginalIndex >= 0 && doc.OriginalIndex < expectedRelevance.Count
                ? expectedRelevance[doc.OriginalIndex]
                : 0;

            if (expected >= relevanceThreshold)
            {
                foundRelevant++;
                sumPrecision += (double)foundRelevant / (i + 1);
            }
        }

        return sumPrecision / relevantCount;
    }

    private double CalculateRankingAccuracy(List<double> expectedRelevance, List<RerankDocumentResult> results)
    {
        if (expectedRelevance.Count == 0 || results.Count == 0) return 0;

        // 计算期望排序
        var expectedOrder = expectedRelevance
            .Select((r, i) => new { Index = i, Relevance = r })
            .OrderByDescending(x => x.Relevance)
            .Select(x => x.Index)
            .ToList();

        // 计算实际排序
        var actualOrder = results
            .OrderByDescending(r => r.RelevanceScore)
            .Select(r => r.OriginalIndex)
            .ToList();

        // 计算排序相关性（使用 Spearman 相关系数简化版）
        var commonCount = Math.Min(expectedOrder.Count, actualOrder.Count);
        if (commonCount == 0) return 0;

        var correctRankings = 0;
        for (int i = 0; i < commonCount; i++)
        {
            if (expectedOrder.Contains(actualOrder[i]))
            {
                var expectedRank = expectedOrder.IndexOf(actualOrder[i]);
                if (Math.Abs(expectedRank - i) <= 1) // 允许一位偏差
                {
                    correctRankings++;
                }
            }
        }

        return (double)correctRankings / commonCount;
    }

    private double CalculateMae(List<double> expectedRelevance, List<RerankDocumentResult> results)
    {
        if (expectedRelevance.Count == 0 || results.Count == 0) return 0;

        var errors = new List<double>();
        foreach (var doc in results)
        {
            if (doc.OriginalIndex >= 0 && doc.OriginalIndex < expectedRelevance.Count)
            {
                var expected = expectedRelevance[doc.OriginalIndex];
                errors.Add(Math.Abs(doc.RelevanceScore - expected));
            }
        }

        return errors.Count > 0 ? errors.Average() : 0;
    }

    private RerankTestRequest? GetPresetSuite(string name)
    {
        return name switch
        {
            "chinese-rerank" => new RerankTestRequest
            {
                Query = "如何学习编程",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Text = "编程学习需要从基础语法开始，逐步掌握数据结构和算法，建立扎实的计算机科学思维体系。", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Text = "学习编程的高效路径包括系统研读经典教程、高频动手写代码、调试典型错误以及参与开源协作。", ExpectedRelevance = 0.90 },
                    new() { Id = "doc3", Text = "初学者建议选择一门通用型语言（如 Python 或 JavaScript）作为切入点，通过编写实用小工具持续获得正反馈。", ExpectedRelevance = 0.85 },
                    new() { Id = "doc4", Text = "深入理解计算机网络（TCP/IP）、操作系统（线程与内存）以及数据库原理，是技术能力进阶的核心根基。", ExpectedRelevance = 0.75 },
                    new() { Id = "doc5", Text = "优秀的编码工程规范包括编写单元测试、规范命名与结构化注释、代码重构以及版本控制管理。", ExpectedRelevance = 0.65 },
                    new() { Id = "doc6", Text = "熟练使用 Git 分支管理、VSCode/IDE 调试技巧以及自动化 CI/CD 构建工具，能显著提升开发交付效率。", ExpectedRelevance = 0.50 },
                    new() { Id = "doc7", Text = "机器学习与深度学习是计算机领域的重要分支，通常需要较强的微积分、线性代数和概率统计数学基础。", ExpectedRelevance = 0.35 },
                    new() { Id = "doc8", Text = "编写清晰的个人履历并在技术社区输出博客，有助于在校园招聘和社招面试中充分展现自身技术实力。", ExpectedRelevance = 0.20 },
                    new() { Id = "doc9", Text = "保持每日规律的户外散步与轻度有氧运动，可以有效缓解久坐编程带来的颈椎酸痛与视疲劳。", ExpectedRelevance = 0.10 },
                    new() { Id = "doc10", Text = "居家常备急救箱应储备体温计、医用碘伏、创口贴以及常见解热镇痛非处方药物以备不时之需。", ExpectedRelevance = 0.05 }
                }
            },
            "english-rerank" => new RerankTestRequest
            {
                Query = "How to improve programming skills",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Text = "Practice coding every single day and build production-ready applications from scratch to master software engineering principles.", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Text = "Read clean code written by experienced developers, understand software design patterns, and study prominent open-source repositories.", ExpectedRelevance = 0.90 },
                    new() { Id = "doc3", Text = "Master debugging techniques, profile memory and CPU bottlenecks, and write comprehensive automated test suites for critical modules.", ExpectedRelevance = 0.85 },
                    new() { Id = "doc4", Text = "Deep dive into computer science fundamentals including algorithms, data structures, concurrency, and distributed systems architecture.", ExpectedRelevance = 0.75 },
                    new() { Id = "doc5", Text = "Participate constructively in peer code reviews, welcome critical feedback, and continuously refactor technical debt.", ExpectedRelevance = 0.65 },
                    new() { Id = "doc6", Text = "Modern DevOps workflows, automated deployment pipelines, and infrastructure as code greatly enhance developer productivity.", ExpectedRelevance = 0.50 },
                    new() { Id = "doc7", Text = "Machine learning and artificial intelligence require extensive expertise in linear algebra, multivariable calculus, and tensor operations.", ExpectedRelevance = 0.35 },
                    new() { Id = "doc8", Text = "Soft skills such as clear technical writing, stakeholder communication, and project planning are indispensable for engineering leadership.", ExpectedRelevance = 0.20 },
                    new() { Id = "doc9", Text = "Maintaining ergonomic desk posture and taking regular eye breaks helps prevent carpal tunnel syndrome and physical burnout.", ExpectedRelevance = 0.10 },
                    new() { Id = "doc10", Text = "The historical museum downtown displays preserved artifacts from classical antiquity and Renaissance landscape oil paintings.", ExpectedRelevance = 0.05 }
                }
            },
            "mixed-rerank" => new RerankTestRequest
            {
                Query = "什么是向量数据库及其应用场景",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Text = "向量数据库是专门用于高效存储、索引与快速检索高维稠密向量的专用系统，主要面向大规模近似最近邻（ANN）相似度计算。", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Text = "Vector databases such as Milvus, Qdrant, and Pinecone are purpose-built to accelerate similarity search for embedding vectors in modern RAG workflows.", ExpectedRelevance = 0.90 },
                    new() { Id = "doc3", Text = "向量数据库最典型的应用场景包括大语言模型外挂知识库问答（RAG）、多模态图文跨模态检索、个性化内容推荐引擎及人脸比对识别。", ExpectedRelevance = 0.85 },
                    new() { Id = "doc4", Text = "HNSW (分层可导航小世界图) 和 IVF-PQ (倒排文件乘积量化) 是现代向量数据库实现千万级向量毫秒级召回的主流核心算法。", ExpectedRelevance = 0.75 },
                    new() { Id = "doc5", Text = "Traditional relational databases can support vector indexing through extensions like pgvector in PostgreSQL, balancing transactional and semantic needs.", ExpectedRelevance = 0.65 },
                    new() { Id = "doc6", Text = "混合检索（Hybrid Search）通过同时运行向量稠密检索与 BM25 稀疏关键词检索，利用倒数排名融合（RRF）互补彼此的检索盲区。", ExpectedRelevance = 0.50 },
                    new() { Id = "doc7", Text = "在线性代数与几何空间中，向量（Vector）指代兼具大小（模长）和方向的数学量，在计算机科学中通常表示为浮点数一维数组。", ExpectedRelevance = 0.35 },
                    new() { Id = "doc8", Text = "关系型数据库（如 MySQL 和 PostgreSQL）依靠严格的 B+ 树索引结构保证 ACID 事务完整性与范式化表关联查询。", ExpectedRelevance = 0.20 },
                    new() { Id = "doc9", Text = "超大规模深度学习模型的分布式并行训练依赖多卡 GPU 互联技术（如 NVLink 和 InfiniBand 网络）提供超高通信吞吐带宽。", ExpectedRelevance = 0.10 },
                    new() { Id = "doc10", Text = "今天下午天气晴好微风吹拂，许多家庭带着帐篷和便当在中央公园湖畔的草坪上享受愉快的春日野餐时光。", ExpectedRelevance = 0.05 }
                }
            },
            "code-rerank" => new RerankTestRequest
            {
                Query = "如何实现快速排序算法",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Text = "快速排序（Quicksort）基于分治法策略：选取基准元素（Pivot），通过分区将待排数组划分为小于和大于基准的两侧，递归对子数组排序，平均时间复杂度为 O(n log n)。", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Text = "def quicksort(arr):\n    if len(arr) <= 1:\n        return arr\n    pivot = arr[len(arr) // 2]\n    left = [x for x in arr if x < pivot]\n    mid = [x for x in arr if x == pivot]\n    right = [x for x in arr if x > pivot]\n    return quicksort(left) + mid + quicksort(right)", ExpectedRelevance = 0.90 },
                    new() { Id = "doc3", Text = "快速排序的原地分区（In-Place Partition）算法采用双指针首尾相向移动，通过就地交换元素消除额外数组分配，将辅助空间复杂度降低至 O(log n)。", ExpectedRelevance = 0.85 },
                    new() { Id = "doc4", Text = "工程级快速排序常引入‘三数取中法’选择基准以防退化为最坏情况 O(n²)，并在小规模切片（元素小于16）时自动切换为插入排序以减少递归调用开销。", ExpectedRelevance = 0.75 },
                    new() { Id = "doc5", Text = "归并排序（Merge Sort）同样基于分治策略且拥有严格的 O(n log n) 最好与最坏时间复杂度以及排序稳定性，但需要 O(n) 的辅助存储空间。", ExpectedRelevance = 0.60 },
                    new() { Id = "doc6", Text = "经典比较排序算法横向对比：冒泡与选择排序属于 O(n²) 基础算法；堆排序与快速排序属于 O(n log n) 原地排序；基数排序属于非比较线性排序。", ExpectedRelevance = 0.45 },
                    new() { Id = "doc7", Text = "二分查找算法（Binary Search）依赖底层连续数组已预先按升序或降序排列，通过每次折半区间在 O(log n) 时间内精确定位目标数值索引。", ExpectedRelevance = 0.30 },
                    new() { Id = "doc8", Text = "数据库聚簇索引通常采用多路平衡搜索树（B+ Tree）作为底层数据结构，叶子节点构成双向链表以高效支持大跨度范围区间扫描。", ExpectedRelevance = 0.20 },
                    new() { Id = "doc9", Text = "关系型数据库的基于代价的优化器（CBO）通过分析表统计信息、索引基数及过滤因子，自主评估并挑选最优的嵌套循环或哈希连接算子。", ExpectedRelevance = 0.10 },
                    new() { Id = "doc10", Text = "计算机主板上的 PCIe 5.0 扩展插槽单通道带宽达到 32 GT/s，能够为高端 NVMe 固态硬盘与深度学习加速卡提供海量数据吞吐能力。", ExpectedRelevance = 0.05 }
                }
            },
            "hard-negatives-rerank" => new RerankTestRequest
            {
                Query = "如何在 Linux 中禁用 SSH 密码登录并仅允许密钥认证",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Tag = "标准操作", Text = "在 Linux 中安全禁用 SSH 密码登录的标准步骤：编辑 /etc/ssh/sshd_config 文件，明确设置 PasswordAuthentication no 与 ChallengeResponseAuthentication no，确认 PubkeyAuthentication yes 已生效后，执行 systemctl restart sshd 重启服务。", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Tag = "实战指南", Text = "SSH 安全加固实战操作指南：务必先使用 ssh-copy-id 将本地公钥成功部署至远程主机的 ~/.ssh/authorized_keys 并测试密钥登录成功，再修改 sshd_config 关闭 PasswordAuthentication 密码认证，防止意外锁死在服务器外。", ExpectedRelevance = 0.90 },
                    new() { Id = "doc3", Tag = "安全规范", Text = "为防范字典密码暴力穷举破解，企业运维规范强制要求弃用密码验证，全面推行基于 ED25519 或 RSA 4096 位强非对称密钥对的 SSH 准入机制。", ExpectedRelevance = 0.80 },
                    new() { Id = "doc4", Tag = "密钥生成", Text = "生成免密认证密钥对的常用命令为 ssh-keygen -t ed25519 -C 'user@server'，妥善保护本地私钥文件权限为 600，将生成的 .pub 公钥内容附加到服务端对应用户的授权密钥文件中。", ExpectedRelevance = 0.65 },
                    new() { Id = "doc5", Tag = "权限审计", Text = "Linux 用户权限与审计加固：通过配置 /etc/sudoers 文件限定特定管理员用户组，并可通过 PAM 模块对连续输错密码的账户实施锁定。", ExpectedRelevance = 0.40 },
                    new() { Id = "doc6", Tag = "硬负例-目标相反", Text = "当因密钥丢失无法远程管理时，如何在 Linux 中重新恢复开启 SSH 密码登录：进入云服务器 VNC 控制台编辑 /etc/ssh/sshd_config 将 PasswordAuthentication 重新改为 yes 并重启服务。", ExpectedRelevance = 0.18 },
                    new() { Id = "doc7", Tag = "硬负例-平台不符", Text = "在 Windows Server 2022 上通过 PowerShell 安装 OpenSSH 服务，配置防火墙端口 22，并配置本地用户通过域账号密码直接登录。", ExpectedRelevance = 0.15 },
                    new() { Id = "doc8", Tag = "硬负例-协议无关", Text = "在 CentOS 上搭建 vsftpd 文件传输服务，通过修改 /etc/vsftpd/vsftpd.conf 配置文件启用系统本地用户账号密码验证并关闭匿名访问。", ExpectedRelevance = 0.12 },
                    new() { Id = "doc9", Tag = "网络排障", Text = "Linux 网络基础排障工具：使用 netstat -tlpn 或 ss -tulpn 命令可快速查看本地操作系统当前正在监听 22 端口的 SSH 守护进程 PID。", ExpectedRelevance = 0.08 },
                    new() { Id = "doc10", Tag = "生活常识", Text = "冬季室内暖气运行期间环境相对湿度普遍偏低，建议开启超声波或冷蒸发型加湿器将湿度维持在 45%~60% 舒适区间。", ExpectedRelevance = 0.03 }
                }
            },
            "long-context-rerank" => new RerankTestRequest
            {
                Query = "员工因公出差因不可抗力航班延误导致的住宿费用报销标准与申请流程",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Tag = "特批条款", Text = "《企业行政与差旅管理规定实施细则》第4.3条【交通异常与在途紧急留宿】：员工因公出差期间因暴雨、台风、航行管制等不可抗力导致航班取消或延误超过 6 小时且被迫在途住宿的，凭航司官方盖章的延误证明文件，当晚住宿费据实报销，不受目的地差旅等级上限（一线城市500元/天）限制，随审批单提交特批报销流程。", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Tag = "特批手册", Text = "《差旅财务核销手册2026版》附录三【突发情况特批报销】：因极端恶劣天气或公共突发事件造成交通中断滞留中转地的，经部门分管总监线上特批后可报销超出常规限额的过夜酒店发票。报销单附件必须同时上传：1.航司或铁路官方延误声明；2.酒店增值税专用发票；3.支付凭据及原定行程单据。", ExpectedRelevance = 0.90 },
                    new() { Id = "doc3", Tag = "应急津贴", Text = "应急出差滞留津贴核发规程：出差人员在途中非因个人主观过错滞留超过 24 小时且航司未统一安排食宿的，可就近入住所处机场或车站合作酒店，产生的临时住宿及就餐费用可按照每人每天最高 600 元标准申领不可抗力中转津贴。", ExpectedRelevance = 0.80 },
                    new() { Id = "doc4", Tag = "集中管理", Text = "差旅预订平台集中管理细则：全体员工出差必须优先通过企业内网商旅平台统一预订机票与酒店；遇突发航班变更由商旅管家线上改签。如擅自使用第三方个人软件另行订购且未提前报备的，差额费用由个人自行承担。", ExpectedRelevance = 0.65 },
                    new() { Id = "doc5", Tag = "常规标准", Text = "常规国内差旅住宿费限额标准：出差人员住宿费实行分级限额实报实销：一类城市（北京、上海、广州、深圳）每人每天不超过 500 元；二类城市（各省会及新一线城市）每人每天不超过 380 元；其余三类城市每人每天不超过 280 元。", ExpectedRelevance = 0.45 },
                    new() { Id = "doc6", Tag = "交通标准", Text = "国内城际公共交通乘坐标准：员工因公出差乘坐高铁以二等座为原则，单程时间超过 8 小时且跨夜的车次可申请动卧或软卧；除高级管理人员外原则上严禁选购商务座。", ExpectedRelevance = 0.30 },
                    new() { Id = "doc7", Tag = "行政耗材", Text = "办公用品申领与行政耗材采购规范：各业务部门每月办公文具申领需在每月 20 日前通过 OA 行政服务台统一提报汇总，由行政部按需批量采购并派发至各楼层工位。", ExpectedRelevance = 0.20 },
                    new() { Id = "doc8", Tag = "年休福利", Text = "职工探亲假及法定年休假待遇细则：工作年限满 1 年不满 10 年的员工享受带薪年休假 5 天；符合探望配偶或父母探亲政策的，往返硬卧车票可按财务探亲福利报销。", ExpectedRelevance = 0.12 },
                    new() { Id = "doc9", Tag = "机房运维", Text = "数据中心机房机架电力与环境监控准则：机房冷通道环境温度应稳定维持在 20℃~24℃ 之间，精密空调采用冗余 N+1 双路供电设计以确保核心网络设备安全无故障运转。", ExpectedRelevance = 0.05 },
                    new() { Id = "doc10", Tag = "茶艺常识", Text = "中国传统茶文化泡茶技艺解析：绿茶适宜选用 85℃ 左右温开水以玻璃杯冲泡观赏叶形舒展；黑茶与武夷岩茶则推荐使用 100℃ 沸水与紫砂壶闷泡以充分激发深层陈香。", ExpectedRelevance = 0.02 }
                }
            },
            "tech-troubleshooting-rerank" => new RerankTestRequest
            {
                Query = ".NET 10 中 HttpClient 报 OperationCanceledException 异常排查与超时配置",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Tag = "底层机制", Text = "在 .NET 10 中，HttpClient 网络超时底层由 CancellationToken 机制驱动。当请求耗时超过 HttpClient.Timeout（默认100秒）或传入的 CancellationTokenSource 超时触发时，均会抛出 OperationCanceledException（内含 TaskCanceledException）。排查时应通过 ex.CancellationToken.IsCancellationRequested 检查是由于外部取消还是超时所致。", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Tag = "Handler调优", Text = "现代 .NET 推荐基于 SocketsHttpHandler 调优网络超时：通过细粒度显式配置 ConnectTimeout（TCP三次握手连接超时）、PooledConnectionLifetime（连接存活周期防止DNS变更失效）以及 ResponseDrainTimeout，可针对复杂网络拓扑实现分阶段精细超时控制。", ExpectedRelevance = 0.90 },
                    new() { Id = "doc3", Tag = "异常判定", Text = "精准区分 .NET 中的超时与网络拒绝：当服务端端口不可达或拒绝连接时，底层抛出 HttpRequestException（包含 SocketException/ECONNREFUSED）；而服务端响应悬挂慢死或网关拥塞则触发 OperationCanceledException。可通过 catch (OperationCanceledException ex) when (!cts.IsCancellationRequested) 专门捕获网络层硬超时。", ExpectedRelevance = 0.85 },
                    new() { Id = "doc4", Tag = "连接池工厂", Text = "IHttpClientFactory 架构最佳实践：使用依赖注入的 IHttpClientFactory 管理长连接池生命周期，避免高并发下反复实例化 HttpClient 导致本地套接字端口耗尽（TIME_WAIT），并支持挂载 DelegatingHandler 拦截器与 Polly 超时重试策略。", ExpectedRelevance = 0.75 },
                    new() { Id = "doc5", Tag = "级联取消", Text = "级联取消令牌设计模式：通过 CancellationTokenSource.CreateLinkedTokenSource(userToken) 可以把系统后台看门狗超时（例如15秒强制硬熔断）与前端用户手动点击‘中止’的取消令牌无缝结合，任意一个先到达即可立即取消 HTTP 请求。", ExpectedRelevance = 0.60 },
                    new() { Id = "doc6", Tag = "版本废弃-旧版方案", Text = "在历史遗留的 .NET Framework 4.5 环境中，系统级 ServicePointManager.DefaultConnectionLimit 默认限制单域名并发连接数为 2，现代 .NET Core/.NET 10 已废弃该限制并由 SocketsHttpHandler 接管。", ExpectedRelevance = 0.35 },
                    new() { Id = "doc7", Tag = "Kestrel防护", Text = "ASP.NET Core Kestrel Web 服务器请求超时防护：在应用端可以通过 Program.cs 配置 KestrelServerOptions.Limits.RequestHeadersTimeout 防止慢速客户端缓慢发送 HTTP 报头占死线程池连接。", ExpectedRelevance = 0.20 },
                    new() { Id = "doc8", Tag = "内存诊断", Text = "C# 托管内存泄露深度诊断流程：借助 dotnet-dump 与 dotnet-gcdump 工具抓取进程快照，重点排查未解除注销的静态事件侦听器、长期积压的 ConcurrentQueue 缓冲以及未调用 Dispose 的非托管资源句柄。", ExpectedRelevance = 0.12 },
                    new() { Id = "doc9", Tag = "SQL超时", Text = "SQL Server 数据库执行超时调优：SqlCommand.CommandTimeout 默认时间为 30 秒，当复杂报表聚合查询由于缺少索引发生全表扫描导致超时挂起时，底层抛出特定错误码为 -2 的 SqlException。", ExpectedRelevance = 0.08 },
                    new() { Id = "doc10", Tag = "动力电池", Text = "纯电动汽车动力电池性能特性：磷酸铁锂电芯热失控起始温度高且循环衰减寿命长；而三元高镍锂电芯在零下 20℃ 低温环境下的放电效率和体积质量能量密度方面具备显著优势。", ExpectedRelevance = 0.02 }
                }
            },
            "cross-lingual-rerank" or "multilingual-query" => new RerankTestRequest
            {
                Query = "How to resolve cross-border payment webhook signature verification failure (跨境支付回调验签失败排障)",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Tag = "验签核心", Text = "跨境支付 Webhook 签名失败排障核心：支付网关回调请求体通常采用 HMAC-SHA256 算法签名。商户端在进行签名计算前，必须使用原始二进制字节流（Raw Body Buffer）计算摘要，严禁反序列化为 JSON 实体后再重新格式化序列化；同时确保服务端时钟已通过 NTP 同步，防止防重放时间戳漂移超出允许的 300 秒容差窗口。", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Tag = "排障规范", Text = "Cross-border payment webhook signature verification troubleshooting: Validate that the signature header matches HMAC-SHA256(raw_payload, webhook_secret). Mismatches commonly stem from reverse proxy header modification, character encoding mismatches (must enforce UTF-8 without BOM), or stripping trailing newline characters.", ExpectedRelevance = 0.90 },
                    new() { Id = "doc3", Tag = "重试幂等", Text = "跨境外汇支付异步通知幂等性与重试规范：当商户回调端点返回 HTTP 500 内部错误或验签失败时，海外支付网关将自动触发指数退避重发机制（重试间隔为 15秒、1分钟、5分钟、30分钟直至 24小时），商户系统需基于出海交易单号实现强幂等更新以避免重复记账。", ExpectedRelevance = 0.85 },
                    new() { Id = "doc4", Tag = "合规安全", Text = "PCI-DSS Level 1 international payment security standard: All external webhook notification URLs must enforce TLS 1.3 with forward secrecy ciphers, and production merchant systems should implement mutual TLS (mTLS) or strict IP whitelisting for incoming gateway requests.", ExpectedRelevance = 0.75 },
                    new() { Id = "doc5", Tag = "动态结汇", Text = "国际多币种动态结汇（DCC）与实时汇率报价锁定：跨境商户在接收支付成功回调后，财务账单系统需记录交易时点的清算汇率、汇兑手续费率及发卡行跨境外汇处理费，实现自动化外汇头寸对冲。", ExpectedRelevance = 0.60 },
                    new() { Id = "doc6", Tag = "身份鉴权", Text = "OAuth 2.0 Client Credentials Grant 鉴权机制：跨境金融开放平台通过颁发有效期为 7200 秒的 Bearer Access Token 校验请求方合法身份，并在令牌过期前 10 分钟支持静默平滑轮换刷新。", ExpectedRelevance = 0.45 },
                    new() { Id = "doc7", Tag = "移动集成", Text = "Apple Pay 与 Google Pay 移动端支付集成规范：客户端通过安全芯片（Secure Element）生成一次性加密支付令牌（Payment Token），商户端将密文提交至收单网关进行解密结算，全流程不触碰真实银行卡号。", ExpectedRelevance = 0.30 },
                    new() { Id = "doc8", Tag = "海关报关", Text = "International cross-border customs declaration (HS Code classification): E-commerce platforms must submit cross-border e-commerce direct purchase import orders and air freight manifest declarations to clearance customs in compliance with local tariff categories.", ExpectedRelevance = 0.15 },
                    new() { Id = "doc9", Tag = "航空货运", Text = "国际航空货运提单（Air Waybill）与海外仓跨国揽收追踪：通过对接国际物流承运商 REST API 实时订阅港口清关放行状态与最后一公里卡车派送轨迹动态。", ExpectedRelevance = 0.08 },
                    new() { Id = "doc10", Tag = "国际标准", Text = "国际标准化组织 ISO 8601 关于协调世界时（UTC）及 ISO 4217 货币代码（如 USD、EUR、GBP）的技术规范与发展沿革。", ExpectedRelevance = 0.02 }
                }
            },
            "enterprise-faq-rerank" or "faq-matching" => new RerankTestRequest
            {
                Query = "购买的生鲜食品破损变质如何申请退款与理赔标准",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Tag = "理赔流程", Text = "《生鲜易腐类商品售后理赔细则》第一条【时效与理赔流程】：生鲜水果、水产冷鲜等易损易腐商品不适用 7 天无理由退货；签收后 24 小时内发现破损腐烂、变质发酸的，在‘我的订单’点击‘极速退款-生鲜破损’，上传带面单的外箱及商品清晰坏斑照片，系统将在 1 小时内极速审核并原路退回全款。", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Tag = "损耗补偿", Text = "《生鲜商品按比例损耗补偿标准表》：生鲜蔬果到货后局部轻微挤压擦伤（坏损比例小于 20%），可申请 30% 体验优惠券补偿；若主食用部分腐烂变质超过 30%，按照实际坏果比例精准折算现金退款；若坏损比例超过 50% 或大面积霉变，支持全额整单免退货退款并赠送补发无门槛券。", ExpectedRelevance = 0.90 },
                    new() { Id = "doc3", Tag = "凭证核验", Text = "生鲜售后凭证核验与特殊情况处理指南：有效理赔凭证需包含快递运单面单全貌、坏果特写（能清晰分辨霉变斑点或出水腐烂）以及整体坏果计数排开对比照。若外包装箱已被清理但能提供签收时快递员现场开箱异常记录的，人工客服将专席接入快速放行理赔。", ExpectedRelevance = 0.85 },
                    new() { Id = "doc4", Tag = "冷链脱冷", Text = "冷链干冰融化与全程温度脱冷责任界定：若冷藏冷冻食品送达时箱内冰袋完全融化常温、货物温度高于 10℃ 导致变质的，属于冷链承运商配送延误或断链责任，运费险将全额垫付商品全款，由平台向物流方追偿。", ExpectedRelevance = 0.70 },
                    new() { Id = "doc5", Tag = "日用品退换", Text = "普通非生鲜日用品 7 天无理由退换货规则：未拆封、标签吊牌齐全且不影响二次销售的常规服装服饰与家居小百货，自签收次日起 7 天内均可发起无理由退换货申请，运费由购买了运费险的保单自动抵扣首重运费。", ExpectedRelevance = 0.45 },
                    new() { Id = "doc6", Tag = "发票开具", Text = "企业采购电子发票开具与抬头修改规范：生鲜初级食用农产品支持开具增值税普通发票，订单完成后可在‘资产中心-发票管理’提交申请，系统在订单确认收货后 24 小时内开具并推送至用户注册电子邮箱。", ExpectedRelevance = 0.30 },
                    new() { Id = "doc7", Tag = "会员积分", Text = "电商会员积分获取与权益兑换指引：用户在商城完成订单交易后，每消费 1 元可累积 1 个会员积分，积分可在积分商城兑换全品类满减券、生鲜专属包邮券或联名实体礼品。", ExpectedRelevance = 0.15 },
                    new() { Id = "doc8", Tag = "包裹代管", Text = "自提柜与菜鸟驿站包裹超期滞留代管公告：常温普通包裹免费保管时长为 48 小时，超过时限按 1 元/天加收超期保管费；冷链生鲜包裹到柜后请务必在 6 小时内取出，超时变质造成的损失由收件方自行承担。", ExpectedRelevance = 0.08 },
                    new() { Id = "doc9", Tag = "冰箱调温", Text = "家用多门风冷无霜冰箱冷藏室与冷冻室合理温度调节建议：冷藏室建议维持在 2℃~4℃ 最佳保鲜区间，软冷冻室维持在 -7℃ 以免破坏鲜肉肌理纤维细胞壁。", ExpectedRelevance = 0.04 },
                    new() { Id = "doc10", Tag = "轻食膳食", Text = "现代都市轻食与地中海膳食结构的营养配比指南：以高膳食纤维蔬菜、全谷物杂粮搭配深海三文鱼及优质初榨橄榄油为主，有助于改善人体血脂指标与代谢机能。", ExpectedRelevance = 0.02 }
                }
            },
            "antonym-polarity-rerank" => new RerankTestRequest
            {
                Query = "关于该抗心律失常处方药在心源性休克与重度心衰患者中的临床用药禁忌与推荐指征",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Tag = "一线转复推荐", Text = "临床治疗指南明确推荐：本抗心律失常处方药对轻中度室性早搏及阵发性心动过速具备优异疗效，推荐作为一线转复指征用药，并给出了具体口服治疗方案与安全监测指标。", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Tag = "规范用药指征", Text = "规范化临床用药指征：针对无器质性心脏病的心律失常患者，推荐起始剂量为每日二次，连续监测心电图 PR 间期与 QTc 延长趋势以确保用药平稳。", ExpectedRelevance = 0.90 },
                    new() { Id = "doc3", Tag = "药效对照研究", Text = "药效动力学与临床对照研究：本品能有效抑制快速心室反应，在转复并维持正常窦性心律方面显著优于传统安慰剂对照组。", ExpectedRelevance = 0.80 },
                    new() { Id = "doc4", Tag = "代谢与调量", Text = "特殊人群代谢与剂量调整原则：老年患者及轻度肌酐清除率下降者，用药需减量 25%，并定期复查肝肾功能与血清电解质浓度。", ExpectedRelevance = 0.65 },
                    new() { Id = "doc5", Tag = "生理监测", Text = "该药物在非心血管病患中的生理代谢监测：对血脂、空腹血糖水平无明显临床干扰，但服药期间应避免剧烈运动与摄入高浓度咖啡因。", ExpectedRelevance = 0.40 },
                    new() { Id = "doc6", Tag = "硬对立-极性相反", Text = "本品绝对禁忌用于心源性休克、重度充血性心力衰竭及二度以上房室传导阻滞，使用可直接导致外周血管阻力崩溃与血压骤降，甚至诱发心脏停搏。", ExpectedRelevance = 0.15 },
                    new() { Id = "doc7", Tag = "否定排他反转", Text = "心血管急救指南强制规范：心源性休克与急性心衰发作期严禁使用任何负性肌力心律转复药物，不仅毫无适用指征，反而会直接加剧循环衰竭危及生命。", ExpectedRelevance = 0.12 },
                    new() { Id = "doc8", Tag = "主谓与方向相反", Text = "回顾性病历报告提示：心源性休克患者误服该药后，左室射血分数（LVEF）急剧恶化下降，导致恶性心律失常发生率剧增，与改善心功能目标完全相反。", ExpectedRelevance = 0.08 },
                    new() { Id = "doc9", Tag = "无关诊疗", Text = "急诊科针对上消化道大出血患者的急诊胃镜止血规范与质子泵抑制剂（PPI）高剂量静脉滴注临床诊疗路径。", ExpectedRelevance = 0.04 },
                    new() { Id = "doc10", Tag = "无关领域", Text = "国际空间站生命保障系统环控生保电解制氧装置与水循环再生设备运行维护规程与故障隔离手册。", ExpectedRelevance = 0.01 }
                }
            },
            "version-temporal-rerank" => new RerankTestRequest
            {
                Query = "在 Vue 3 与现代前端工程中，官方推荐的全局状态管理与组件通信方案及 Composition API 最佳实践",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Tag = "官方推荐-Pinia", Text = "Vue 3 官方推荐全局状态管理库 Pinia：全面支持 TypeScript 类型推断、摒弃了冗余的 mutations、采用模块化扁平 store 设计，并无缝支持 SSR 服务端渲染与 Devtools 时间旅行调试。", ExpectedRelevance = 0.96 },
                    new() { Id = "doc2", Tag = "官方规范-组合式API", Text = "Vue 3 组合式 API（Composition API）与 <script setup> 响应式开发规范：推荐使用 ref、reactive 与 computed 声明状态，通过自定义 Composable 函数封装可复用业务逻辑，取代脆弱的 mixins。", ExpectedRelevance = 0.92 },
                    new() { Id = "doc3", Tag = "跨组件通信-Provide/Inject", Text = "Vue 3 深度嵌套组件通信规范：通过 provide 和 inject 配合 readonly 响应式对象实现祖孙跨层级单向数据流传递，避免 prop 逐级钻取透传（Prop Drilling）。", ExpectedRelevance = 0.85 },
                    new() { Id = "doc4", Tag = "事件总线替代", Text = "在 Vue 3 中，$on、$off 和 $once 实例事件总线已被官方彻底移除；对于简单的跨组件解耦通信，推荐使用 mitt 或 tiny-emitter 轻量发布订阅第三方库代替。", ExpectedRelevance = 0.72 },
                    new() { Id = "doc5", Tag = "现代化构建-Vite", Text = "Vue 3 前端工程现代化构建方案推荐采用 Vite，基于原生 ESM 模块实现毫秒级冷启动与极速热模块替换（HMR），相比传统 Webpack 大幅提升开发体验。", ExpectedRelevance = 0.60 },
                    new() { Id = "doc6", Tag = "版本演进冲突-Vuex旧版", Text = "Vuex 3.x/4.x 集中式状态管理规范：所有状态变更必须严格通过同步 mutations 提交 commit，异步操作必须封装在 actions 中分发 dispatch，通过嵌套 modules 树状拆分状态。", ExpectedRelevance = 0.35 },
                    new() { Id = "doc7", Tag = "版本废弃-Vue2选项式", Text = "Vue 2 选项式 API（Options API）架构设计：使用 data、methods、computed、watch 组织组件选项，多组件代码逻辑复用主要通过 mixins 混入实现。", ExpectedRelevance = 0.20 },
                    new() { Id = "doc8", Tag = "版本废弃-EventBus旧方案", Text = "Vue 2 时代常用的全局 EventBus 方案：通过 new Vue() 实例作为全局事件中心，在组件 mounted 中绑定 this.$bus.$on，在 destroyed 中通过 $off 解绑事件。", ExpectedRelevance = 0.15 },
                    new() { Id = "doc9", Tag = "不相关-React并发", Text = "React 19 核心并发特性与 Server Actions：通过 useTransition、useActionState 统一处理客户端服务端异步操作，搭配 Zustand 进行客户端原子状态管理。", ExpectedRelevance = 0.05 },
                    new() { Id = "doc10", Tag = "不相关-容器部署", Text = "Nginx 官方 Docker 镜像作为静态 Web 前端部署底座：配置 gzip 压缩、缓存强协商控制及 try_files $uri $uri/ /index.html 单页面路由重写规则。", ExpectedRelevance = 0.01 }
                }
            },
            "numerical-entity-rerank" => new RerankTestRequest
            {
                Query = "选购要求：适用于工业物联网户外严苛环境的高精度温湿度传感器，工作温度必须覆盖 -40℃至85℃，供电电压兼容 12V 至 24V DC，且测量精度需达到 ±0.2℃ 以内",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Tag = "完美满足-严苛工业级", Text = "工业级数字式温湿度变送器 Modbus-RTU 型号 SHT-45X：工作温度范围为 -40℃~+85℃，宽电压供电范围 DC 10V~30V（完美适配 12V/24V 电源系统），温度测量精度高达 ±0.15℃，具备 IP67 防水防尘等级与过压反接保护，专为户外恶劣环境设计。", ExpectedRelevance = 0.96 },
                    new() { Id = "doc2", Tag = "满足参数-高精度变送器", Text = "高可靠性工业温湿度探头 HMP-80：额定工作温度 -40℃ 到 100℃，供电输入支持 12V/24V 直流自适应，测温精度在全量程内优于 ±0.2℃，采用特氟龙防护滤芯抗化学气体腐蚀。", ExpectedRelevance = 0.90 },
                    new() { Id = "doc3", Tag = "参数基本满足-宽温型", Text = "户外气象观测站专业温湿变送器：工作温度 -40℃~85℃，支持标准 24V DC 工业供电（低至 12V 正常启动），温度精度 ±0.2℃，标配铝合金防辐射百叶箱与 RS485 接口。", ExpectedRelevance = 0.85 },
                    new() { Id = "doc4", Tag = "参数边际-精度稍欠", Text = "管路插入式工业温度变送器：工作环境温度 -40℃ 至 85℃，支持 12V~24V DC 供电，测温元件采用 A 级 PT100，整机系统精度为 ±0.35℃，配备隔爆铸铝接线盒。", ExpectedRelevance = 0.60 },
                    new() { Id = "doc5", Tag = "参数边际-温宽收窄", Text = "仓储冷链专用数字温湿度记录仪：工作温度范围 -20℃ 至 70℃，供电支持 12V~24V 直流外接电源，测量精度达到 ±0.2℃，内置 10 万组数据存储与报警蜂鸣器。", ExpectedRelevance = 0.45 },
                    new() { Id = "doc6", Tag = "硬约束越界-温宽不符", Text = "数据中心室内精密机房温湿度传感器：仅适用于商业室内环境，工作温度范围 0℃ 至 50℃（无法在 -40℃ 极寒启动），工作电压 12V/24V DC，测量精度 ±0.2℃。", ExpectedRelevance = 0.25 },
                    new() { Id = "doc7", Tag = "硬约束越界-电压超限", Text = "高精度微功耗温湿度芯片模块：温度测量精度高达 ±0.1℃，工作温度 -40℃~85℃，但仅支持 3.3V 或 5V 弱电供电，严禁直接接入 12V 或 24V 工业供电以免瞬间击穿烧毁。", ExpectedRelevance = 0.20 },
                    new() { Id = "doc8", Tag = "硬约束越界-精度不达标", Text = "户外大棚简易温湿度采集器：工作温度 -40℃~85℃，12V~24V 供电，但采用低成本热敏电阻，测量精度仅为 ±1.5℃，无法满足高精度工业严苛监控要求。", ExpectedRelevance = 0.15 },
                    new() { Id = "doc9", Tag = "领域不符-红外热像仪", Text = "工业在线式长波红外热像仪：测温范围 -20℃~500℃，精度 ±2% 或 ±2℃，支持以太网 PoE 与 24V DC 供电，主要用于高压电气设备热缺陷巡检。", ExpectedRelevance = 0.05 },
                    new() { Id = "doc10", Tag = "无关技术-高斯混合模型", Text = "高斯混合模型（GMM）在时间序列传感器异常检测中的数学建模：利用 EM 期望最大化算法对多维变量的概率分布进行拟合，识别偏离正态分布的离群点。", ExpectedRelevance = 0.01 }
                }
            },
            _ => null
        };
    }
}

// ==================== 请求/响应模型 ====================

public class RerankTestRequest
{
    public string Query { get; set; } = "";
    public List<RerankDocumentInput> Documents { get; set; } = new();
    public string? ModelName { get; set; }
}

public class RerankDocumentInput
{
    public string? Id { get; set; }
    public string? Tag { get; set; }
    public string Text { get; set; } = "";
    public double? ExpectedRelevance { get; set; }
}

public class RerankTestResult
{
    public string TestType { get; set; } = "rerank";
    public string ModelName { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public long QueryMs { get; set; }
    public List<RerankDocumentResult> Documents { get; set; } = new();

    // 评估指标
    public double Ndcg { get; set; }
    public double Mrr { get; set; }
    public double Map { get; set; }
    public double RankingAccuracy { get; set; }
    public double MeanAbsoluteError { get; set; }
}

public class RerankDocumentResult
{
    public string Id { get; set; } = "";
    public string? Tag { get; set; }
    public string Text { get; set; } = "";
    public double RelevanceScore { get; set; }
    public int OriginalIndex { get; set; }
    public int NewRank { get; set; }
    public double? ExpectedRelevance { get; set; }
    public double? Deviation => ExpectedRelevance.HasValue ? Math.Abs(RelevanceScore - ExpectedRelevance.Value) : null;
}

public class RerankPresetSuiteInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int DocumentCount { get; set; }
}

public class RerankTestStatistics
{
    public int TotalTests { get; set; }
    public List<RerankModelStatistics> ByModel { get; set; } = new();
}

public class RerankModelStatistics
{
    public string ModelName { get; set; } = "";
    public int TotalTests { get; set; }
    public double AvgNdcg { get; set; }
    public double AvgMrr { get; set; }
    public double AvgMap { get; set; }
    public double AvgRankingAccuracy { get; set; }
    public double AvgMae { get; set; }
    public double AvgQueryMs { get; set; }
}

public class RerankBenchmarkRequest
{
    /// <summary>
    /// 并发数
    /// </summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>
    /// 总请求数
    /// </summary>
    public int TotalRequests { get; set; } = 100;

    /// <summary>
    /// 每个请求的文档数
    /// </summary>
    public int DocumentCount { get; set; } = 5;
}

public class RerankBenchmarkResult
{
    public DateTime Timestamp { get; set; }
    public int Concurrency { get; set; }
    public int TotalRequests { get; set; }
    public int DocumentCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public long TotalMs { get; set; }

    /// <summary>
    /// 每秒请求数 (QPS)
    /// </summary>
    public double Qps { get; set; }

    /// <summary>
    /// 平均延迟 (毫秒)
    /// </summary>
    public double AvgLatencyMs { get; set; }

    /// <summary>
    /// 最小延迟 (毫秒)
    /// </summary>
    public long MinLatencyMs { get; set; }

    /// <summary>
    /// 最大延迟 (毫秒)
    /// </summary>
    public long MaxLatencyMs { get; set; }

    /// <summary>
    /// P50 延迟 (毫秒)
    /// </summary>
    public long P50LatencyMs { get; set; }

    /// <summary>
    /// P95 延迟 (毫秒)
    /// </summary>
    public long P95LatencyMs { get; set; }

    /// <summary>
    /// P99 延迟 (毫秒)
    /// </summary>
    public long P99LatencyMs { get; set; }

    /// <summary>
    /// 错误信息 (最多5条)
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

internal class BenchmarkTestQuery
{
    public string Query { get; set; } = "";
    public List<string> Documents { get; set; } = new();
}
