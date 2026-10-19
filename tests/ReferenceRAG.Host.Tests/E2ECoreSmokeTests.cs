using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using ReferenceRAG.ApiHost.Transport;
using ReferenceRAG.Core.Features.ModelManagement.StateMachine;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using System.Net;
using System.Net.Http;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Extensions;
using ReferenceRAG.Service.Middleware;
using ReferenceRAG.Service.Services;
using Xunit;
using Xunit.Abstractions;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 性能与耗时指标记录项
/// </summary>
public sealed class PerformanceMetricRecord
{
    public string Scenario { get; set; } = string.Empty;
    public string Metric { get; set; } = string.Empty;
    public double ValueMs { get; set; }
    public double ThresholdMs { get; set; }
    public bool Passed => ValueMs <= ThresholdMs;
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// 端对端测试全局性能指标跟踪与评估器
/// </summary>
public static class SmokePerformanceTracker
{
    private static readonly ConcurrentBag<PerformanceMetricRecord> _metrics = new();
    private static readonly long _startAllocatedBytes = GC.GetTotalAllocatedBytes(true);

    public static void Record(string scenario, string metric, double valueMs, double thresholdMs, string notes = "")
    {
        _metrics.Add(new PerformanceMetricRecord
        {
            Scenario = scenario,
            Metric = metric,
            ValueMs = Math.Round(valueMs, 2),
            ThresholdMs = thresholdMs,
            Notes = notes
        });
    }

    public static IReadOnlyList<PerformanceMetricRecord> GetRecords() =>
        _metrics.OrderBy(m => m.Scenario).ThenBy(m => m.Metric).ToList();

    public static string GenerateReport()
    {
        var records = GetRecords();
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("====================================================================================================");
        sb.AppendLine("                 ReferenceRAG E2E Core Smoke Test - Performance & SLA Report                       ");
        sb.AppendLine("====================================================================================================");
        sb.AppendLine(string.Format("{0,-28} | {1,-20} | {2,10} | {3,12} | {4,-6} | {5}",
            "Scenario", "Metric", "Value", "Threshold", "SLA", "Notes"));
        sb.AppendLine("----------------------------------------------------------------------------------------------------");

        int violations = 0;
        foreach (var r in records)
        {
            var slaStr = r.Passed ? "PASS" : "FAIL";
            if (!r.Passed) violations++;
            sb.AppendLine(string.Format("{0,-28} | {1,-20} | {2,8:F1} ms | {3,10:F1} ms | {4,-6} | {5}",
                r.Scenario, r.Metric, r.ValueMs, r.ThresholdMs, slaStr, r.Notes));
        }

        var memoryAllocatedMb = (GC.GetTotalAllocatedBytes(true) - _startAllocatedBytes) / (1024.0 * 1024.0);
        sb.AppendLine("----------------------------------------------------------------------------------------------------");
        sb.AppendLine(string.Format("Total Metrics: {0,-4} | SLA Passed: {1,-4} | Violations: {2,-4} | Memory Delta: {3:F2} MB",
            records.Count, records.Count - violations, violations, memoryAllocatedMb));
        sb.AppendLine("====================================================================================================");
        return sb.ToString();
    }

    public static void SaveReportToFile(string outputPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var reportText = GenerateReport();
            File.WriteAllText(outputPath, reportText, Encoding.UTF8);

            // 同时输出结构化 JSON 便于 CI/CD 趋势统计
            var jsonPath = Path.ChangeExtension(outputPath, ".json");
            var json = JsonSerializer.Serialize(new
            {
                Timestamp = DateTime.UtcNow,
                TotalMetrics = _metrics.Count,
                Violations = _metrics.Count(m => !m.Passed),
                Records = GetRecords()
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json, Encoding.UTF8);
        }
        catch { }
    }
}

/// <summary>
/// 端对端测试轻量级确定性 Embedding 服务
/// </summary>
public sealed class DeterministicEmbeddingService : IEmbeddingService
{
    private readonly int _dimension;

    public DeterministicEmbeddingService(int dimension = 384)
    {
        _dimension = dimension;
    }

    public string ModelName => "mock-embedding";
    public int Dimension => _dimension;
    public bool IsSimulationMode => false;
    public bool SupportsAsymmetricEncoding => false;

    public Task<bool> ReloadModelAsync(string modelPath, string modelName, int? maxSequenceLength = null) =>
        Task.FromResult(true);

    public void UnloadModel() { }

    public Task<float[]> EncodeAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(GenerateVector(text));

    public Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default) =>
        Task.FromResult(texts.Select(GenerateVector).ToArray());

    public Task<float[]> EncodeAsync(string text, EmbeddingMode mode, CancellationToken cancellationToken = default) =>
        EncodeAsync(text, cancellationToken);

    public Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, EmbeddingMode mode, CancellationToken cancellationToken = default) =>
        EncodeBatchAsync(texts, cancellationToken);

    public float[] Normalize(float[] vector)
    {
        double sumSq = 0;
        for (int i = 0; i < vector.Length; i++) sumSq += vector[i] * vector[i];
        var norm = (float)Math.Sqrt(sumSq);
        if (norm > 0)
        {
            for (int i = 0; i < vector.Length; i++) vector[i] /= norm;
        }
        return vector;
    }

    public float Similarity(float[] a, float[] b)
    {
        float dot = 0f;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) dot += a[i] * b[i];
        return dot;
    }

    private float[] GenerateVector(string text)
    {
        var vector = new float[_dimension];
        uint hash = 2166136261;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }
        var rng = new Random((int)hash);
        double sumSq = 0;
        for (int i = 0; i < _dimension; i++)
        {
            var v = (float)(rng.NextDouble() * 2.0 - 1.0);
            vector[i] = v;
            sumSq += v * v;
        }
        var norm = (float)Math.Sqrt(sumSq);
        if (norm > 0)
        {
            for (int i = 0; i < _dimension; i++) vector[i] /= norm;
        }
        return vector;
    }
}

