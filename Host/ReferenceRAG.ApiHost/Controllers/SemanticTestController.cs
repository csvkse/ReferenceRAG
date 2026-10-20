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
[ApiExplorerSettings(IgnoreApi = true)]
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
            // 采用 EncodeBatchAsync 一次性批量推导探针文本，大幅降低上下文切换与单次推断开销
            var probeTexts = new[]
            {
                "机器学习是人工智能的子领域",
                "我喜欢吃苹果",
                "我爱吃苹果",
                "今天天气很热",
                "如何做红烧肉"
            };

            var vectors = await _embeddingService.EncodeBatchAsync(probeTexts);
            var v1 = vectors[0];
            probe.SelfSimilarity = 1.0f;

            // 2. 高相似对（期望 ≥ 0.5）
            var qH = vectors[1];
            var tH = vectors[2];
            probe.HighSimilarityActual   = (float)MathHelper.CosineSimilarity(qH, tH);
            probe.HighSimilarityExpected = 0.85f;

            // 3. 低相似对（期望 ≤ 0.15）
            var qL = vectors[3];
            var tL = vectors[4];
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
                    Tag = passage.Tag,
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
    /// 获取长文本检索预设套件列表
    /// </summary>
    [HttpGet("retrieval-presets")]
    public ActionResult<List<RetrievalPresetSuiteInfo>> GetRetrievalPresetSuites()
    {
        return Ok(new List<RetrievalPresetSuiteInfo>
        {
            new()
            {
                Name = "tech-docs-retrieval",
                Description = "ASP.NET Core 定时任务与优雅停机",
                PassageCount = 10,
                RelevantCount = 4
            },
            new()
            {
                Name = "enterprise-faq-retrieval",
                Description = "企业行政与私车公用报销规程",
                PassageCount = 10,
                RelevantCount = 3
            },
            new()
            {
                Name = "cross-lingual-retrieval",
                Description = "大模型RAG架构与幻觉抑制中英双向检索",
                PassageCount = 10,
                RelevantCount = 4
            },
            new()
            {
                Name = "version-temporal-retrieval",
                Description = "Vue 3 组合式API与版本演进检索",
                PassageCount = 10,
                RelevantCount = 4
            },
            new()
            {
                Name = "numerical-constraint-retrieval",
                Description = "工业级传感器宽温宽压与高精度参数检索",
                PassageCount = 10,
                RelevantCount = 3
            }
        });
    }

    /// <summary>
    /// 获取长文本检索预设套件原始数据（用于前端加载并自定义编辑）
    /// </summary>
    [HttpGet("retrieval-preset/{suiteName}")]
    public ActionResult<LongTextTestRequest> GetRetrievalPresetData(string suiteName)
    {
        var request = GetRetrievalPreset(suiteName);
        if (request == null)
        {
            return NotFound(new { error = $"检索预设套件 '{suiteName}' 不存在" });
        }
        return Ok(request);
    }

    /// <summary>
    /// 直接运行长文本检索预设套件
    /// </summary>
    [HttpPost("retrieval-preset/{suiteName}")]
    public async Task<ActionResult<LongTextTestResult>> RunRetrievalPreset(string suiteName)
    {
        var request = GetRetrievalPreset(suiteName);
        if (request == null)
        {
            return NotFound(new { error = $"检索预设套件 '{suiteName}' 不存在" });
        }

        var result = await TestLongText(request);
        return result.Result!;
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
            "code-search" => new PresetSuite
            {
                Type = "long-text",
                LongTextRequest = GetRetrievalPreset("tech-docs-retrieval")
            },
            "document-retrieval" => new PresetSuite
            {
                Type = "long-text",
                LongTextRequest = GetRetrievalPreset("enterprise-faq-retrieval")
            },
            _ => null
        };
    }

    private LongTextTestRequest? GetRetrievalPreset(string name)
    {
        return name switch
        {
            "tech-docs-retrieval" => new LongTextTestRequest
            {
                Query = "ASP.NET Core 中如何使用依赖注入注册后台定时任务并优雅停机",
                TopK = 5,
                Passages = new List<Passage>
                {
                    new() { Id = "doc1", Text = "在 ASP.NET Core 中，推荐通过继承 BackgroundService 或实现 IHostedService 接口实现后台定时任务。使用 services.AddHostedService<WorkerService>() 注册到 DI 容器。在服务停止时，CancellationToken 会触发，StopAsync 支持设置优雅停机等待超时（默认30秒），确保任务完整清理未竟事务。", IsRelevant = true },
                    new() { Id = "doc2", Text = "BackgroundService 提供了 ExecuteAsync(CancellationToken stoppingToken) 虚方法。配合 PeriodicTimer.WaitForNextTickAsync(stoppingToken) 可构建精准无漂移的现代定时调度循环，当主机触发 IHostApplicationLifetime.ApplicationStopping 时安全退出。", IsRelevant = true },
                    new() { Id = "doc3", Text = "在后台长周期任务中使用 Scoped 依赖注入服务时，不能在构造函数直接注入 Scoped 服务，而必须注入 IServiceProvider 或 IServiceScopeFactory，在每个定时批次周期内使用 using (var scope = _serviceProvider.CreateScope()) 手动创建作用域，防止内存泄露与单例生命周期混淆。", IsRelevant = true },
                    new() { Id = "doc4", Text = "ASP.NET Core 优雅停机（Graceful Shutdown）机制依赖 HostOptions.ShutdownTimeout 参数配置。容器编排平台（如 Kubernetes）发送 SIGTERM 信号后，主机依次执行每个 IHostedService.StopAsync，如果在规定时间内未退出则强制 SIGKILL 终止进程。", IsRelevant = true },
                    new() { Id = "doc5", Text = "ASP.NET Core 的依赖注入容器原生支持三种服务生命周期：Transient（瞬时，每次请求新建）、Scoped（作用域，单次 HTTP 请求共享）、Singleton（单例，进程全局唯一）。可通过 services.AddSingleton 等扩展方法声明。", IsRelevant = false },
                    new() { Id = "doc6", Text = "Quartz.NET 和 Hangfire 是 .NET 生态中常用的分布式任务调度中间件，支持 CRON 表达式、持久化作业存储、分布式集群锁以及 Web 控制台可视化管理。", IsRelevant = false },
                    new() { Id = "doc7", Text = "ASP.NET Core 中间件管道配置顺序极为关键：UseExceptionHandler 应当位于管道最外层，随后依次配置 UseRouting、UseCors、UseAuthentication、UseAuthorization 以及 UseEndpoints。", IsRelevant = false },
                    new() { Id = "doc8", Tag = "对抗硬负例", Text = "在 Windows 任务计划程序中配置批处理脚本定时启动控制台应用，通过 schtasks /create /sc daily /tn 'BackupTask' /tr 'backup.bat' /st 02:00 命令配置。", IsRelevant = false },
                    new() { Id = "doc9", Text = "SQL Server 的代理作业（SQL Server Agent）支持基于 T-SQL 语句或 SSIS 包的定时调度，可配置作业执行失败时通过数据库邮件向管理员告警。", IsRelevant = false },
                    new() { Id = "doc10", Text = "使用 Docker 构建 ASP.NET Core 微服务镜像时，推荐采用多阶段构建（Multi-Stage Builds）以减小最终生产镜像体积并提升构建缓存命中率。", IsRelevant = false }
                }
            },
            "enterprise-faq-retrieval" => new LongTextTestRequest
            {
                Query = "员工使用个人车辆因公出差的油费与过路费报销标准与审批规定",
                TopK = 5,
                Passages = new List<Passage>
                {
                    new() { Id = "doc1", Text = "《企业行政与财务制度》私车公用实施细则：经部门总监预先邮件报备并审批同意私车公用的，员工出差期间产生的真实过路过桥费凭增值税通行费发票实报实销；油耗补贴按照实际公用往返里程每公里 1.2 元定额计算补贴，出差前后须在行政协同系统上传仪表盘里程数照片。", IsRelevant = true },
                    new() { Id = "doc2", Text = "私车因公出差核销流程要求：员工需在 OA 提报‘私车公用差旅报销申请’，报销单必须挂接：1. 部门负责人审批通过的《出差事前审批单》；2. ETC通行费电子发票及行程明细；3. 出差起点与终点的电子地图导航路线测距截图；4. 车辆行驶证复印件（车主须为本人或直系亲属）。", IsRelevant = true },
                    new() { Id = "doc3", Text = "私车公用突发交通违章与意外责任划分：因公出差期间因个人驾驶违规产生的交通罚款、滞纳金及扣分由驾驶员本人自行承担，财务一律不予报销；如在出差途中发生交通事故，按员工个人商业车险条款理赔，企业报销免赔额以内部公车同等标准上限补贴。", IsRelevant = true },
                    new() { Id = "doc4", Text = "公司公车班车与商务接待车队管理规定：商务接待用车需提前24小时通过行政调度系统申请派车单，随车配备专职司机，司机加油统一使用企业中石化中石油充值油卡，不得私自使用现金加油。", IsRelevant = false },
                    new() { Id = "doc5", Text = "员工国内差旅住宿费与市内交通补助：出差期间未安排公务用车的，市内出租车及网约车发票据实报销，或可按每天 80 元定额标准申领市内交通包干综合补助。", IsRelevant = false },
                    new() { Id = "doc6", Text = "企业固定资产报废与处置规范：使用年限超过 5 年且维修成本高于购置原值 40% 的办公设备与公务机动车辆，由资产管理部门提出报废鉴定申请，经财务审计核销后公开竞价拍卖处理。", IsRelevant = false },
                    new() { Id = "doc7", Tag = "硬负例-非因公", Text = "员工每日上下班通勤产生的自驾油费、停车费、洗车费及月度交通补贴标准，属于日常薪酬福利范畴，按各职级标准直接计入每月工资卡发放，不得走差旅费用报销渠道。", IsRelevant = false },
                    new() { Id = "doc8", Text = "办公差旅餐饮发票合规指引：因公出差期间员工伙食补贴按每人每天 120 元包干发放，不得另行开具非业务接待性质的单张高额餐饮发票报销。", IsRelevant = false },
                    new() { Id = "doc9", Text = "年度优秀员工带薪休假旅游津贴：凡入职满三年的优秀员工每年可申请 3000 元家庭自驾旅游专项基金，可在年度休假结束凭过路费及门票冲抵报销。", IsRelevant = false },
                    new() { Id = "doc10", Text = "企业安全生产消防疏散演练方案：研发大楼地库停车场严禁占用消防通道，电动自行车充电桩区域配备感温感烟探测器与水喷淋自动灭火系统。", IsRelevant = false }
                }
            },
            "cross-lingual-retrieval" => new LongTextTestRequest
            {
                Query = "What is Retrieval-Augmented Generation and how does it prevent LLM hallucination",
                TopK = 5,
                Passages = new List<Passage>
                {
                    new() { Id = "doc1", Text = "Retrieval-Augmented Generation (RAG) is an architectural framework that enhances Large Language Models by fetching authoritative facts from external vector knowledge bases before prompt synthesis. Grounding responses in retrieved context drastically reduces hallucination and ensures factual fidelity.", IsRelevant = true },
                    new() { Id = "doc2", Text = "检索增强生成（RAG）通过将大语言模型与外部动态知识库相结合，在生成答案前先行检索与提问最相关的文档切片（Chunks），将其作为上下文提示（Context Prompt）注入模型，有效杜绝了大模型在专业垂直领域由于知识盲区产生的编造虚假信息（幻觉）问题。", IsRelevant = true },
                    new() { Id = "doc3", Text = "In advanced RAG pipelines, dense vector search is combined with reranking and query decomposition. Providing the generator LLM with verifiable source citations and domain-specific knowledge grounds the completion, thereby mitigating parametric hallucinations.", IsRelevant = true },
                    new() { Id = "doc4", Text = "大模型幻觉消除策略中，RAG 相比全量微调（Fine-Tuning）具有知识实时更新、零重复训练开销、回答溯源可查以及权限受控等多项决定性优势，是企业私有化知识库落地的工业界标准范式。", IsRelevant = true },
                    new() { Id = "doc5", Text = "Fine-Tuning adapts model weights directly using domain-specific dataset instruction tuning, optimizing the style and domain task behaviors but requiring substantial GPU compute resources.", IsRelevant = false },
                    new() { Id = "doc6", Text = "Transformer architecture relies on multi-head self-attention mechanisms allowing models to dynamically weight token interactions across the entire input sequence length.", IsRelevant = false },
                    new() { Id = "doc7", Tag = "硬负例-RLHF", Text = "Reinforcement Learning from Human Feedback (RLHF) aligns LLM generations with human moral values and preferences through PPO optimization without querying external databases.", IsRelevant = false },
                    new() { Id = "doc8", Text = "Prompt engineering techniques such as Chain-of-Thought (CoT) and Few-Shot prompting guide LLMs to perform step-by-step reasoning on complex logic and mathematical puzzles.", IsRelevant = false },
                    new() { Id = "doc9", Text = "PostgreSQL provides pgvector extension enabling cosine distance and L2 distance operators for dense vector similarity indexing.", IsRelevant = false },
                    new() { Id = "doc10", Text = "Quantum computing utilizes qubits governed by superposition and entanglement to execute specific computational tasks exponentially faster than classical computers.", IsRelevant = false }
                }
            },
            "version-temporal-retrieval" => new LongTextTestRequest
            {
                Query = "在 Vue 3 与现代前端工程中，官方推荐的全局状态管理与组件通信方案及 Composition API 最佳实践",
                TopK = 5,
                Passages = new List<Passage>
                {
                    new() { Id = "doc1", Tag = "官方推荐-Pinia", Text = "Vue 3 官方推荐全局状态管理库 Pinia：全面支持 TypeScript 类型推断、摒弃了冗余的 mutations、采用模块化扁平 store 设计，并无缝支持 SSR 服务端渲染与 Devtools 时间旅行调试。", IsRelevant = true },
                    new() { Id = "doc2", Tag = "官方规范-组合式API", Text = "Vue 3 组合式 API（Composition API）与 <script setup> 响应式开发规范：推荐使用 ref、reactive 与 computed 声明状态，通过自定义 Composable 函数封装可复用业务逻辑，取代脆弱的 mixins。", IsRelevant = true },
                    new() { Id = "doc3", Tag = "跨组件通信-Provide/Inject", Text = "Vue 3 深度嵌套组件通信规范：通过 provide 和 inject 配合 readonly 响应式对象实现祖孙跨层级单向数据流传递，避免 prop 逐级钻取透传（Prop Drilling）。", IsRelevant = true },
                    new() { Id = "doc4", Tag = "事件总线替代", Text = "在 Vue 3 中，$on、$off 和 $once 实例事件总线已被官方彻底移除；对于简单的跨组件解耦通信，推荐使用 mitt 或 tiny-emitter 轻量发布订阅第三方库代替。", IsRelevant = true },
                    new() { Id = "doc5", Tag = "现代化构建-Vite", Text = "Vue 3 前端工程现代化构建方案推荐采用 Vite，基于原生 ESM 模块实现毫秒级冷启动与极速热模块替换（HMR），相比传统 Webpack 大幅提升开发体验。", IsRelevant = false },
                    new() { Id = "doc6", Tag = "版本演进冲突-Vuex旧版", Text = "Vuex 3.x/4.x 集中式状态管理规范：所有状态变更必须严格通过同步 mutations 提交 commit，异步操作必须封装在 actions 中分发 dispatch，通过嵌套 modules 树状拆分状态。", IsRelevant = false },
                    new() { Id = "doc7", Tag = "版本废弃-Vue2选项式", Text = "Vue 2 选项式 API（Options API）架构设计：使用 data、methods、computed、watch 组织组件选项，多组件代码逻辑复用主要通过 mixins 混入实现。", IsRelevant = false },
                    new() { Id = "doc8", Tag = "版本废弃-EventBus旧方案", Text = "Vue 2 时代常用的全局 EventBus 方案：通过 new Vue() 实例作为全局事件中心，在组件 mounted 中绑定 this.$bus.$on，在 destroyed 中通过 $off 解绑事件。", IsRelevant = false },
                    new() { Id = "doc9", Tag = "不相关-React并发", Text = "React 19 核心并发特性与 Server Actions：通过 useTransition、useActionState 统一处理客户端服务端异步操作，搭配 Zustand 进行客户端原子状态管理。", IsRelevant = false },
                    new() { Id = "doc10", Tag = "不相关-容器部署", Text = "Nginx 官方 Docker 镜像作为静态 Web 前端部署底座：配置 gzip 压缩、缓存强协商控制及 try_files $uri $uri/ /index.html 单页面路由重写规则。", IsRelevant = false }
                }
            },
            "numerical-constraint-retrieval" => new LongTextTestRequest
            {
                Query = "选购要求：适用于工业物联网户外严苛环境的高精度温湿度传感器，工作温度必须覆盖 -40℃至85℃，供电电压兼容 12V 至 24V DC，且测量精度需达到 ±0.2℃ 以内",
                TopK = 5,
                Passages = new List<Passage>
                {
                    new() { Id = "doc1", Tag = "完美满足-严苛工业级", Text = "工业级数字式温湿度变送器 Modbus-RTU 型号 SHT-45X：工作温度范围为 -40℃~+85℃，宽电压供电范围 DC 10V~30V（完美适配 12V/24V 电源系统），温度测量精度高达 ±0.15℃，具备 IP67 防水防尘等级与过压反接保护，专为户外恶劣环境设计。", IsRelevant = true },
                    new() { Id = "doc2", Tag = "满足参数-高精度变送器", Text = "高可靠性工业温湿度探头 HMP-80：额定工作温度 -40℃ 到 100℃，供电输入支持 12V/24V 直流自适应，测温精度在全量程内优于 ±0.2℃，采用特氟龙防护滤芯抗化学气体腐蚀。", IsRelevant = true },
                    new() { Id = "doc3", Tag = "参数基本满足-宽温型", Text = "户外气象观测站专业温湿变送器：工作温度 -40℃~85℃，支持标准 24V DC 工业供电（低至 12V 正常启动），温度精度 ±0.2℃，标配铝合金防辐射百叶箱与 RS485 接口。", IsRelevant = true },
                    new() { Id = "doc4", Tag = "参数边际-精度稍欠", Text = "管路插入式工业温度变送器：工作环境温度 -40℃ 至 85℃，支持 12V~24V DC 供电，测温元件采用 A 级 PT100，整机系统精度为 ±0.35℃，配备隔爆铸铝接线盒。", IsRelevant = false },
                    new() { Id = "doc5", Tag = "参数边际-温宽收窄", Text = "仓储冷链专用数字温湿度记录仪：工作温度范围 -20℃ 至 70℃，供电支持 12V~24V 直流外接电源，测量精度达到 ±0.2℃，内置 10 万组数据存储与报警蜂鸣器。", IsRelevant = false },
                    new() { Id = "doc6", Tag = "硬约束越界-温宽不符", Text = "数据中心室内精密机房温湿度传感器：仅适用于商业室内环境，工作温度范围 0℃ 至 50℃（无法在 -40℃ 极寒启动），工作电压 12V/24V DC，测量精度 ±0.2℃。", IsRelevant = false },
                    new() { Id = "doc7", Tag = "硬约束越界-电压超限", Text = "高精度微功耗温湿度芯片模块：温度测量精度高达 ±0.1℃，工作温度 -40℃~85℃，但仅支持 3.3V 或 5V 弱电供电，严禁直接接入 12V 或 24V 工业供电以免瞬间击穿烧毁。", IsRelevant = false },
                    new() { Id = "doc8", Tag = "硬约束越界-精度不达标", Text = "户外大棚简易温湿度采集器：工作温度 -40℃~85℃，12V~24V 供电，但采用低成本热敏电阻，测量精度仅为 ±1.5℃，无法满足高精度工业严苛监控要求。", IsRelevant = false },
                    new() { Id = "doc9", Tag = "领域不符-红外热像仪", Text = "工业在线式长波红外热像仪：测温范围 -20℃~500℃，精度 ±2% 或 ±2℃，支持以太网 PoE 与 24V DC 供电，主要用于高压电气设备热缺陷巡检。", IsRelevant = false },
                    new() { Id = "doc10", Tag = "无关技术-高斯混合模型", Text = "高斯混合模型（GMM）在时间序列传感器异常检测中的数学建模：利用 EM 期望最大化算法对多维变量的概率分布进行拟合，识别偏离正态分布的离群点。", IsRelevant = false }
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
    public string? Tag { get; set; }
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
    public string? Tag { get; set; }
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

public class RetrievalPresetSuiteInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int PassageCount { get; set; }
    public int RelevantCount { get; set; }
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
        await _fileLock.WaitAsync();
        try
        {
            var file = Path.Combine(_dataPath, "records.json");
            if (File.Exists(file))
            {
                for (int retry = 0; retry < 5; retry++)
                {
                    try
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
                        break;
                    }
                    catch (IOException) when (retry < 4)
                    {
                        await Task.Delay(50 * (retry + 1));
                    }
                }
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static readonly SemaphoreSlim _fileLock = new(1, 1);

    private async Task SaveRecordsAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(_dataPath);
            var file = Path.Combine(_dataPath, "records.json");
            var json = System.Text.Json.JsonSerializer.Serialize(_records, _jsonOptions);
            for (int retry = 0; retry < 5; retry++)
            {
                try
                {
                    await File.WriteAllTextAsync(file, json);
                    break;
                }
                catch (IOException) when (retry < 4)
                {
                    await Task.Delay(50 * (retry + 1));
                }
            }
        }
        finally
        {
            _fileLock.Release();
        }
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
