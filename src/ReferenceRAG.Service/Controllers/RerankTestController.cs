using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using System.Diagnostics;
using System.Text.Json;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 重排测试 API
/// </summary>
[ApiController]
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
            new() { Name = "chinese-rerank", Description = "中文重排测试", DocumentCount = 5 },
            new() { Name = "english-rerank", Description = "英文重排测试", DocumentCount = 5 },
            new() { Name = "mixed-rerank", Description = "中英混合重排测试", DocumentCount = 6 },
            new() { Name = "code-rerank", Description = "代码相关重排测试", DocumentCount = 5 }
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
                    new() { Id = "doc1", Text = "编程学习需要从基础语法开始，逐步掌握数据结构和算法", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Text = "学习编程的方法包括阅读教程、实践项目和参与开源", ExpectedRelevance = 0.85 },
                    new() { Id = "doc3", Text = "编程语言有很多种，如Python、Java、C++等", ExpectedRelevance = 0.5 },
                    new() { Id = "doc4", Text = "今天天气很好，适合出去散步", ExpectedRelevance = 0.1 },
                    new() { Id = "doc5", Text = "机器学习是人工智能的重要分支", ExpectedRelevance = 0.3 }
                }
            },
            "english-rerank" => new RerankTestRequest
            {
                Query = "How to improve programming skills",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Text = "Practice coding every day and work on real projects to improve your programming skills", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Text = "Read code from experienced developers and learn common patterns and best practices", ExpectedRelevance = 0.85 },
                    new() { Id = "doc3", Text = "Programming languages have different syntax and features that developers should understand", ExpectedRelevance = 0.5 },
                    new() { Id = "doc4", Text = "The weather is nice today for outdoor activities and sports", ExpectedRelevance = 0.1 },
                    new() { Id = "doc5", Text = "Machine learning models require large datasets and computational resources", ExpectedRelevance = 0.3 }
                }
            },
            "mixed-rerank" => new RerankTestRequest
            {
                Query = "什么是向量数据库及其应用场景",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Text = "向量数据库是专门用于存储和检索高维向量的数据库系统，广泛应用于相似度搜索、推荐系统、语义检索等场景", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Text = "Vector databases like Pinecone and Milvus are optimized for similarity search in machine learning applications", ExpectedRelevance = 0.85 },
                    new() { Id = "doc3", Text = "数据库是用于存储和管理数据的系统，包括关系型数据库和非关系型数据库", ExpectedRelevance = 0.4 },
                    new() { Id = "doc4", Text = "向量是数学中的概念，表示具有大小和方向的量", ExpectedRelevance = 0.3 },
                    new() { Id = "doc5", Text = "Machine learning involves training models on data to make predictions", ExpectedRelevance = 0.35 },
                    new() { Id = "doc6", Text = "今天天气很好，适合出去散步", ExpectedRelevance = 0.05 }
                }
            },
            "code-rerank" => new RerankTestRequest
            {
                Query = "如何实现快速排序算法",
                Documents = new List<RerankDocumentInput>
                {
                    new() { Id = "doc1", Text = "快速排序的实现：选择基准元素，将数组分为两部分，递归排序。时间复杂度平均O(n log n)", ExpectedRelevance = 0.95 },
                    new() { Id = "doc2", Text = "def quicksort(arr):\n    if len(arr) <= 1:\n        return arr\n    pivot = arr[len(arr) // 2]\n    left = [x for x in arr if x < pivot]\n    middle = [x for x in arr if x == pivot]\n    right = [x for x in arr if x > pivot]\n    return quicksort(left) + middle + quicksort(right)", ExpectedRelevance = 0.9 },
                    new() { Id = "doc3", Text = "排序算法有很多种，包括冒泡排序、插入排序、选择排序、归并排序等", ExpectedRelevance = 0.5 },
                    new() { Id = "doc4", Text = "数据库查询优化涉及索引设计和查询计划分析", ExpectedRelevance = 0.2 },
                    new() { Id = "doc5", Text = "算法的时间复杂度分析是评估算法效率的重要方法", ExpectedRelevance = 0.4 }
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
    public string Text { get; set; } = "";
    public double RelevanceScore { get; set; }
    public int OriginalIndex { get; set; }
    public int NewRank { get; set; }
    public double? ExpectedRelevance { get; set; }
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
