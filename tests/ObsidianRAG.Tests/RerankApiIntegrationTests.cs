using Xunit;
using System.Net.Http.Json;
using System.Text.Json;

namespace ObsidianRAG.Tests;

/// <summary>
/// 自定义跳过异常
/// </summary>
public class SkipException : Exception
{
    public SkipException(string message) : base(message) { }
}

/// <summary>
/// 重排 API 集成测试
/// 需要启动后端服务: dotnet run --project src/ObsidianRAG.Service
/// </summary>
public class RerankApiIntegrationTests : IAsyncLifetime
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "http://localhost:5000";
    private bool _serviceAvailable;

    public RerankApiIntegrationTests()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task InitializeAsync()
    {
        // 检查服务是否可用
        try
        {
            var response = await _httpClient.GetAsync("/api/health");
            _serviceAvailable = response.IsSuccessStatusCode;
        }
        catch
        {
            _serviceAvailable = false;
        }

        if (!_serviceAvailable)
        {
            Console.WriteLine("⚠ 后端服务未启动，API 集成测试将被跳过");
            Console.WriteLine("  启动命令: dotnet run --project src/ObsidianRAG.Service");
        }
    }

    public Task DisposeAsync()
    {
        _httpClient.Dispose();
        return Task.CompletedTask;
    }

    private void SkipIfServiceNotAvailable()
    {
        if (!_serviceAvailable)
        {
            throw new SkipException("后端服务未启动，跳过测试");
        }
    }

    #region TC-OA-001: POST /v1/rerank 基本测试

    [Fact]
    public async Task TC_OA_001_Rerank_ReturnsValidResults()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-OA-001: POST /v1/rerank 基本测试 ===");

        var request = new
        {
            query = "机器学习是什么",
            documents = new[]
            {
                "机器学习是人工智能的一个分支，它使计算机能够从数据中学习",
                "今天天气很好，适合出去散步"
            },
            top_n = 2
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/rerank", request);
        var content = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"状态码: {response.StatusCode}");
        Console.WriteLine($"响应: {content}");

        Assert.True(response.IsSuccessStatusCode, $"请求失败: {content}");

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // 验证响应结构
        Assert.True(root.TryGetProperty("results", out var results));
        Assert.Equal(2, results.GetArrayLength());

        foreach (var result in results.EnumerateArray())
        {
            Assert.True(result.TryGetProperty("index", out _));
            Assert.True(result.TryGetProperty("relevance_score", out var score));
            var scoreValue = score.GetDouble();
            Assert.InRange(scoreValue, 0.0, 1.0);
        }

        // 验证 usage
        Assert.True(root.TryGetProperty("usage", out var usage));
        Assert.True(usage.TryGetProperty("prompt_tokens", out _));

        Console.WriteLine("✓ POST /v1/rerank 基本测试通过");
    }

    #endregion

    #region TC-OA-002: 参数验证 - 缺少 query

    [Fact]
    public async Task TC_OA_002_MissingQuery_ReturnsBadRequest()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-OA-002: 参数验证 - 缺少 query ===");

        var request = new
        {
            documents = new[] { "文档1" }
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/rerank", request);

        Console.WriteLine($"状态码: {response.StatusCode}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        Console.WriteLine("✓ 缺少 query 参数验证通过");
    }

    #endregion

    #region TC-OA-003: 参数验证 - 缺少 documents

    [Fact]
    public async Task TC_OA_003_MissingDocuments_ReturnsBadRequest()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-OA-003: 参数验证 - 缺少 documents ===");

        var request = new
        {
            query = "测试查询"
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/rerank", request);

        Console.WriteLine($"状态码: {response.StatusCode}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        Console.WriteLine("✓ 缺少 documents 参数验证通过");
    }

    #endregion

    #region TC-OA-004: return_documents=false

    [Fact]
    public async Task TC_OA_004_ReturnDocumentsFalse_DocumentIsNull()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-OA-004: return_documents=false ===");

        var request = new
        {
            query = "测试查询",
            documents = new[] { "文档1", "文档2" },
            return_documents = false
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/rerank", request);
        var content = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"状态码: {response.StatusCode}");

        Assert.True(response.IsSuccessStatusCode);

        using var doc = JsonDocument.Parse(content);
        var results = doc.RootElement.GetProperty("results");

        foreach (var result in results.EnumerateArray())
        {
            Assert.True(result.TryGetProperty("document", out var document));
            Assert.Equal(JsonValueKind.Null, document.ValueKind);
        }

        Console.WriteLine("✓ return_documents=false 测试通过");
    }

    #endregion

    #region TC-OA-005: 文档数量限制

    [Fact]
    public async Task TC_OA_005_ExceedMaxDocuments_ReturnsBadRequest()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-OA-005: 文档数量限制 ===");

        // 创建超过限制的文档数组 (256)
        var documents = Enumerable.Range(0, 300).Select(i => $"文档{i}").ToArray();

        var request = new
        {
            query = "测试查询",
            documents = documents
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/rerank", request);

        Console.WriteLine($"状态码: {response.StatusCode}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        Console.WriteLine("✓ 文档数量限制测试通过");
    }

    #endregion

    #region TC-OA-006: POST /v1/rerank/single

    [Fact]
    public async Task TC_OA_006_RerankSingle_ReturnsValidScore()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-OA-006: POST /v1/rerank/single ===");

        var request = new
        {
            query = "测试查询",
            document = "测试文档内容"
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/rerank/single", request);
        var content = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"状态码: {response.StatusCode}");
        Console.WriteLine($"响应: {content}");

        Assert.True(response.IsSuccessStatusCode);

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("relevance_score", out var score));
        var scoreValue = score.GetDouble();
        Assert.InRange(scoreValue, 0.0, 1.0);

        Console.WriteLine($"相关性分数: {scoreValue:F4}");
        Console.WriteLine("✓ POST /v1/rerank/single 测试通过");
    }

    #endregion

    #region TC-RT-001: GET /api/reranktest/presets

    [Fact]
    public async Task TC_RT_001_GetPresets_ReturnsPresetList()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-RT-001: GET /api/reranktest/presets ===");

        var response = await _httpClient.GetAsync("/api/reranktest/presets");
        var content = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"状态码: {response.StatusCode}");
        Console.WriteLine($"响应: {content}");

        Assert.True(response.IsSuccessStatusCode);

        using var doc = JsonDocument.Parse(content);
        var presets = doc.RootElement;

        Assert.True(presets.ValueKind == JsonValueKind.Array);
        Assert.True(presets.GetArrayLength() >= 4);

        foreach (var preset in presets.EnumerateArray())
        {
            Assert.True(preset.TryGetProperty("name", out _));
            Assert.True(preset.TryGetProperty("description", out _));
            Assert.True(preset.TryGetProperty("documentCount", out _));
        }

        Console.WriteLine($"预设数量: {presets.GetArrayLength()}");
        Console.WriteLine("✓ GET /api/reranktest/presets 测试通过");
    }

    #endregion

    #region TC-RT-002: POST /api/reranktest/preset/{suiteName}

    [Fact]
    public async Task TC_RT_002_RunPresetTest_ReturnsTestResult()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-RT-002: POST /api/reranktest/preset/chinese-rerank ===");

        var response = await _httpClient.PostAsync("/api/reranktest/preset/chinese-rerank", null);
        var content = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"状态码: {response.StatusCode}");
        Console.WriteLine($"响应: {content[..Math.Min(500, content.Length)]}...");

        Assert.True(response.IsSuccessStatusCode);

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // 验证基本字段
        Assert.True(root.TryGetProperty("testType", out var testType));
        Assert.Equal("rerank", testType.GetString());

        Assert.True(root.TryGetProperty("documents", out var documents));
        Assert.True(documents.GetArrayLength() > 0);

        // 验证评估指标存在
        Assert.True(root.TryGetProperty("ndcg", out _));
        Assert.True(root.TryGetProperty("mrr", out _));
        Assert.True(root.TryGetProperty("map", out _));

        Console.WriteLine("✓ POST /api/reranktest/preset/{suiteName} 测试通过");
    }

    #endregion

    #region TC-RT-003: POST /api/reranktest/test

    [Fact]
    public async Task TC_RT_003_CustomTest_ReturnsMetrics()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-RT-003: POST /api/reranktest/test ===");

        var request = new
        {
            query = "向量数据库应用",
            documents = new[]
            {
                new { id = "d1", text = "向量数据库是专门用于存储和检索高维向量的数据库系统", expectedRelevance = 0.9 },
                new { id = "d2", text = "今天天气很好，适合出去散步", expectedRelevance = 0.1 }
            }
        };

        var response = await _httpClient.PostAsJsonAsync("/api/reranktest/test", request);
        var content = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"状态码: {response.StatusCode}");
        Console.WriteLine($"响应: {content}");

        Assert.True(response.IsSuccessStatusCode);

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // 验证结果包含文档
        Assert.True(root.TryGetProperty("documents", out var documents));
        Assert.Equal(2, documents.GetArrayLength());

        // 验证评估指标
        if (root.TryGetProperty("ndcg", out var ndcg))
        {
            Console.WriteLine($"NDCG: {ndcg.GetDouble():F4}");
        }

        Console.WriteLine("✓ POST /api/reranktest/test 测试通过");
    }

    #endregion

    #region TC-RT-004: GET /api/reranktest/statistics

    [Fact]
    public async Task TC_RT_004_GetStatistics_ReturnsStats()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-RT-004: GET /api/reranktest/statistics ===");

        var response = await _httpClient.GetAsync("/api/reranktest/statistics");
        var content = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"状态码: {response.StatusCode}");
        Console.WriteLine($"响应: {content}");

        Assert.True(response.IsSuccessStatusCode);

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("totalTests", out _));

        Console.WriteLine("✓ GET /api/reranktest/statistics 测试通过");
    }

    #endregion

    #region TC-RT-005: 不存在的预设测试

    [Fact]
    public async Task TC_RT_005_InvalidPreset_ReturnsNotFound()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-RT-005: 不存在的预设测试 ===");

        var response = await _httpClient.PostAsync("/api/reranktest/preset/nonexistent-preset", null);

        Console.WriteLine($"状态码: {response.StatusCode}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);

        Console.WriteLine("✓ 不存在的预设测试通过");
    }

    #endregion

    #region TC-RT-006: 文档对象格式测试

    [Fact]
    public async Task TC_RT_006_DocumentObjectFormat_Works()
    {
        SkipIfServiceNotAvailable();
        Console.WriteLine("=== TC-RT-006: 文档对象格式测试 ===");

        // 测试文档作为对象而非字符串
        var request = new
        {
            query = "测试查询",
            documents = new[]
            {
                new { text = "这是第一个文档" },
                new { text = "这是第二个文档" }
            }
        };

        var response = await _httpClient.PostAsJsonAsync("/v1/rerank", request);
        var content = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"状态码: {response.StatusCode}");

        Assert.True(response.IsSuccessStatusCode, $"请求失败: {content}");

        Console.WriteLine("✓ 文档对象格式测试通过");
    }

    #endregion
}