/// <summary>
/// 端对端测试确定性 Rerank 精排服务
/// </summary>
public sealed class DeterministicRerankService : IRerankService
{
    public string ModelName => "mock-reranker";
    public bool IsLoaded => true;

    public Task<double> RerankAsync(string query, string document, CancellationToken cancellationToken = default)
    {
        var score = document.Contains(query, StringComparison.OrdinalIgnoreCase) ? 0.92 : 0.65;
        return Task.FromResult(score);
    }

    public Task<RerankResult> RerankBatchAsync(string query, IEnumerable<string> documents, CancellationToken cancellationToken = default)
    {
        var docList = documents.ToList();
        var results = new List<RerankDocument>();
        for (int i = 0; i < docList.Count; i++)
        {
            var doc = docList[i];
            var score = doc.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? Math.Clamp(0.95 - (i * 0.05), 0.50, 0.99)
                : Math.Clamp(0.40 - (i * 0.02), 0.10, 0.50);

            results.Add(new RerankDocument
            {
                Index = i,
                Document = doc,
                RelevanceScore = score
            });
        }

        results.Sort((a, b) => b.RelevanceScore.CompareTo(a.RelevanceScore));

        return Task.FromResult(new RerankResult
        {
            Query = query,
            Documents = results,
            DurationMs = 15
        });
    }

    public Task<bool> ReloadModelAsync(string modelPath, string modelName) => Task.FromResult(true);
    public void UnloadModel() { }
}

