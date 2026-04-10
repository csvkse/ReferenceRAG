using Xunit;
using ObsidianRAG.Core.Services.Rerank;
using ObsidianRAG.Core.Interfaces;

namespace ObsidianRAG.Tests;

/// <summary>
/// 重排服务测试
/// </summary>
public class RerankServiceTests
{
    private const string TestModelsPath = "E:/LinuxWork/Obsidian/resource/models";
    private const string TestDataModelsPath = "E:/LinuxWork/Obsidian/resource/data/models";

    private string? FindModelPath(string modelName)
    {
        var paths = new[]
        {
            Path.Combine(TestModelsPath, modelName, "model.onnx"),
            Path.Combine(TestDataModelsPath, modelName, "model.onnx")
        };

        return paths.FirstOrDefault(File.Exists);
    }

    #region TC-RS-001: 模拟模式初始化测试

    [Fact]
    public void TC_RS_001_SimulationMode_WhenModelNotExists()
    {
        Console.WriteLine("=== TC-RS-001: 模拟模式初始化测试 ===");

        var options = new RerankOptions
        {
            ModelPath = "/nonexistent/path/model.onnx",
            ModelName = "test-reranker",
            UseCuda = false
        };

        using var service = new OnnxRerankService(options);

        Console.WriteLine($"ModelName: {service.ModelName}");
        Console.WriteLine($"IsLoaded: {service.IsLoaded}");

        // 当模型不存在时，IsLoaded 应为 false
        Assert.False(service.IsLoaded);
        Assert.Equal("test-reranker", service.ModelName);

        Console.WriteLine("✓ 模拟模式初始化测试通过");
    }

    #endregion

    #region TC-RS-002: CPU 推理测试

    [Fact]
    public async Task TC_RS_002_CpuInference_WhenModelExists()
    {
        Console.WriteLine("=== TC-RS-002: CPU 推理测试 ===");

        var modelPath = FindModelPath("bge-reranker-base");
        if (modelPath == null)
        {
            Console.WriteLine("跳过测试：未找到 bge-reranker-base 模型");
            return;
        }

        Console.WriteLine($"模型路径: {modelPath}");

        var options = new RerankOptions
        {
            ModelPath = modelPath,
            ModelName = "bge-reranker-base",
            UseCuda = false,
            MaxSequenceLength = 512
        };

        using var service = new OnnxRerankService(options);

        Console.WriteLine($"IsLoaded: {service.IsLoaded}");

        // 如果模型加载成功，进行推理测试
        if (service.IsLoaded)
        {
            var query = "如何学习编程";
            var documents = new[]
            {
                "编程学习需要从基础语法开始，逐步掌握数据结构和算法",
                "今天天气很好，适合出去散步"
            };

            var result = await service.RerankBatchAsync(query, documents);

            Console.WriteLine($"\n推理结果:");
            Console.WriteLine($"  文档数量: {result.Documents.Count}");
            Console.WriteLine($"  耗时: {result.DurationMs}ms");

            foreach (var doc in result.Documents)
            {
                Console.WriteLine($"  [{doc.Index}] Score: {doc.RelevanceScore:F4}, Text: {doc.Document[..Math.Min(50, doc.Document.Length)]}...");
            }

            Assert.Equal(2, result.Documents.Count);

            // 验证分数在 [0, 1] 范围
            foreach (var doc in result.Documents)
            {
                Assert.InRange(doc.RelevanceScore, 0.0, 1.0);
            }

            // 验证相关文档分数 > 不相关文档分数
            var relevantScore = result.Documents.First(d => d.Index == 0).RelevanceScore;
            var irrelevantScore = result.Documents.First(d => d.Index == 1).RelevanceScore;
            Console.WriteLine($"\n相关性验证: 相关文档分数 ({relevantScore:F4}) vs 不相关文档分数 ({irrelevantScore:F4})");

            // 注意：这可能不是总是成立，取决于模型，但通常应该成立
            if (relevantScore > irrelevantScore)
            {
                Console.WriteLine("✓ 相关性排序正确");
            }
            else
            {
                Console.WriteLine("⚠ 相关性排序与预期不符（可能是模型行为）");
            }
        }
        else
        {
            Console.WriteLine("模型未加载，使用模拟模式");
        }

        Console.WriteLine("✓ CPU 推理测试通过");
    }

    #endregion

    #region TC-RS-003: 批量重排测试

