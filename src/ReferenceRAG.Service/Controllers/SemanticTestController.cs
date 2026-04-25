using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Models;
using System.Diagnostics;
using System.Text.Json;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 语义查询测试 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SemanticTestController : ControllerBase
{
    private readonly IEmbeddingService _embeddingService;
    private readonly ITokenizer _tokenizer;
    private readonly TestRecordStore _recordStore;
    private readonly ILogger<SemanticTestController> _logger;
    private readonly string _testSuitesPath;

    public SemanticTestController(
        IEmbeddingService embeddingService,
        ITokenizer tokenizer,
        TestRecordStore recordStore,
        ILogger<SemanticTestController> logger)
    {
        _embeddingService = embeddingService;
        _tokenizer = tokenizer;
        _recordStore = recordStore;
        _logger = logger;
        _testSuitesPath = Path.Combine(Environment.CurrentDirectory, "data", "test-suites");
    }

    /// <summary>
    /// 模型健康诊断探针 —— 用 3 个固定文本对验证向量质量
    /// </summary>
    [HttpGet("model-probe")]
    public async Task<ActionResult<ModelProbeResult>> ProbeModel()
    {
        var probe = new ModelProbeResult
        {
            ModelName          = _embeddingService.ModelName,
            IsSimulationMode   = _embeddingService.IsSimulationMode,
            Dimension          = _embeddingService.Dimension,
            SupportsAsymmetric = _embeddingService.SupportsAsymmetricEncoding,
            Timestamp          = DateTime.UtcNow
        };

        try
        {
            // 1. 自相似度（应 ≈ 1.0）
            var v1 = await _embeddingService.EncodeAsync("机器学习是人工智能的子领域");
            var v2 = await _embeddingService.EncodeAsync("机器学习是人工智能的子领域");
            probe.SelfSimilarity = (float)MathHelper.CosineSimilarity(v1, v2);

            // 2. 高相似对（期望 ≥ 0.5）
            var qH = await _embeddingService.EncodeAsync("我喜欢吃苹果");
            var tH = await _embeddingService.EncodeAsync("我爱吃苹果");
            probe.HighSimilarityActual   = (float)MathHelper.CosineSimilarity(qH, tH);
            probe.HighSimilarityExpected = 0.85f;

            // 3. 低相似对（期望 ≤ 0.15）
            var qL = await _embeddingService.EncodeAsync("今天天气很热");
            var tL = await _embeddingService.EncodeAsync("如何做红烧肉");
            probe.LowSimilarityActual   = (float)MathHelper.CosineSimilarity(qL, tL);
            probe.LowSimilarityExpected = 0.10f;

            // 向量采样（前 8 维，用于判断是否为随机值）
            probe.VectorSample = v1.Take(8).Select(f => (double)f).ToArray();

            // 健康判断：自相似≈1、高相似>0.4、低相似<0.5（高质量模型语义不相关句子通常 0.3-0.5）
            probe.Healthy = !probe.IsSimulationMode
                && probe.SelfSimilarity   > 0.990f
                && probe.HighSimilarityActual > 0.40f
                && probe.LowSimilarityActual  < 0.50f;

            _logger.LogInformation(
                "[ModelProbe] 模型={Model} 仿真={Sim} 维度={Dim} 自相似={Self:F3} 高相似={Hi:F3} 低相似={Lo:F3} 健康={Healthy}",
                probe.ModelName, probe.IsSimulationMode, probe.Dimension,
                probe.SelfSimilarity, probe.HighSimilarityActual, probe.LowSimilarityActual, probe.Healthy);
        }
        catch (Exception ex)
        {
            probe.Error = ex.Message;
            _logger.LogError(ex, "[ModelProbe] 探针失败");
        }

        return Ok(probe);
    }

    /// <summary>
    /// 短文本语义相似度测试
    /// </summary>
    [HttpPost("short-text")]
    public async Task<ActionResult<SemanticTestResult>> TestShortText([FromBody] ShortTextTestRequest request)
    {
        var result = new SemanticTestResult
        {
            TestType  = "short-text",
            ModelName = _embeddingService.ModelName,
            Timestamp = DateTime.UtcNow
        };

        _logger.LogDebug(
            "[SemanticTest] short-text | 模型={Model} 仿真={Sim} 维度={Dim} | Query={Query}",
            _embeddingService.ModelName,
            _embeddingService.IsSimulationMode,
            _embeddingService.Dimension,
            request.Query.Length > 30 ? request.Query[..30] + "…" : request.Query);

        try
        {
            var sw = Stopwatch.StartNew();

            // 分离编码：query 用 Query 模式，candidates 用 Document 模式（支持非对称编码模型）
            var queryVectors = await _embeddingService.EncodeBatchAsync(
                new[] { request.Query }, EmbeddingMode.Query);
            result.QueryEmbeddingMs = sw.ElapsedMilliseconds;  // 只计 query 编码耗时
            var queryVector = queryVectors[0];

            var candidateTexts = request.Candidates.Select(c => c.Text).ToArray();
            var candidateVectors = await _embeddingService.EncodeBatchAsync(
                candidateTexts, EmbeddingMode.Document);
            result.QueryTokenCount = _tokenizer.CountTokens(request.Query);

            // 测试每个候选文本
            var candidateResults = new List<CandidateResult>();

            for (int i = 0; i < request.Candidates.Count; i++)
            {
                var candidate = request.Candidates[i];
                var candidateVector = candidateVectors[i];

                var similarity = MathHelper.CosineSimilarity(queryVector, candidateVector);
                var expectedScore = candidate.ExpectedSimilarity;

                candidateResults.Add(new CandidateResult
                {
                    Text = candidate.Text,
                    Similarity = similarity,
                    ExpectedSimilarity = expectedScore,
                    Deviation = Math.Abs(similarity - expectedScore),
                    TokenCount = _tokenizer.CountTokens(candidate.Text)
                });
            }

            result.Candidates = candidateResults;
            result.TotalEmbeddingMs = sw.ElapsedMilliseconds;
            result.AvgEmbeddingMs = (double)sw.ElapsedMilliseconds / (request.Candidates.Count + 1);

            // 计算准确度指标
            result.MeanAbsoluteError = candidateResults.Average(c => c.Deviation);
            result.RootMeanSquareError = Math.Sqrt(candidateResults.Average(c => c.Deviation * c.Deviation));
            result.CorrelationCoefficient = CalculateCorrelation(
                candidateResults.Select(c => c.ExpectedSimilarity).ToList(),
                candidateResults.Select(c => c.Similarity).ToList());

            // 排序准确度
            var expectedOrder = request.Candidates
                .Select((c, i) => new { Index = i, Score = c.ExpectedSimilarity })
                .OrderByDescending(x => x.Score)
                .Select(x => x.Index)
                .ToList();
            var actualOrder = candidateResults
                .Select((c, i) => new { Index = i, Score = c.Similarity })
                .OrderByDescending(x => x.Score)
                .Select(x => x.Index)
                .ToList();
            result.RankingAccuracy = CalculateRankingAccuracy(expectedOrder, actualOrder);

            // 保存记录
            var record = new TestRecord
            {
                Id = Guid.NewGuid().ToString(),
                TestType = "short-text",
                ModelName = result.ModelName,
                Timestamp = result.Timestamp,
                Query = request.Query,
                Results = new Dictionary<string, object>
                {
                    ["queryTokenCount"] = result.QueryTokenCount,
                    ["candidateCount"] = request.Candidates.Count,
                    ["meanAbsoluteError"] = result.MeanAbsoluteError,
                    ["rootMeanSquareError"] = result.RootMeanSquareError,
                    ["correlationCoefficient"] = result.CorrelationCoefficient,
                    ["rankingAccuracy"] = result.RankingAccuracy,
                    ["avgEmbeddingMs"] = result.AvgEmbeddingMs
                }
            };
            await _recordStore.SaveAsync(record);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Short text test failed");
            return StatusCode(500, new { error = "语义测试执行失败，请查看服务日志" });
        }
    }

    /// <summary>
    /// 长文本语义检索测试
    /// </summary>
    [HttpPost("long-text")]
    public async Task<ActionResult<LongTextTestResult>> TestLongText([FromBody] LongTextTestRequest request)
    {
        var result = new LongTextTestResult
        {
            TestType = "long-text",
            ModelName = request.ModelName ?? "bge-small-zh-v1.5",
            Timestamp = DateTime.UtcNow
        };

        try
        {
            var sw = Stopwatch.StartNew();

            // 分离编码：query 用 Query 模式，passages 用 Document 模式（支持非对称编码模型）
            var queryVectors = await _embeddingService.EncodeBatchAsync(
                new[] { request.Query }, EmbeddingMode.Query);
            var queryVector = queryVectors[0];

            var passageTexts = request.Passages.Select(p => p.Text).ToArray();
            var passageVectors = await _embeddingService.EncodeBatchAsync(
                passageTexts, EmbeddingMode.Document);
            result.QueryEmbeddingMs = sw.ElapsedMilliseconds;
            result.QueryTokenCount = _tokenizer.CountTokens(request.Query);

            // 2. 处理长文本段落
            var passageResults = new List<PassageResult>();

            for (int i = 0; i < request.Passages.Count; i++)
            {
                var passage = request.Passages[i];
                var passageVector = passageVectors[i];

                var similarity = MathHelper.CosineSimilarity(queryVector, passageVector);

                passageResults.Add(new PassageResult
                {
                    Id = passage.Id,
                    Text = passage.Text.Length > 200 ? passage.Text[..200] + "..." : passage.Text,
                    FullTextLength = passage.Text.Length,
                    Similarity = similarity,
                    IsRelevant = passage.IsRelevant,
                    TokenCount = _tokenizer.CountTokens(passage.Text)
                });
            }

            result.Passages = passageResults;
            result.TotalEmbeddingMs = sw.ElapsedMilliseconds;
            result.AvgEmbeddingMs = (double)sw.ElapsedMilliseconds / (request.Passages.Count + 1);

            // 3. 计算检索指标
            var relevantIds = request.Passages
                .Where(p => p.IsRelevant)
                .Select(p => p.Id)
                .ToHashSet();
            var retrievedIds = passageResults
                .OrderByDescending(p => p.Similarity)
                .Take(request.TopK)
                .Select(p => p.Id)
                .ToHashSet();

            var truePositives = retrievedIds.Count(id => relevantIds.Contains(id));
            var falsePositives = retrievedIds.Count(id => !relevantIds.Contains(id));
            var falseNegatives = relevantIds.Count(id => !retrievedIds.Contains(id));

            result.Precision = retrievedIds.Count > 0 ? (double)truePositives / retrievedIds.Count : 0;
            result.Recall = relevantIds.Count > 0 ? (double)truePositives / relevantIds.Count : 0;
            result.F1Score = (result.Precision + result.Recall) > 0 
                ? 2 * result.Precision * result.Recall / (result.Precision + result.Recall) 
                : 0;

            // 4. 计算 NDCG
            result.Ndcg = CalculateNdcg(
                request.Passages.Select(p => p.IsRelevant ? 1 : 0).ToList(),
                passageResults.OrderByDescending(p => p.Similarity).Select(p => p.IsRelevant ? 1 : 0).ToList(),
                request.TopK);

            // 5. 计算 MRR
            var firstRelevantRank = passageResults
                .OrderByDescending(p => p.Similarity)
                .Select((p, i) => new { p.IsRelevant, Rank = i + 1 })
                .FirstOrDefault(x => x.IsRelevant)?.Rank ?? 0;
            result.Mrr = firstRelevantRank > 0 ? 1.0 / firstRelevantRank : 0;

            // 保存记录
            var record = new TestRecord
            {
                Id = Guid.NewGuid().ToString(),
                TestType = "long-text",
                ModelName = result.ModelName,
                Timestamp = result.Timestamp,
                Query = request.Query,
                Results = new Dictionary<string, object>
                {
                    ["queryTokenCount"] = result.QueryTokenCount,
                    ["passageCount"] = request.Passages.Count,
                    ["relevantCount"] = relevantIds.Count,
                    ["topK"] = request.TopK,
                    ["precision"] = result.Precision,
                    ["recall"] = result.Recall,
                    ["f1Score"] = result.F1Score,
                    ["ndcg"] = result.Ndcg,
                    ["mrr"] = result.Mrr,
                    ["avgEmbeddingMs"] = result.AvgEmbeddingMs
                }
            };
            await _recordStore.SaveAsync(record);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Long text test failed");
            return StatusCode(500, new { error = "长文本测试执行失败，请查看服务日志" });
        }
    }

    /// <summary>
    /// 预设测试套件
    /// </summary>
    [HttpPost("preset/{suiteName}")]
    public async Task<ActionResult<SemanticTestResult>> RunPresetTest(string suiteName)
    {
        var suite = GetPresetSuite(suiteName);
        if (suite == null)
        {
            return NotFound(new { error = $"测试套件 '{suiteName}' 不存在" });
        }

        if (suite.Type == "short-text")
        {
            var result = await TestShortText(suite.ShortTextRequest!);
            return result.Result;
        }
        else
        {
            var result = await TestLongText(suite.LongTextRequest!);
            return result.Result;
        }
    }

    /// <summary>
    /// 获取可用测试套件
    /// </summary>
    [HttpGet("presets")]
    public ActionResult<List<PresetSuiteInfo>> GetPresetSuites()
    {
        return Ok(new List<PresetSuiteInfo>
        {
            new() { Name = "chinese-similarity", Description = "中文语义相似度测试", Type = "short-text" },
            new() { Name = "english-similarity", Description = "英文语义相似度测试", Type = "short-text" },
            new() { Name = "code-search", Description = "代码搜索测试", Type = "long-text" },
            new() { Name = "document-retrieval", Description = "文档检索测试", Type = "long-text" },
            new() { Name = "qa-matching", Description = "问答匹配测试", Type = "short-text" }
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
        var records = await _recordStore.GetAsync(modelName, testType, limit);
        return Ok(records);
    }

    /// <summary>
    /// 获取测试统计汇总
    /// </summary>
    [HttpGet("statistics")]
    public async Task<ActionResult<TestStatistics>> GetStatistics([FromQuery] string? modelName = null)
    {
        var records = await _recordStore.GetAsync(null, null, 1000);
        var filtered = modelName != null ? records.Where(r => r.ModelName == modelName).ToList() : records;

        var stats = new TestStatistics
        {
            TotalTests = filtered.Count,
            ByModel = filtered
                .GroupBy(r => r.ModelName)
                .Select(g => new ModelStatistics
                {
                    ModelName = g.Key,
                    TotalTests = g.Count(),
                    ShortTextTests = g.Count(r => r.TestType == "short-text"),
                    LongTextTests = g.Count(r => r.TestType == "long-text"),
                    AvgMae = g.Where(r => r.Results.ContainsKey("meanAbsoluteError"))
                        .Select(r => Convert.ToDouble(r.Results["meanAbsoluteError"]))
                        .DefaultIfEmpty(0)
                        .Average(),
                    AvgCorrelation = g.Where(r => r.Results.ContainsKey("correlationCoefficient"))
                        .Select(r => Convert.ToDouble(r.Results["correlationCoefficient"]))
                        .DefaultIfEmpty(0)
                        .Average(),
                    AvgPrecision = g.Where(r => r.Results.ContainsKey("precision"))
                        .Select(r => Convert.ToDouble(r.Results["precision"]))
                        .DefaultIfEmpty(0)
                        .Average(),
                    AvgRecall = g.Where(r => r.Results.ContainsKey("recall"))
                        .Select(r => Convert.ToDouble(r.Results["recall"]))
                        .DefaultIfEmpty(0)
                        .Average(),
                    AvgF1 = g.Where(r => r.Results.ContainsKey("f1Score"))
                        .Select(r => Convert.ToDouble(r.Results["f1Score"]))
                        .DefaultIfEmpty(0)
                        .Average(),
                    AvgEmbeddingMs = g.Where(r => r.Results.ContainsKey("avgEmbeddingMs"))
                        .Select(r => Convert.ToDouble(r.Results["avgEmbeddingMs"]))
                        .DefaultIfEmpty(0)
                        .Average()
                })
                .ToList(),
            ByTestType = filtered
                .GroupBy(r => r.TestType)
                .Select(g => new TestTypeStatistics
                {
                    TestType = g.Key,
                    Count = g.Count(),
                    AvgEmbeddingMs = g.Where(r => r.Results.ContainsKey("avgEmbeddingMs"))
                        .Select(r => Convert.ToDouble(r.Results["avgEmbeddingMs"]))
                        .DefaultIfEmpty(0)
                        .Average()
                })
                .ToList()
        };

        return Ok(stats);
    }

    /// <summary>
    /// 清除测试记录
    /// </summary>
    [HttpDelete("records")]
    public async Task<ActionResult> ClearRecords([FromQuery] string? modelName = null, [FromQuery] string? testType = null)
    {
        await _recordStore.ClearAsync(modelName, testType);
        return NoContent();
    }

    // ==================== 辅助方法 ====================

    private double CalculateCorrelation(List<double> expected, List<double> actual)
    {
        if (expected.Count != actual.Count || expected.Count < 2) return 0;

        var n = expected.Count;
        var sumX = expected.Sum();
        var sumY = actual.Sum();
        var sumXY = expected.Zip(actual, (x, y) => x * y).Sum();
        var sumX2 = expected.Sum(x => x * x);
        var sumY2 = actual.Sum(y => y * y);

        var numerator = n * sumXY - sumX * sumY;
        var varX = n * sumX2 - sumX * sumX;
        var varY = n * sumY2 - sumY * sumY;

        // 高精度模型（如 BGE-M3）可能导致所有相似度值近乎相等，方差趋近于 0
        // 浮点精度误差会使 varX/varY 变为微小负数，导致 Math.Sqrt 返回 NaN
        if (varX <= 0 || varY <= 0) return 0;

        var denominator = Math.Sqrt(varX * varY);
        return denominator < 1e-10 ? 0 : numerator / denominator;
    }

    private double CalculateRankingAccuracy(List<int> expected, List<int> actual)
    {
        if (expected.Count != actual.Count) return 0;

        var correct = 0;
        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i] == actual[i]) correct++;
        }

        return (double)correct / expected.Count;
    }

    private double CalculateNdcg(List<int> expectedRelevance, List<int> predictedRelevance, int k)
    {
        var actualDcg = CalculateDcg(predictedRelevance.Take(k).ToList());
        var idealDcg = CalculateDcg(expectedRelevance.OrderByDescending(r => r).Take(k).ToList());

        return idealDcg > 0 ? actualDcg / idealDcg : 0;
    }

    private double CalculateDcg(List<int> relevance)
    {
        double dcg = 0;
        for (int i = 0; i < relevance.Count; i++)
        {
            dcg += relevance[i] / Math.Log2(i + 2);
        }
        return dcg;
    }

    private PresetSuite? GetPresetSuite(string name)
    {
        // Path traversal prevention: reject names with path separators or parent references
        if (name.Contains("..") || name.Contains('/') || name.Contains('\\') || name.Contains(Path.DirectorySeparatorChar))
        {
            return null;
        }

        // 优先从文件加载
        var filePath = Path.Combine(_testSuitesPath, $"{name}.json");
        if (System.IO.File.Exists(filePath))
        {
            try
            {
                var json = System.IO.File.ReadAllText(filePath);
                var suite = JsonSerializer.Deserialize<PresetSuiteFile>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (suite != null)
                {
                    return new PresetSuite
                    {
                        Type = suite.Type,
                        ShortTextRequest = new ShortTextTestRequest
                        {
                            Query = suite.Query,
                            Candidates = suite.Candidates?.Select(c => new CandidateText
                            {
                                Text = c.Text,
                                ExpectedSimilarity = c.ExpectedSimilarity
                            }).ToList() ?? new List<CandidateText>()
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load preset suite from file: {File}", filePath);
            }
        }

        // 内置默认测试集（当文件不存在时使用）
        return name switch
        {
            "chinese-similarity" => new PresetSuite
            {
                Type = "short-text",
                ShortTextRequest = new ShortTextTestRequest
                {
                    Query = "如何学习编程",
                    Candidates = new List<CandidateText>
                    {
                        new() { Text = "编程学习需要从基础语法开始，逐步掌握数据结构和算法", ExpectedSimilarity = 0.9 },
                        new() { Text = "学习编程的方法包括阅读教程、实践项目和参与开源", ExpectedSimilarity = 0.85 },
                        new() { Text = "编程语言有很多种，如Python、Java、C++等", ExpectedSimilarity = 0.6 },
                        new() { Text = "今天天气很好，适合出去散步", ExpectedSimilarity = 0.1 },
                        new() { Text = "机器学习是人工智能的重要分支", ExpectedSimilarity = 0.3 }
                    }
                }
            },
            "english-similarity" => new PresetSuite
            {
                Type = "short-text",
                ShortTextRequest = new ShortTextTestRequest
                {
                    Query = "How to improve programming skills",
                    Candidates = new List<CandidateText>
                    {
                        new() { Text = "Practice coding every day and work on real projects", ExpectedSimilarity = 0.9 },
                        new() { Text = "Read code from experienced developers and learn patterns", ExpectedSimilarity = 0.85 },
                        new() { Text = "Programming languages have different syntax and features", ExpectedSimilarity = 0.5 },
                        new() { Text = "The weather is nice today for outdoor activities", ExpectedSimilarity = 0.1 },
                        new() { Text = "Machine learning models require large datasets", ExpectedSimilarity = 0.3 }
                    }
                }
            },
            "qa-matching" => new PresetSuite
            {
                Type = "short-text",
                ShortTextRequest = new ShortTextTestRequest
                {
                    Query = "什么是向量数据库？",
                    Candidates = new List<CandidateText>
                    {
                        new() { Text = "向量数据库是专门用于存储和检索高维向量的数据库系统", ExpectedSimilarity = 0.95 },
                        new() { Text = "向量数据库支持相似度搜索，常用于推荐系统和语义搜索", ExpectedSimilarity = 0.9 },
                        new() { Text = "数据库是用于存储和管理数据的系统", ExpectedSimilarity = 0.5 },
                        new() { Text = "向量是数学中的概念，表示大小和方向", ExpectedSimilarity = 0.4 },
                        new() { Text = "关系数据库使用表格存储数据", ExpectedSimilarity = 0.3 }
                    }
                }
            },
            _ => null
        };
    }
}

/// <summary>
/// 预设测试集文件格式
/// </summary>
public class PresetSuiteFile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "";
    public string Query { get; set; } = "";
    public List<CandidateTextFile>? Candidates { get; set; }
}

public class CandidateTextFile
{
    public string Text { get; set; } = "";
    public double ExpectedSimilarity { get; set; }
}

// ==================== 请求/响应模型 ====================

public class ShortTextTestRequest
{
    public string Query { get; set; } = "";
    public List<CandidateText> Candidates { get; set; } = new();
    public string? ModelName { get; set; }
}

public class CandidateText
{
    public string Text { get; set; } = "";
    public double ExpectedSimilarity { get; set; }
}

public class LongTextTestRequest
{
    public string Query { get; set; } = "";
    public List<Passage> Passages { get; set; } = new();
    public int TopK { get; set; } = 5;
    public string? ModelName { get; set; }
}

public class Passage
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public bool IsRelevant { get; set; }
}