/// <summary>
/// 端对端测试确定性 Chat 上游模拟器，支持流式 SSE 与单次 JSON 响应
/// </summary>
public sealed class DeterministicSmokeChatHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isStream = false;
        string model = "gpt-4o-mini";
        if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            isStream = body.Contains("\"stream\":true");
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                {
                    model = modelEl.GetString() ?? "gpt-4o-mini";
                }
            }
            catch { }
        }

        if (isStream)
        {
            var sse = $"data: {{\"id\":\"chatcmpl-smoke-chunk-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"assistant\",\"content\":\"ReferenceRAG 核心检索设计支持密集向量与 BM25 混合搜索。\"}},\"finish_reason\":null}}]}}\n\n" +
                      $"data: {{\"id\":\"chatcmpl-smoke-chunk-2\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{}},\"finish_reason\":\"stop\"}}]}}\n\n" +
                      "data: [DONE]\n\n";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        }
        else
        {
            var json = $"{{\"id\":\"chatcmpl-smoke-resp\",\"object\":\"chat.completion\",\"created\":1700000000,\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":\"ReferenceRAG 核心检索架构基于 SQLite FTS5 与向量数据库混合检索。\"}},\"finish_reason\":\"stop\"}}],\"usage\":{{\"prompt_tokens\":28,\"completion_tokens\":14,\"total_tokens\":42}}}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}

/// <summary>
/// 端对端测试宿主生命周期夹具
/// </summary>
public sealed class E2ESmokeFixture : IAsyncLifetime
{
    public string TempDir { get; }
    public string VaultDir { get; }
    public string DataDir { get; }
    public string OriginalDir { get; }
    public WebApplication App { get; private set; } = null!;
    public InProcessServer Server => App.Services.GetRequiredService<InProcessServer>();
    public ConfigManager ConfigManager => App.Services.GetRequiredService<ConfigManager>();
    public ObsidianRagConfig Config { get; private set; } = null!;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public E2ESmokeFixture()
    {
        OriginalDir = Directory.GetCurrentDirectory();
        TempDir = Path.Combine(Path.GetTempPath(), $"rag-e2e-smoke-{Guid.NewGuid():N}");
        VaultDir = Path.Combine(TempDir, "vault");
        DataDir = Path.Combine(TempDir, "data");

        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(VaultDir);
        Directory.CreateDirectory(DataDir);
        Directory.SetCurrentDirectory(TempDir);
    }

    public async Task InitializeAsync()
    {
        // 1. 创建基准 Markdown 测试文档
        var doc1 = Path.Combine(VaultDir, "architecture.md");
        await File.WriteAllTextAsync(doc1, """
            # ReferenceRAG 系统架构
            本文档定义 ReferenceRAG 的核心架构规范与两阶段检索设计。
            ## 核心检索设计
            ReferenceRAG 实现了混合搜索能力，包含基于 SQLite FTS5 的 BM25 全文检索和密集向量检索。
            针对复杂知识库问答场景，通过两阶段精排（Rerank）进一步提升相关度。
            ## 架构设计规范
            架构采用清晰的六边形分层，领域模型与基础设施完全解耦。
            """);

        var doc2 = Path.Combine(VaultDir, "guide.md");
        await File.WriteAllTextAsync(doc2, """
            # ReferenceRAG 操作指南
            本手册指导用户如何配置与维护知识库系统。
            ## 数据源管理
            系统支持挂载多个本地文档目录，并实时监听文件修改。
            对于删除或更新的文档，流水线会自动清理孤立向量与倒排索引。
            """);

        // 2. 初始化持久化配置
        Config = new ObsidianRagConfig
        {
            DataPath = DataDir,
            Sources = new List<SourceFolder>
            {
                new() { Name = "MainVault", Path = VaultDir, Enabled = true }
            },
            Embedding = new EmbeddingConfig
            {
                ModelName = "mock-embedding"
            },
            Rerank = new RerankConfig
            {
                Enabled = true,
                ModelName = "mock-reranker",
                ScoreThreshold = 0.0f
            },
            Indexing = new IndexingConfig
            {
                SyncOnStartup = false
            }
        };

        var configManager = new ConfigManager(Config);
        configManager.Save(Config);

        // 3. 构建 ASP.NET Core 进程内宿主
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = TempDir,
            EnvironmentName = "Development"
        });

        builder.Logging.ClearProviders();
        builder.Services.AddInProcessServer(listen: false);
        builder.Services.AddSingleton<ReferenceRAG.Business.Features.App.Contracts.IPlatformActions, ReferenceRAG.Business.Features.App.Contracts.BrowserPlatformActions>();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(AIQueryController).Assembly)
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.PropertyNamingPolicy = null;
                o.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
                o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        builder.Configuration["Chat:Endpoint"] = "https://mock-openai.test/v1";
        builder.Configuration["Chat:ApiKey"] = "sk-smoke-test-key";
        builder.Configuration["Chat:Model"] = "gpt-4o-mini";
        builder.Configuration["Chat:SystemPrompt"] = "你是 ReferenceRAG 智能助手。";

        builder.Services.AddRagCoreServices(builder.Configuration);

        // 注入确定性且高效的 Embedding / Rerank 测试服务，实现全隔离真实运行
        builder.Services.RemoveAll<IEmbeddingService>();
        builder.Services.AddSingleton<IEmbeddingService>(new DeterministicEmbeddingService(384));
        builder.Services.RemoveAll<IRerankService>();
        builder.Services.AddSingleton<IRerankService>(new DeterministicRerankService());

        // 注入确定性 Chat 处理管道，隔离外部网络并支持流式与非流式真实冒烟验证
        var mockChatHandler = new DeterministicSmokeChatHandler();
        builder.Services.RemoveAll<MafChatService>();
        builder.Services.AddSingleton<MafChatService>(sp => new MafChatService(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<MafChatService>>(),
            timeProvider: null,
            httpMessageHandler: mockChatHandler));

        App = builder.Build();
        App.UseApiKeyAuthentication();
        App.UseRagEndpoints();

        await App.InitializeSearchAsync();
        await App.StartAsync();

        // 4. 执行初始文档全流程建索引并记录首批建库耗时
        var pipeline = App.Services.GetRequiredService<IFileIndexPipeline>();
        var initSw = Stopwatch.StartNew();
        var files = Directory.GetFiles(VaultDir, "*.md");
        foreach (var file in files)
        {
            await pipeline.IndexSingleAsync(file, Config.Sources);
        }
        initSw.Stop();
        SmokePerformanceTracker.Record("Baseline Initialization", "InitialBatchIndexing", initSw.Elapsed.TotalMilliseconds, 1000, $"{files.Length} docs indexed");
    }

    public async Task<(int StatusCode, string Body)> DispatchAsync(
        string method,
        string path,
        object? body = null,
        Dictionary<string, string>? headers = null)
    {
        headers ??= new Dictionary<string, string>();
        if (!headers.ContainsKey("Content-Type"))
            headers["Content-Type"] = "application/json";

        byte[] bodyBytes = body switch
        {
            null => Array.Empty<byte>(),
            string s => Encoding.UTF8.GetBytes(s),
            _ => JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions)
        };

        using var input = new MemoryStream(bodyBytes);
        using var output = new MemoryStream();
        var response = new HttpResponseFeature();

        await Server.DispatchAsync(method, path, headers, input, output, response, CancellationToken.None);
        var responseBody = Encoding.UTF8.GetString(output.ToArray());
        return (response.StatusCode, responseBody);
    }

    public async Task DisposeAsync()
    {
        if (App != null)
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }

        // 保存并打印端对端性能指标报告
        var repoRoot = FindRepoRoot();
        if (repoRoot != null)
        {
            var artifactPath = Path.Combine(repoRoot, "support", "artifacts", "smoke-perf-report.txt");
            SmokePerformanceTracker.SaveReportToFile(artifactPath);
        }

        Console.WriteLine(SmokePerformanceTracker.GenerateReport());

        try
        {
            if (Directory.Exists(OriginalDir))
                Directory.SetCurrentDirectory(OriginalDir);
            else
                Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        }
        catch { }

        try
        {
            if (Directory.Exists(TempDir))
                Directory.Delete(TempDir, recursive: true);
        }
        catch { }
    }

    private static string? FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")) ||
                File.Exists(Path.Combine(current.FullName, "smoke-test.bat")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }
}

/// <summary>
/// 端对端核心冒烟测试（E2E Core Smoke Tests）
/// 验证多模式检索、上下文展开、IPC通信、防卡退生命周期与数据源级联，并实施严格的耗时与性能 SLA 监控。
/// </summary>
[Collection("CurrentDirectoryCollection")]
public class E2ECoreSmokeTests : IClassFixture<E2ESmokeFixture>
{
    private readonly E2ESmokeFixture _fixture;
    private readonly ITestOutputHelper _output;

