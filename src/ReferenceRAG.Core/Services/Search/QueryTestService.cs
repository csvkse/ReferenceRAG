using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 查询测试结果
/// </summary>
public class QueryTestResult
{
    public string Query { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int TotalResults { get; set; }
    public int RelevantResults { get; set; }
    public float Precision { get; set; }
    public float Recall { get; set; }
    public float NDCG { get; set; }
    public float MRR { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public List<ChunkResult>? Results { get; set; }
}

/// <summary>
/// 查询测试套件
/// </summary>
public class QueryTestSuite
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<QueryTestCase> TestCases { get; set; } = new();
}

/// <summary>
/// 查询测试用例
/// </summary>
public class QueryTestCase
{
    public string Query { get; set; } = string.Empty;
    public List<string> ExpectedKeywords { get; set; } = new();
    public List<string>? ExpectedFiles { get; set; }
    public int? MinResults { get; set; }
    public float? MinPrecision { get; set; }
}

/// <summary>
/// 查询测试服务
/// </summary>
public class QueryTestService
{
    private readonly ISearchService _searchService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorStore _vectorStore;

    public QueryTestService(
        ISearchService searchService,
        IEmbeddingService embeddingService,
        IVectorStore vectorStore)
    {
        _searchService = searchService;
        _embeddingService = embeddingService;
        _vectorStore = vectorStore;
    }

    /// <summary>
    /// 运行完整测试套件
    /// </summary>
    public async Task<Dictionary<string, QueryTestResult>> RunTestSuiteAsync(
        QueryTestSuite suite,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, QueryTestResult>();

        foreach (var testCase in suite.TestCases)
        {
            var result = await RunTestCaseAsync(testCase, cancellationToken);
            results[testCase.Query] = result;
        }

        return results;
    }