public class SemanticTestResult
{
    public string TestType { get; set; } = "";
    public string ModelName { get; set; } = "";
    public DateTime Timestamp { get; set; }

    public long QueryEmbeddingMs { get; set; }
    public int QueryTokenCount { get; set; }
    public long TotalEmbeddingMs { get; set; }
    public double AvgEmbeddingMs { get; set; }

    public List<CandidateResult> Candidates { get; set; } = new();

    public double MeanAbsoluteError { get; set; }
    public double RootMeanSquareError { get; set; }
    public double CorrelationCoefficient { get; set; }
    public double RankingAccuracy { get; set; }
}

public class CandidateResult
{
    public string Text { get; set; } = "";
    public double Similarity { get; set; }
    public double ExpectedSimilarity { get; set; }
    public double Deviation { get; set; }
    public int TokenCount { get; set; }
}

public class LongTextTestResult
{
    public string TestType { get; set; } = "";
    public string ModelName { get; set; } = "";
    public DateTime Timestamp { get; set; }

    public long QueryEmbeddingMs { get; set; }
    public int QueryTokenCount { get; set; }
    public long TotalEmbeddingMs { get; set; }
    public double AvgEmbeddingMs { get; set; }

    public List<PassageResult> Passages { get; set; } = new();

    public double Precision { get; set; }
    public double Recall { get; set; }
    public double F1Score { get; set; }
    public double Ndcg { get; set; }
    public double Mrr { get; set; }
}

