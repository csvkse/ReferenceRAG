using Microsoft.AspNetCore.Mvc;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Services;
using System.Diagnostics;

namespace ObsidianRAG.Service.Controllers;

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
    }

    /// <summary>
    /// 短文本语义相似度测试
    /// </summary>
    [HttpPost("short-text")]
    public async Task<ActionResult<SemanticTestResult>> TestShortText([FromBody] ShortTextTestRequest request)
    {
        var result = new SemanticTestResult
        {
            TestType = "short-text",
            ModelName = request.ModelName ?? "bge-small-zh-v1.5",
            Timestamp = DateTime.UtcNow
        };

        try
        {
            var sw = Stopwatch.StartNew();

            // 生成查询向量
            var queryVector = await _embeddingService.EncodeAsync(request.Query);
            result.QueryEmbeddingMs = sw.ElapsedMilliseconds;
            result.QueryTokenCount = _tokenizer.CountTokens(request.Query);

            // 测试每个候选文本
            var candidateResults = new List<CandidateResult>();
            var totalEmbeddingMs = 0L;

            foreach (var candidate in request.Candidates)
            {
                sw.Restart();
                var candidateVector = await _embeddingService.EncodeAsync(candidate.Text);
                totalEmbeddingMs += sw.ElapsedMilliseconds;

                var similarity = CosineSimilarity(queryVector, candidateVector);
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
            result.TotalEmbeddingMs = totalEmbeddingMs;
            result.AvgEmbeddingMs = (double)totalEmbeddingMs / request.Candidates.Count;

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
            return StatusCode(500, new { error = ex.Message });
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

            // 1. 生成查询向量
            var queryVector = await _embeddingService.EncodeAsync(request.Query);
            result.QueryEmbeddingMs = sw.ElapsedMilliseconds;
            result.QueryTokenCount = _tokenizer.CountTokens(request.Query);

            // 2. 处理长文本段落
            var passageResults = new List<PassageResult>();
            var embeddingTimes = new List<long>();

            foreach (var passage in request.Passages)
            {
                sw.Restart();
                var passageVector = await _embeddingService.EncodeAsync(passage.Text);
                embeddingTimes.Add(sw.ElapsedMilliseconds);

                var similarity = CosineSimilarity(queryVector, passageVector);

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
            result.TotalEmbeddingMs = embeddingTimes.Sum();
            result.AvgEmbeddingMs = embeddingTimes.Average();

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
            return StatusCode(500, new { error = ex.Message });
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

    private float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator < 1e-10f ? 0 : dot / denominator;
    }

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
        var denominator = Math.Sqrt((n * sumX2 - sumX * sumX) * (n * sumY2 - sumY * sumY));

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

    private async Task LoadRecordsAsync()
    {
        var file = Path.Combine(_dataPath, "records.json");
        if (File.Exists(file))
        {
            var json = await File.ReadAllTextAsync(file);
            var records = System.Text.Json.JsonSerializer.Deserialize<List<TestRecord>>(json);
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
        var json = System.Text.Json.JsonSerializer.Serialize(_records, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
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