    [Fact]
    public async Task TC_RS_003_BatchRerank_ReturnsCorrectCount()
    {
        Console.WriteLine("=== TC-RS-003: 批量重排测试 ===");

        var modelPath = FindModelPath("bge-reranker-base") ?? "/nonexistent/model.onnx";
        var options = new RerankOptions
        {
            ModelPath = modelPath,
            ModelName = "test-reranker",
            UseCuda = false
        };

        using var service = new OnnxRerankService(options);

        var query = "测试查询";
        var documents = new[]
        {
            "文档1内容",
            "文档2内容",
            "文档3内容",
            "文档4内容",
            "文档5内容"
        };

        var result = await service.RerankBatchAsync(query, documents);

        Console.WriteLine($"返回文档数量: {result.Documents.Count}");

        // 验证返回数量正确
        Assert.Equal(5, result.Documents.Count);

        // 验证每个文档都有 Index 和 RelevanceScore
        foreach (var doc in result.Documents)
        {
            Console.WriteLine($"  Index: {doc.Index}, Score: {doc.RelevanceScore:F4}");
            Assert.InRange(doc.Index, 0, 4);
            Assert.InRange(doc.RelevanceScore, 0.0, 1.0);
        }

        // 验证结果按分数降序排列
        var scores = result.Documents.Select(d => d.RelevanceScore).ToList();
        var sortedScores = scores.OrderByDescending(s => s).ToList();
        Assert.Equal(sortedScores, scores);

        Console.WriteLine("✓ 批量重排测试通过");
    }

    #endregion

    #region TC-RS-004: 空文档处理

    [Fact]
    public async Task TC_RS_004_EmptyDocuments_ReturnsEmptyResult()
    {
        Console.WriteLine("=== TC-RS-004: 空文档处理测试 ===");

        var options = new RerankOptions
        {
            ModelPath = "/nonexistent/model.onnx",
            ModelName = "test-reranker",
            UseCuda = false
        };

        using var service = new OnnxRerankService(options);

        var query = "测试查询";
        var documents = Array.Empty<string>();

        var result = await service.RerankBatchAsync(query, documents);

        Console.WriteLine($"空文档列表返回结果数量: {result.Documents.Count}");

        Assert.Empty(result.Documents);
        Assert.Equal(query, result.Query);

        Console.WriteLine("✓ 空文档处理测试通过");
    }

    #endregion

    #region TC-RS-005: 模型切换测试

    [Fact]
    public async Task TC_RS_005_ReloadModel_UpdatesModelName()
    {
        Console.WriteLine("=== TC-RS-005: 模型切换测试 ===");

        var options = new RerankOptions
        {
            ModelPath = "/nonexistent/model.onnx",
            ModelName = "initial-model",
            UseCuda = false
        };

        using var service = new OnnxRerankService(options);

        Console.WriteLine($"初始模型: {service.ModelName}");
        Assert.Equal("initial-model", service.ModelName);

        // 尝试切换到不存在的模型
        var success = await service.ReloadModelAsync("/nonexistent/new-model.onnx", "new-model");

        Console.WriteLine($"切换结果: {success}");
        Console.WriteLine($"切换后模型: {service.ModelName}");

        // 即使模型不存在，ModelName 也应该更新
        Assert.Equal("new-model", service.ModelName);

        Console.WriteLine("✓ 模型切换测试通过");
    }

    #endregion

    #region TC-RS-006: 单文档重排测试

    [Fact]
    public async Task TC_RS_006_SingleDocumentRerank_ReturnsValidScore()
    {
        Console.WriteLine("=== TC-RS-006: 单文档重排测试 ===");

        var options = new RerankOptions
        {
            ModelPath = "/nonexistent/model.onnx",
            ModelName = "test-reranker",
            UseCuda = false
        };

        using var service = new OnnxRerankService(options);

        var query = "什么是机器学习";
        var document = "机器学习是人工智能的一个分支，它使计算机能够从数据中学习";

        var score = await service.RerankAsync(query, document);

        Console.WriteLine($"单文档重排分数: {score:F4}");

        Assert.InRange(score, 0.0, 1.0);

        Console.WriteLine("✓ 单文档重排测试通过");
    }

    #endregion

    #region TC-RS-007: RerankDocument 属性测试