public class PassageResult
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public int FullTextLength { get; set; }
    public double Similarity { get; set; }
    public bool IsRelevant { get; set; }
    public int TokenCount { get; set; }
}

public class PresetSuite
{
    public string Type { get; set; } = "";
    public ShortTextTestRequest? ShortTextRequest { get; set; }
    public LongTextTestRequest? LongTextRequest { get; set; }
}

public class PresetSuiteInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "";
}

// ==================== 测试记录存储 ====================

public class TestRecordStore
{
    private readonly string _dataPath;
    private readonly List<TestRecord> _records = new();
    private readonly object _lock = new();

    public TestRecordStore()
    {
        _dataPath = Path.Combine(Environment.CurrentDirectory, "data", "test-records");
        Directory.CreateDirectory(_dataPath);
        LoadRecordsAsync().GetAwaiter().GetResult();
    }

    public async Task SaveAsync(TestRecord record)
    {
        lock (_lock)
        {
            _records.Add(record);
        }
        await SaveRecordsAsync();
    }

    public Task<List<TestRecord>> GetAsync(string? modelName, string? testType, int limit)
    {
        lock (_lock)
        {
            var query = _records.AsEnumerable();

            if (!string.IsNullOrEmpty(modelName))
                query = query.Where(r => r.ModelName == modelName);

            if (!string.IsNullOrEmpty(testType))
                query = query.Where(r => r.TestType == testType);

            return Task.FromResult(query.OrderByDescending(r => r.Timestamp).Take(limit).ToList());
        }
    }