    /// <summary>
    /// 运行单个测试用例
    /// </summary>
    public async Task<QueryTestResult> RunTestCaseAsync(
        QueryTestCase testCase,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var request = new AIQueryRequest
            {
                Query = testCase.Query,
                TopK = 20,
                Mode = QueryMode.Standard
            };

            var response = await _searchService.SearchAsync(request, cancellationToken);
            sw.Stop();

            var result = new QueryTestResult
            {
                Query = testCase.Query,
                Success = true,
                TotalResults = response.Chunks.Count,
                DurationMs = sw.ElapsedMilliseconds,
                Results = response.Chunks
            };

            // 计算指标
            CalculateMetrics(result, testCase);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new QueryTestResult
            {
                Query = testCase.Query,
                Success = false,
                DurationMs = sw.ElapsedMilliseconds,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// 计算评估指标
    /// </summary>
    private void CalculateMetrics(QueryTestResult result, QueryTestCase testCase)
    {
        if (result.Results == null || result.Results.Count == 0)
        {
            result.Precision = 0;
            result.Recall = 0;
            result.NDCG = 0;
            result.MRR = 0;
            return;
        }

        // 计算相关性
        var relevantCount = 0;
        var dcg = 0.0;
        var mrrPosition = 0;

        for (int i = 0; i < result.Results.Count; i++)
        {
            var chunk = result.Results[i];
            var isRelevant = IsRelevant(chunk, testCase);

            if (isRelevant)
            {
                relevantCount++;
                
                if (mrrPosition == 0)
                {
                    mrrPosition = i + 1;
                }

                // DCG 计算
                dcg += 1.0 / Math.Log2(i + 2);
            }
        }

        // Precision@K
        result.Precision = (float)relevantCount / result.Results.Count;

        // Recall（假设所有相关文档都在结果中）
        var totalRelevant = testCase.ExpectedKeywords.Count;
        result.Recall = totalRelevant > 0 ? (float)relevantCount / Math.Min(totalRelevant, result.Results.Count) : 0;

        // NDCG
        var idealDcg = 0.0;
        for (int i = 0; i < Math.Min(relevantCount, result.Results.Count); i++)
        {
            idealDcg += 1.0 / Math.Log2(i + 2);
        }
        result.NDCG = idealDcg > 0 ? (float)(dcg / idealDcg) : 0;

        // MRR
        result.MRR = mrrPosition > 0 ? 1.0f / mrrPosition : 0;

        result.RelevantResults = relevantCount;
    }

    /// <summary>
    /// 判断结果是否相关
    /// </summary>
    private bool IsRelevant(ChunkResult chunk, QueryTestCase testCase)
    {
        var content = chunk.Content.ToLowerInvariant();

        // 检查关键词
        foreach (var keyword in testCase.ExpectedKeywords)
        {
            if (content.Contains(keyword.ToLowerInvariant()))
            {
                return true;
            }
        }

        // 检查文件名
        if (testCase.ExpectedFiles != null)
        {
            foreach (var file in testCase.ExpectedFiles)
            {
                if (chunk.FilePath.Contains(file, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 生成测试报告
    /// </summary>
    public string GenerateReport(Dictionary<string, QueryTestResult> results)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# 查询测试报告");
        sb.AppendLine();
        sb.AppendLine($"测试时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"测试用例: {results.Count}");
        sb.AppendLine();

        // 统计
        var successCount = results.Values.Count(r => r.Success);
        var avgPrecision = results.Values.Where(r => r.Success).Average(r => r.Precision);
        var avgRecall = results.Values.Where(r => r.Success).Average(r => r.Recall);
        var avgNDCG = results.Values.Where(r => r.Success).Average(r => r.NDCG);
        var avgMRR = results.Values.Where(r => r.Success).Average(r => r.MRR);
        var avgDuration = results.Values.Where(r => r.Success).Average(r => r.DurationMs);

        sb.AppendLine("## 总体统计");
        sb.AppendLine();
        sb.AppendLine($"- 成功率: {successCount}/{results.Count} ({successCount * 100.0 / results.Count:F1}%)");
        sb.AppendLine($"- 平均精确率: {avgPrecision:F3}");
        sb.AppendLine($"- 平均召回率: {avgRecall:F3}");
        sb.AppendLine($"- 平均 NDCG: {avgNDCG:F3}");
        sb.AppendLine($"- 平均 MRR: {avgMRR:F3}");
        sb.AppendLine($"- 平均延迟: {avgDuration:F0}ms");
        sb.AppendLine();

        // 详细结果
        sb.AppendLine("## 详细结果");
        sb.AppendLine();
        sb.AppendLine("| 查询 | 成功 | 结果数 | 精确率 | 召回率 | NDCG | MRR | 延迟(ms) |");
        sb.AppendLine("|------|------|--------|--------|--------|------|-----|----------|");

        foreach (var (query, result) in results)
        {
            var queryShort = query.Length > 30 ? query[..30] + "..." : query;
            sb.AppendLine($"| {queryShort} | {(result.Success ? "✓" : "✗")} | {result.TotalResults} | {result.Precision:F3} | {result.Recall:F3} | {result.NDCG:F3} | {result.MRR:F3} | {result.DurationMs} |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 获取默认测试套件
    /// </summary>
    public static QueryTestSuite GetDefaultTestSuite()
    {
        return new QueryTestSuite
        {
            Name = "默认测试套件",
            Description = "基础语义搜索测试",
            TestCases = new List<QueryTestCase>
            {
                new()
                {
                    Query = "如何配置系统？",
                    ExpectedKeywords = new List<string> { "配置", "设置", "config" },
                    MinResults = 1
                },
                new()
                {
                    Query = "核心功能有哪些？",
                    ExpectedKeywords = new List<string> { "功能", "特性", "feature" },
                    MinResults = 1
                },
                new()
                {
                    Query = "如何使用向量搜索？",
                    ExpectedKeywords = new List<string> { "向量", "搜索", "vector", "search" },
                    MinResults = 1
                },
                new()
                {
                    Query = "系统架构是什么？",
                    ExpectedKeywords = new List<string> { "架构", "architecture", "结构" },
                    MinResults = 1
                },
                new()
                {
                    Query = "如何部署服务？",
                    ExpectedKeywords = new List<string> { "部署", "deploy", "docker" },
                    MinResults = 1
                }
            }
        };
    }

    /// <summary>
    /// 交互式查询测试
    /// </summary>
    public async Task RunInteractiveTestAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine("=== 交互式查询测试 ===");
        Console.WriteLine("输入查询内容，输入 'quit' 退出");
        Console.WriteLine();

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("查询> ");
            var query = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(query))
                continue;

            if (query.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                query.Equals("exit", StringComparison.OrdinalIgnoreCase))
                break;

            var testCase = new QueryTestCase
            {
                Query = query,
                MinResults = 1
            };

            var result = await RunTestCaseAsync(testCase, cancellationToken);

            Console.WriteLine();
            Console.WriteLine($"结果: {result.TotalResults} 条, 耗时: {result.DurationMs}ms");
            Console.WriteLine();

            if (result.Results != null)
            {
                for (int i = 0; i < Math.Min(5, result.Results.Count); i++)
                {
                    var chunk = result.Results[i];
                    Console.WriteLine($"[{i + 1}] {chunk.Title ?? Path.GetFileNameWithoutExtension(chunk.FilePath)}");
                    Console.WriteLine($"    分数: {chunk.Score:F4}");
                    Console.WriteLine($"    文件: {chunk.FilePath}");
                    Console.WriteLine($"    内容: {chunk.Content[..Math.Min(100, chunk.Content.Length)]}...");
                    Console.WriteLine($"    链接: {chunk.ObsidianLink}");
                    Console.WriteLine();
                }
            }
        }
    }
}