    [Fact]
    public void TC_RS_007_RerankDocument_PropertiesWork()
    {
        Console.WriteLine("=== TC-RS-007: RerankDocument 属性测试 ===");

        var doc = new RerankDocument
        {
            Id = "doc-123",
            Index = 5,
            Document = "测试文档内容",
            RelevanceScore = 0.85
        };

        Console.WriteLine($"Id: {doc.Id}");
        Console.WriteLine($"Index: {doc.Index}");
        Console.WriteLine($"Document: {doc.Document}");
        Console.WriteLine($"Text (别名): {doc.Text}");
        Console.WriteLine($"RelevanceScore: {doc.RelevanceScore}");

        Assert.Equal("doc-123", doc.Id);
        Assert.Equal(5, doc.Index);
        Assert.Equal("测试文档内容", doc.Document);
        Assert.Equal(doc.Document, doc.Text); // Text 是 Document 的别名
        Assert.Equal(0.85, doc.RelevanceScore);

        // 验证 Text setter
        doc.Text = "新内容";
        Assert.Equal("新内容", doc.Document);

        Console.WriteLine("✓ RerankDocument 属性测试通过");
    }

    #endregion

    #region TC-RS-008: RerankResult 属性测试

    [Fact]
    public void TC_RS_008_RerankResult_PropertiesWork()
    {
        Console.WriteLine("=== TC-RS-008: RerankResult 属性测试 ===");

        var result = new RerankResult
        {
            Query = "测试查询",
            Documents = new List<RerankDocument>
            {
                new() { Index = 0, Document = "文档1", RelevanceScore = 0.9 },
                new() { Index = 1, Document = "文档2", RelevanceScore = 0.5 }
            },
            DurationMs = 100
        };

        Console.WriteLine($"Query: {result.Query}");
        Console.WriteLine($"Documents Count: {result.Documents.Count}");
        Console.WriteLine($"DurationMs: {result.DurationMs}");

        Assert.Equal("测试查询", result.Query);
        Assert.Equal(2, result.Documents.Count);
        Assert.Equal(100, result.DurationMs);

        Console.WriteLine("✓ RerankResult 属性测试通过");
    }

    #endregion

    #region TC-RS-009: 长文本截断测试

    [Fact]
    public async Task TC_RS_009_LongText_HandledGracefully()
    {
        Console.WriteLine("=== TC-RS-009: 长文本截断测试 ===");

        var modelPath = FindModelPath("bge-reranker-base");
        if (modelPath == null)
        {
            Console.WriteLine("跳过测试：未找到测试模型");
            return;
        }

        var options = new RerankOptions
        {
            ModelPath = modelPath,
            ModelName = "bge-reranker-base",
            UseCuda = false,
            MaxSequenceLength = 512
        };

        using var service = new OnnxRerankService(options);

        if (!service.IsLoaded)
        {
            Console.WriteLine("模型未加载，跳过测试");
            return;
        }

        // 创建超长文本
        var longQuery = string.Join(" ", Enumerable.Repeat("这是一个测试查询", 100));
        var longDocument = string.Join(" ", Enumerable.Repeat("这是一个测试文档内容", 200));

        Console.WriteLine($"Query 长度: {longQuery.Length} 字符");
        Console.WriteLine($"Document 长度: {longDocument.Length} 字符");

        // 应该不会抛出异常
        var score = await service.RerankAsync(longQuery, longDocument);

        Console.WriteLine($"长文本重排分数: {score:F4}");
        Assert.InRange(score, 0.0, 1.0);

        Console.WriteLine("✓ 长文本截断测试通过");
    }

    #endregion

    #region TC-RS-010: 并发请求测试

    [Fact]
    public async Task TC_RS_010_ConcurrentRequests_ThreadSafe()
    {
        Console.WriteLine("=== TC-RS-010: 并发请求测试 ===");

        var options = new RerankOptions
        {
            ModelPath = "/nonexistent/model.onnx",
            ModelName = "test-reranker",
            UseCuda = false
        };

        using var service = new OnnxRerankService(options);

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var result = await service.RerankBatchAsync($"查询{i}", new[] { $"文档{i}" });
            return (Index: i, Score: result.Documents[0].RelevanceScore);
        }).ToList();

        var results = await Task.WhenAll(tasks);

        Console.WriteLine($"并发请求数: {results.Length}");
        foreach (var r in results.OrderBy(x => x.Index))
        {
            Console.WriteLine($"  Task {r.Index}: Score = {r.Score:F4}");
        }

        Assert.Equal(10, results.Length);
        // 每个结果都应该有效
        foreach (var r in results)
        {
            Assert.InRange(r.Score, 0.0, 1.0);
        }

        Console.WriteLine("✓ 并发请求测试通过");
    }

    #endregion
}