    public async Task ClearAsync(string? modelName, string? testType)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(modelName) && string.IsNullOrEmpty(testType))
            {
                _records.Clear();
            }
            else
            {
                _records.RemoveAll(r =>
                    (modelName == null || r.ModelName == modelName) &&
                    (testType == null || r.TestType == testType));
            }
        }
        await SaveRecordsAsync();
    }

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // 允许 NaN/Infinity — 高质量模型（如 BGE-M3）的相似度分布集中时
        // CalculateCorrelation 的浮点精度误差可能产生 NaN，避免序列化异常
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private async Task LoadRecordsAsync()
    {
        var file = Path.Combine(_dataPath, "records.json");
        if (File.Exists(file))
        {
            var json = await File.ReadAllTextAsync(file);
            var records = System.Text.Json.JsonSerializer.Deserialize<List<TestRecord>>(json, _jsonOptions);
            if (records != null)
            {
                lock (_lock)
                {
                    _records.AddRange(records);
                }
            }
        }
    }

    private async Task SaveRecordsAsync()
    {
        var file = Path.Combine(_dataPath, "records.json");
        var json = System.Text.Json.JsonSerializer.Serialize(_records, _jsonOptions);
        await File.WriteAllTextAsync(file, json);
    }
}

public class TestRecord
{
    public string Id { get; set; } = "";
    public string TestType { get; set; } = "";
    public string ModelName { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string Query { get; set; } = "";
    public Dictionary<string, object> Results { get; set; } = new();
}

public class TestStatistics
{
    public int TotalTests { get; set; }
    public List<ModelStatistics> ByModel { get; set; } = new();
    public List<TestTypeStatistics> ByTestType { get; set; } = new();
}

public class ModelStatistics
{
    public string ModelName { get; set; } = "";
    public int TotalTests { get; set; }
    public int ShortTextTests { get; set; }
    public int LongTextTests { get; set; }
    public double AvgMae { get; set; }
    public double AvgCorrelation { get; set; }
    public double AvgPrecision { get; set; }
    public double AvgRecall { get; set; }
    public double AvgF1 { get; set; }
    public double AvgEmbeddingMs { get; set; }
}

public class TestTypeStatistics
{
    public string TestType { get; set; } = "";
    public int Count { get; set; }
    public double AvgEmbeddingMs { get; set; }
}

/// <summary>
/// 模型健康诊断结果
/// </summary>
public class ModelProbeResult
{
    public string ModelName { get; set; } = "";
    public bool IsSimulationMode { get; set; }
    public int Dimension { get; set; }
    public bool SupportsAsymmetric { get; set; }

    /// <summary>同一文本编两次的余弦相似度，健康模型应 ≈ 1.0</summary>
    public float SelfSimilarity { get; set; }

    /// <summary>同义词对实际相似度</summary>
    public float HighSimilarityActual { get; set; }
    public float HighSimilarityExpected { get; set; }

    /// <summary>无关词对实际相似度</summary>
    public float LowSimilarityActual { get; set; }
    public float LowSimilarityExpected { get; set; }

    /// <summary>向量采样（前 8 维），用于判断是否为随机值</summary>
    public double[] VectorSample { get; set; } = [];

    /// <summary>综合判断：模型是否正常工作</summary>
    public bool Healthy { get; set; }

    public string? Error { get; set; }
    public DateTime Timestamp { get; set; }
}