    public E2ECoreSmokeTests(E2ESmokeFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// 场景 1：检索与精排全链路冒烟，断言非 NaN、有限浮点数合法性、排序与响应耗时 SLA。
    /// </summary>
    [Fact]
    public async Task TC_SMOKE_01_Search_MultiMode_And_Score_Validity()
    {
        var modes = new[] { QueryMode.Quick, QueryMode.HybridRerank, QueryMode.Deep };

        foreach (var mode in modes)
        {
            var req = new AIQueryRequest
            {
                Query = "架构",
                Mode = mode,
                TopK = 5
            };

            var sw = Stopwatch.StartNew();
            var (status, body) = await _fixture.DispatchAsync("POST", "/api/ai/query", req);
            sw.Stop();

            Assert.Equal(200, status);
            Assert.DoesNotContain("NaN", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Infinity", body, StringComparison.OrdinalIgnoreCase);

            var res = JsonSerializer.Deserialize<AIQueryResponse>(body, E2ESmokeFixture.JsonOptions);
            Assert.NotNull(res);
            Assert.NotEmpty(res.Chunks);

            // 检查分块分数合法性
            foreach (var chunk in res.Chunks)
            {
                Assert.False(float.IsNaN(chunk.Score), $"Score 不能为 NaN: {chunk.Title}");
                Assert.False(float.IsInfinity(chunk.Score), $"Score 不能为 Infinity: {chunk.Title}");
                Assert.InRange(chunk.Score, 0.0f, 1.0f);

                if (chunk.RerankScore.HasValue)
                {
                    Assert.False(double.IsNaN(chunk.RerankScore.Value));
                    Assert.False(double.IsInfinity(chunk.RerankScore.Value));
                    Assert.InRange(chunk.RerankScore.Value, 0.0, 1.0);
                }
            }

            // 记录与断言各模式耗时指标 (SLA 阈值)
            var (slaClientThreshold, slaServerThreshold) = mode switch
            {
                QueryMode.Quick => (300.0, 200.0),
                QueryMode.HybridRerank => (500.0, 300.0),
                QueryMode.Deep => (500.0, 300.0),
                _ => (500.0, 300.0)
            };

            SmokePerformanceTracker.Record($"Search: {mode}", "ClientLatency", sw.Elapsed.TotalMilliseconds, slaClientThreshold, $"Chunks={res.Chunks.Count}");
            SmokePerformanceTracker.Record($"Search: {mode}", "ServerDuration", res.Stats.DurationMs, slaServerThreshold, $"Matches={res.Stats.TotalMatches}");

            Assert.True(sw.ElapsedMilliseconds <= slaClientThreshold,
                $"[{mode}] 客户端检索耗时 {sw.ElapsedMilliseconds}ms 超出 SLA 门限 {slaClientThreshold}ms");

            if (mode == QueryMode.HybridRerank)
            {
                Assert.True(res.RerankApplied);
                Assert.NotNull(res.RerankStats);
                Assert.False(double.IsNaN(res.RerankStats.AverageScore));
                Assert.InRange(res.RerankStats.AverageScore, 0.0, 1.0);

                SmokePerformanceTracker.Record("Search: HybridRerank", "RerankDuration", res.RerankStats.RerankDurationMs, 100.0, $"AvgScore={res.RerankStats.AverageScore:F2}");
                Assert.True(res.RerankStats.RerankDurationMs <= 100, $"重排耗时 {res.RerankStats.RerankDurationMs}ms 超出 100ms SLA");
            }
        }

        // 测试空匹配安全返回与快速熔断耗时
        var emptyReq = new AIQueryRequest
        {
            Query = "non_existent_token_999888777",
            Mode = QueryMode.Quick
        };

        var emptySw = Stopwatch.StartNew();
        var (emptyStatus, emptyBody) = await _fixture.DispatchAsync("POST", "/api/ai/query", emptyReq);
        emptySw.Stop();

        Assert.Equal(200, emptyStatus);
        Assert.DoesNotContain("NaN", emptyBody);
        var emptyRes = JsonSerializer.Deserialize<AIQueryResponse>(emptyBody, E2ESmokeFixture.JsonOptions);
        Assert.NotNull(emptyRes);

        SmokePerformanceTracker.Record("Search: EmptyUnmatched", "ClientLatency", emptySw.Elapsed.TotalMilliseconds, 200.0, "Zero match safe path");
        Assert.True(emptySw.ElapsedMilliseconds <= 200, $"未命中词搜索耗时 {emptySw.ElapsedMilliseconds}ms 超出 200ms 门限");
    }

    /// <summary>
    /// 场景 2：深入查询（DrillDown）上下文扩展链路冒烟与耗时指标监控。
    /// </summary>
    [Fact]
    public async Task TC_SMOKE_02_DrillDown_ContextExpansion()
    {
        // 1. 先进行一次基础搜索获取候选 RefId
        var (searchStatus, searchBody) = await _fixture.DispatchAsync("POST", "/api/ai/query", new AIQueryRequest
        {
            Query = "架构",
            Mode = QueryMode.Quick
        });
        Assert.Equal(200, searchStatus);
        var searchRes = JsonSerializer.Deserialize<AIQueryResponse>(searchBody, E2ESmokeFixture.JsonOptions)!;
        Assert.NotEmpty(searchRes.Chunks);

        var targetRefId = searchRes.Chunks[0].RefId;
        Assert.False(string.IsNullOrEmpty(targetRefId));

        // 2. 发起上下文扩展 DrillDown 请求并测速
        var drillReq = new DrillDownRequest
        {
            Query = "架构",
            RefIds = new List<string> { targetRefId },
            ExpandContext = 1
        };

        var sw = Stopwatch.StartNew();
        var (drillStatus, drillBody) = await _fixture.DispatchAsync("POST", "/api/ai/drill-down", drillReq);
        sw.Stop();

        Assert.Equal(200, drillStatus);
        Assert.DoesNotContain("NaN", drillBody);

        var drillRes = JsonSerializer.Deserialize<DrillDownResponse>(drillBody, E2ESmokeFixture.JsonOptions);
        Assert.NotNull(drillRes);
        Assert.NotEmpty(drillRes.ExpandedChunks);
        Assert.False(string.IsNullOrWhiteSpace(drillRes.FullContext));

        SmokePerformanceTracker.Record("DrillDown Context", "ClientLatency", sw.Elapsed.TotalMilliseconds, 200.0, $"ExpandedChunks={drillRes.ExpandedChunks.Count}");
        Assert.True(sw.ElapsedMilliseconds <= 200, $"DrillDown 耗时 {sw.ElapsedMilliseconds}ms 超出 200ms SLA");
    }

    /// <summary>
    /// 场景 3：系统状态探针与配置探针冒烟及响应耗时指标。
    /// </summary>
    [Fact]
    public async Task TC_SMOKE_03_SearchStatus_Probe()
    {
        var statusSw = Stopwatch.StartNew();
        var (status, body) = await _fixture.DispatchAsync("GET", "/api/ai/status");
        statusSw.Stop();

        Assert.Equal(200, status);
        var res = JsonSerializer.Deserialize<SearchStatusResponse>(body, E2ESmokeFixture.JsonOptions);
        Assert.NotNull(res);
        Assert.Equal("mock-embedding", res.EmbeddingModel);
        Assert.Equal("mock-reranker", res.RerankModel);
        Assert.True(res.Bm25HasIndex, "BM25 索引应已建立");
        Assert.True(res.VectorHasIndex, "向量索引应已建立");
        Assert.True(res.TotalFiles >= 2, "文件数应至少为 2");

        SmokePerformanceTracker.Record("System Status Probe", "StatusLatency", statusSw.Elapsed.TotalMilliseconds, 200.0, $"TotalFiles={res.TotalFiles}");
        Assert.True(statusSw.ElapsedMilliseconds <= 200, $"Status 端点耗时 {statusSw.ElapsedMilliseconds}ms 超出 200ms SLA");

        // 验证系统配置端点
        var settingsSw = Stopwatch.StartNew();
        var (settingsStatus, settingsBody) = await _fixture.DispatchAsync("GET", "/api/settings");
        settingsSw.Stop();

        Assert.Equal(200, settingsStatus);
        Assert.Contains("MainVault", settingsBody);

        SmokePerformanceTracker.Record("System Settings Probe", "SettingsLatency", settingsSw.Elapsed.TotalMilliseconds, 200.0, "Config payload");
        Assert.True(settingsSw.ElapsedMilliseconds <= 200, $"Settings 端点耗时 {settingsSw.ElapsedMilliseconds}ms 超出 200ms SLA");
    }

    /// <summary>
    /// 场景 4：Chromium IPC 消息通道、分发、请求取消冒烟与往返延迟 SLA 监控。
    /// </summary>
    [Fact]
    public async Task TC_SMOKE_04_IpcDispatcher_EndToEnd_MessageExchange()
    {
        var messages = new ConcurrentQueue<string>();
        await using var ipc = new IpcDispatcher(
            _fixture.Server,
            msg => messages.Enqueue(msg),
            _fixture.App.Services.GetRequiredService<ILogger<IpcDispatcher>>());

        var reqId = "smoke-ipc-test-01";
        var queryPayload = new AIQueryRequest { Query = "架构", Mode = QueryMode.Quick };

        var reqEnvelope = JsonSerializer.Serialize(new
        {
            id = reqId,
            type = "request",
            method = "POST",
            path = "/api/ai/query",
            headers = new Dictionary<string, string> { { "Content-Type", "application/json" } },
            body = JsonSerializer.Serialize(queryPayload, E2ESmokeFixture.JsonOptions)
        });

        var sw = Stopwatch.StartNew();
        await ipc.ReceiveAsync(reqEnvelope);

        // 等待 IPC 异步响应
        string? responseJson = null;
        while (sw.ElapsedMilliseconds < 5000)
        {
            if (messages.TryDequeue(out var msg))
            {
                responseJson = msg;
                break;
            }
            await Task.Delay(10);
        }
        sw.Stop();

        Assert.NotNull(responseJson);
        using var doc = JsonDocument.Parse(responseJson);
        Assert.Equal(reqId, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("response", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(200, doc.RootElement.GetProperty("status").GetInt32());

        var respBody = doc.RootElement.GetProperty("body").GetString();
        Assert.NotNull(respBody);
        Assert.Contains("架构", respBody);

        SmokePerformanceTracker.Record("Chromium IPC Dispatch", "RoundTripLatency", sw.Elapsed.TotalMilliseconds, 200.0, "InProcess IPC loop");
        Assert.True(sw.ElapsedMilliseconds <= 200, $"IPC 往返耗时 {sw.ElapsedMilliseconds}ms 超出 200ms SLA");

        // 验证取消请求安全处理
        await ipc.ReceiveAsync(JsonSerializer.Serialize(new { id = "smoke-cancel-id", type = "cancel" }));

        // 验证超大报文被安全拒绝而不崩溃
        var hugeBody = new string('x', 9 * 1024 * 1024);
        var oversizedEnvelope = JsonSerializer.Serialize(new
        {
            id = "oversized-req",
            type = "request",
            method = "POST",
            path = "/api/ai/query",
            body = hugeBody
        });
        await ipc.ReceiveAsync(oversizedEnvelope);
    }

    /// <summary>
    /// 场景 5：模型生命周期并发推理防卡退冒烟（验证在途租约存在时终止卸载，杜绝闪退与超时阻塞）。
    /// </summary>
    [Fact]
    public async Task TC_SMOKE_05_ModelLifecycle_ConcurrentInference_GuardsAgainstCrash()
    {
        var mockDisposable = new Mock<IDisposable>();
        var machine = new ModelLifecycleStateMachine<IDisposable>(
            "SmokeConcurrencyModel",
            memoryManager: null,
            idleTimeout: TimeSpan.FromMilliseconds(150));

        await using (machine)
        {
            var loaded = await machine.TwoPhaseReloadAsync(() => Task.FromResult(mockDisposable.Object));
            Assert.True(loaded);
            Assert.Equal(ModelState.Ready, machine.State);

            // 获取推理租约（模拟长耗时推理在途）
            var leaseSw = Stopwatch.StartNew();
            var lease = machine.AcquireLease();
            Assert.Equal(ModelState.Inferring, machine.State);
            Assert.Equal(1, machine.ActiveLeases);

            // 经过 200ms（超过 150ms 超时），租约未归还，看门狗应补偿延后，严禁卸载
            await Task.Delay(200);
            Assert.Equal(ModelState.Inferring, machine.State);
            Assert.Equal(1, machine.ActiveLeases);
            mockDisposable.Verify(m => m.Dispose(), Times.Never);

            // 归还租约
            lease.Dispose();
            leaseSw.Stop();
            Assert.Equal(0, machine.ActiveLeases);
            Assert.Equal(ModelState.Ready, machine.State);

            SmokePerformanceTracker.Record("Model Lifecycle Guard", "ActiveLeaseHoldTime", leaseSw.Elapsed.TotalMilliseconds, 500.0, "Watchdog guarded inference");

            // 归还后再等待超过空闲超时时间，平稳卸载并释放资源
            await Task.Delay(250);
            Assert.Equal(ModelState.Unloaded, machine.State);
            mockDisposable.Verify(m => m.Dispose(), Times.Once);
        }
    }

    /// <summary>
    /// 场景 6：数据源挂载、动态切片与级联清理冒烟及索引耗时 SLA。
    /// </summary>
    [Fact]
    public async Task TC_SMOKE_06_SourceManagement_And_CascadeCleanup()
    {
        // 1. 获取现有源
        var (listStatus, listBody) = await _fixture.DispatchAsync("GET", "/api/sources");
        Assert.Equal(200, listStatus);
        Assert.Contains("MainVault", listBody);

        // 2. 创建临时独立源文件夹与测试文档
        var extraSourceDir = Path.Combine(_fixture.TempDir, "extra_source");
        Directory.CreateDirectory(extraSourceDir);
        var extraFile = Path.Combine(extraSourceDir, "extra_doc.md");
        await File.WriteAllTextAsync(extraFile, "# Extra Document\nThis is a temporary document for cascade cleanup test.");

        var addPayload = new
        {
            Path = extraSourceDir,
            Name = "ExtraSource",
            Type = 0
        };
        var (addStatus, addBody) = await _fixture.DispatchAsync("POST", "/api/sources", addPayload);
        Assert.True(addStatus is 201 or 200, $"Add source failed with status {addStatus}: {addBody}");

        // 3. 动态触发索引生成并监控耗时指标
        var pipeline = _fixture.App.Services.GetRequiredService<IFileIndexPipeline>();
        var configManager = _fixture.App.Services.GetRequiredService<ConfigManager>();
        var updatedSources = configManager.Load().Sources;

        var indexSw = Stopwatch.StartNew();
        await pipeline.IndexSingleAsync(extraFile, updatedSources);
        indexSw.Stop();

        SmokePerformanceTracker.Record("Indexing Pipeline", "SingleDocIndexing", indexSw.Elapsed.TotalMilliseconds, 500.0, "Chunking + Vector + BM25");
        Assert.True(indexSw.ElapsedMilliseconds <= 500, $"单文档动态索引耗时 {indexSw.ElapsedMilliseconds}ms 超出 500ms 门限");

        // 验证索引已写入
        var vectorStore = _fixture.App.Services.GetRequiredService<IVectorStore>();
        var stats = await vectorStore.GetSourceStatsAsync();
        Assert.Contains(stats, s => s.Source == "ExtraSource" && s.FileCount > 0);

        // 4. 级联删除该源及其所有切片并监控清理耗时
        var delSw = Stopwatch.StartNew();
        var (delStatus, delBody) = await _fixture.DispatchAsync("DELETE", "/api/sources/ExtraSource?deleteData=true");
        delSw.Stop();

        Assert.True(delStatus is 204 or 200, $"Delete source failed with status {delStatus}: {delBody}");
        SmokePerformanceTracker.Record("Source Management", "CascadeCleanup", delSw.Elapsed.TotalMilliseconds, 300.0, "Delete chunks + FTS5 entries");
        Assert.True(delSw.ElapsedMilliseconds <= 300, $"数据源级联清理耗时 {delSw.ElapsedMilliseconds}ms 超出 300ms 门限");

        // 5. 验证清理干净无外键残留
        var finalStats = await vectorStore.GetSourceStatsAsync();
        Assert.DoesNotContain(finalStats, s => s.Source == "ExtraSource");
        var finalConfig = configManager.Load();
        Assert.DoesNotContain(finalConfig.Sources, s => s.Name == "ExtraSource");
    }

    /// <summary>
    /// 场景 7：OpenAI 兼容 REST 接口全链路真实冒烟测试
    /// 覆盖统一模型发现 (/v1/models)、向量嵌入计算 (/v1/embeddings)、两阶段重排评分 (/v1/rerank)、
    /// 直连与 RAG 知识增强聊天补全 (/v1/chat/completions 非流式与流式 SSE)，以及输入异常与 SLA 门限。
    /// </summary>
    [Fact]
    public async Task TC_SMOKE_07_OpenAICompatibility_EndToEnd_FullPipeline()
    {
        // 1. 统一模型列表发现 (GET /v1/models & GET /v1/models/{modelId})
        var modelsSw = Stopwatch.StartNew();
        var (modelsStatus, modelsBody) = await _fixture.DispatchAsync("GET", "/v1/models");
        modelsSw.Stop();

        Assert.Equal(200, modelsStatus);
        SmokePerformanceTracker.Record("OpenAI Compat: Models", "ListModelsLatency", modelsSw.Elapsed.TotalMilliseconds, 100.0, "Active models list");
        Assert.True(modelsSw.ElapsedMilliseconds <= 100, $"获取模型列表耗时 {modelsSw.ElapsedMilliseconds}ms 超出 100ms 门限");

        using (var modelsDoc = JsonDocument.Parse(modelsBody))
        {
            var root = modelsDoc.RootElement;
            Assert.Equal("list", root.GetProperty("object").GetString());
            var data = root.GetProperty("data").EnumerateArray().ToList();
            Assert.True(data.Count >= 4, $"模型数量应至少包含 4 个激活项，实际为 {data.Count}");

            var modelIds = data.Select(m => m.GetProperty("id").GetString()).ToList();
            Assert.Contains("reference-rag", modelIds);
            Assert.Contains("gpt-4o-mini", modelIds);
            Assert.Contains("mock-embedding", modelIds);
            Assert.Contains("mock-reranker", modelIds);

            // 验证规范遵循度：owned_by 均为统一组织标识
            foreach (var item in data)
            {
                Assert.Equal("reference-rag", item.GetProperty("owned_by").GetString());
                Assert.Equal("model", item.GetProperty("object").GetString());
                Assert.True(item.GetProperty("created").GetInt64() > 0);
            }
        }

        // 单模型元数据查询
        var (singleModelStatus, singleModelBody) = await _fixture.DispatchAsync("GET", "/v1/models/reference-rag");
        Assert.Equal(200, singleModelStatus);
        using (var singleDoc = JsonDocument.Parse(singleModelBody))
        {
            Assert.Equal("reference-rag", singleDoc.RootElement.GetProperty("id").GetString());
            Assert.Equal("model", singleDoc.RootElement.GetProperty("object").GetString());
            Assert.Equal("reference-rag", singleDoc.RootElement.GetProperty("owned_by").GetString());
        }

        // 2. 向量嵌入计算 (POST /v1/embeddings)
        var embSw = Stopwatch.StartNew();
        var embReq = new
        {
            input = new[] { "ReferenceRAG 混合检索架构", "两阶段重排精排能力" },
            model = "mock-embedding"
        };
        var (embStatus, embBody) = await _fixture.DispatchAsync("POST", "/v1/embeddings", embReq);
        embSw.Stop();

        Assert.Equal(200, embStatus);
        SmokePerformanceTracker.Record("OpenAI Compat: Embeddings", "BatchEmbeddingsLatency", embSw.Elapsed.TotalMilliseconds, 200.0, "2 texts batch encoded");
        Assert.True(embSw.ElapsedMilliseconds <= 200, $"向量计算耗时 {embSw.ElapsedMilliseconds}ms 超出 200ms 门限");

        using (var embDoc = JsonDocument.Parse(embBody))
        {
            var root = embDoc.RootElement;
            Assert.Equal("list", root.GetProperty("object").GetString());
            Assert.Equal("mock-embedding", root.GetProperty("model").GetString());

            var items = root.GetProperty("data").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            foreach (var item in items)
            {
                Assert.Equal("embedding", item.GetProperty("object").GetString());
                var vec = item.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                Assert.Equal(384, vec.Length);
            }

            var usage = root.GetProperty("usage");
            Assert.True(usage.GetProperty("total_tokens").GetInt32() > 0);
        }

        // 3. 重排精排评分 (POST /v1/rerank)
        var rerankSw = Stopwatch.StartNew();
        var rerankReq = new
        {
            query = "系统架构",
            documents = new[] { "无关说明文本", "ReferenceRAG 系统架构核心规范", "普通配置文档" },
            top_n = 2,
            return_documents = true
        };
        var (rrStatus, rrBody) = await _fixture.DispatchAsync("POST", "/v1/rerank", rerankReq);
        rerankSw.Stop();

        Assert.Equal(200, rrStatus);
        SmokePerformanceTracker.Record("OpenAI Compat: Rerank", "BatchRerankLatency", rerankSw.Elapsed.TotalMilliseconds, 150.0, "3 docs top_n=2 rerank");
        Assert.True(rerankSw.ElapsedMilliseconds <= 150, $"重排评分耗时 {rerankSw.ElapsedMilliseconds}ms 超出 150ms 门限");

        using (var rrDoc = JsonDocument.Parse(rrBody))
        {
            var root = rrDoc.RootElement;
            Assert.Equal("list", root.GetProperty("object").GetString());
            var results = root.GetProperty("results").EnumerateArray().ToList();
            Assert.Equal(2, results.Count);

            var first = results[0];
            var second = results[1];
            Assert.True(first.GetProperty("relevance_score").GetDouble() >= second.GetProperty("relevance_score").GetDouble());
            Assert.Equal("ReferenceRAG 系统架构核心规范", first.GetProperty("document").GetString());
        }

        // 4. 直连对话非流式补全 (Mode A: Direct LLM 透传 POST /v1/chat/completions)
        var directSw = Stopwatch.StartNew();
        var directChatReq = new
        {
            model = "gpt-4o-mini",
            stream = false,
            messages = new[]
            {
                new { role = "user", content = "你好，请确认服务正常。" }
            }
        };
        var (directStatus, directBody) = await _fixture.DispatchAsync("POST", "/v1/chat/completions", directChatReq);
        directSw.Stop();

        Assert.Equal(200, directStatus);
        SmokePerformanceTracker.Record("OpenAI Compat: Chat", "DirectCompletionLatency", directSw.Elapsed.TotalMilliseconds, 300.0, "Direct LLM non-streaming");
        Assert.True(directSw.ElapsedMilliseconds <= 300, $"直连对话补全耗时 {directSw.ElapsedMilliseconds}ms 超出 300ms 门限");

        using (var directDoc = JsonDocument.Parse(directBody))
        {
            var root = directDoc.RootElement;
            Assert.Equal("chat.completion", root.GetProperty("object").GetString());
            Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());

            var choices = root.GetProperty("choices").EnumerateArray().ToList();
            Assert.Single(choices);
            Assert.Equal("assistant", choices[0].GetProperty("message").GetProperty("role").GetString());
            Assert.False(string.IsNullOrWhiteSpace(choices[0].GetProperty("message").GetProperty("content").GetString()));

            var usage = root.GetProperty("usage");
            Assert.True(usage.GetProperty("prompt_tokens").GetInt32() > 0);
            Assert.True(usage.GetProperty("completion_tokens").GetInt32() > 0);
            Assert.Equal(
                usage.GetProperty("prompt_tokens").GetInt32() + usage.GetProperty("completion_tokens").GetInt32(),
                usage.GetProperty("total_tokens").GetInt32());
        }

        // 5. 知识库增强非流式补全 (Mode B: RAG 增强生成 model="reference-rag")
        // 本请求真实触发 SQLite FTS5 + 向量检索 + 提示词注入 + 上游补全全管道
        var ragSw = Stopwatch.StartNew();
        var ragChatReq = new
        {
            model = "reference-rag",
            stream = false,
            messages = new[]
            {
                new { role = "user", content = "ReferenceRAG 的核心架构和检索设计是什么？" }
            }
        };
        var (ragStatus, ragBody) = await _fixture.DispatchAsync("POST", "/v1/chat/completions", ragChatReq);
        ragSw.Stop();

        Assert.Equal(200, ragStatus);
        SmokePerformanceTracker.Record("OpenAI Compat: Chat", "RagCompletionLatency", ragSw.Elapsed.TotalMilliseconds, 500.0, "RAG Hybrid context injection + completion");
        Assert.True(ragSw.ElapsedMilliseconds <= 500, $"RAG 对话补全耗时 {ragSw.ElapsedMilliseconds}ms 超出 500ms 门限");

        using (var ragDoc = JsonDocument.Parse(ragBody))
        {
            var root = ragDoc.RootElement;
            Assert.Equal("chat.completion", root.GetProperty("object").GetString());
            Assert.Equal("reference-rag", root.GetProperty("model").GetString());

            var choices = root.GetProperty("choices").EnumerateArray().ToList();
            Assert.Single(choices);
            Assert.Equal("assistant", choices[0].GetProperty("message").GetProperty("role").GetString());
            Assert.False(string.IsNullOrWhiteSpace(choices[0].GetProperty("message").GetProperty("content").GetString()));
        }

        // 6. 流式 Chat 补全测试 (SSE 协议 stream=true, model="reference-rag")
        var streamSw = Stopwatch.StartNew();
        var streamReq = new
        {
            model = "reference-rag",
            stream = true,
            messages = new[]
            {
                new { role = "user", content = "流式输出核心架构总结" }
            }
        };
        var (streamStatus, streamBody) = await _fixture.DispatchAsync("POST", "/v1/chat/completions", streamReq);
        streamSw.Stop();

        Assert.Equal(200, streamStatus);
        SmokePerformanceTracker.Record("OpenAI Compat: Chat", "RagStreamLatency", streamSw.Elapsed.TotalMilliseconds, 500.0, "RAG SSE streaming chunks + [DONE]");
        Assert.True(streamSw.ElapsedMilliseconds <= 500, $"RAG 流式补全耗时 {streamSw.ElapsedMilliseconds}ms 超出 500ms 门限");

        Assert.Contains("data: ", streamBody);
        Assert.Contains("chat.completion.chunk", streamBody);
        Assert.Contains("data: [DONE]", streamBody);

        // 7. 边界异常与标准 OpenAI 错误协议校验
        var errReq = new { messages = Array.Empty<object>() };
        var (errStatus, errBody) = await _fixture.DispatchAsync("POST", "/v1/chat/completions", errReq);
        Assert.Equal(400, errStatus);
        using (var errDoc = JsonDocument.Parse(errBody))
        {
            var error = errDoc.RootElement.GetProperty("error");
            Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
            Assert.Contains("cannot be empty", error.GetProperty("message").GetString());
        }

        var (embErrStatus, _) = await _fixture.DispatchAsync("POST", "/v1/embeddings", new { model = "mock-embedding" });
        Assert.Equal(400, embErrStatus);

        var (rrErrStatus, _) = await _fixture.DispatchAsync("POST", "/v1/rerank", new { query = "test" });
        Assert.Equal(400, rrErrStatus);
    }
}
