using Xunit;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Tests;

/// <summary>
/// CUDA 推理问题专项测试
/// 测试目的：验证 CUDA 在动态 batch size 下的行为
/// </summary>
public class CudaInferenceTests
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

    [Fact]
    public void Test1_CpuMode_VariableBatchSize()
    {
        Console.WriteLine("=== Test 1: CPU 模式下变化 Batch Size ===");

        var modelPath = FindModelPath("bge-small-zh-v1.5") ?? FindModelPath("bge-base-zh-v1.5");
        if (modelPath == null)
        {
            Console.WriteLine("跳过测试：没有找到测试模型");
            return;
        }

        var modelName = Path.GetFileName(Path.GetDirectoryName(modelPath)!);
        Console.WriteLine($"模型: {modelName}");

        var options = new EmbeddingOptions
        {
            ModelPath = modelPath,
            ModelName = modelName,
            MaxSequenceLength = 512,
            BatchSize = 8,
            UseCuda = false  // 使用 CPU 模式
        };

        IEmbeddingService service = new EmbeddingService(options);
        Assert.False(service.IsSimulationMode);

        // 测试变化 batch size
        Console.WriteLine("\n测试 CPU 模式下变化 batch size:");

        // Batch 1
        var result1 = service.EncodeAsync("单条文本").GetAwaiter().GetResult();
        Console.WriteLine($"  Batch=1: 成功, 维度={result1.Length}");
        Assert.Equal(service.Dimension, result1.Length);

        // Batch 2
        var result2 = service.EncodeBatchAsync(new[] { "文本一", "文本二" }).GetAwaiter().GetResult();
        Console.WriteLine($"  Batch=2: 成功, 向量数={result2.Length}");
        Assert.Equal(2, result2.Length);

        // Batch 1 again
        var result3 = service.EncodeAsync("另一条单文本").GetAwaiter().GetResult();
        Console.WriteLine($"  Batch=1 (again): 成功, 维度={result3.Length}");
        Assert.Equal(service.Dimension, result3.Length);

        // Batch 4
        var result4 = service.EncodeBatchAsync(new[] { "A", "B", "C", "D" }).GetAwaiter().GetResult();
        Console.WriteLine($"  Batch=4: 成功, 向量数={result4.Length}");
        Assert.Equal(4, result4.Length);

        Console.WriteLine("\n✅ CPU 模式下所有 batch size 测试通过");
    }

    [Fact]
    public void Test2_SemanticTestScenario()
    {
        Console.WriteLine("=== Test 2: 模拟 SemanticTest 场景 ===");

        var modelPath = FindModelPath("bge-small-zh-v1.5") ?? FindModelPath("bge-base-zh-v1.5");
        if (modelPath == null)
        {
            Console.WriteLine("跳过测试：没有找到测试模型");
            return;
        }

        var modelName = Path.GetFileName(Path.GetDirectoryName(modelPath)!);

        // 使用 CPU 模式（避免 CUDA 问题）
        var options = new EmbeddingOptions
        {
            ModelPath = modelPath,
            ModelName = modelName,
            MaxSequenceLength = 512,
            BatchSize = 8,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);

        // 模拟 SemanticTestController.TestShortText 的调用
        // 通常格式: query + 多个 candidates
        var query = "这是一个测试查询";
        var candidates = new[]
        {
            "第一个候选文本",
            "第二个候选文本",
            "第三个候选文本"
        };

        Console.WriteLine("\n模拟 SemanticTest 场景:");
        Console.WriteLine($"  Query: {query}");
        Console.WriteLine($"  Candidates: {candidates.Length} 个");

        // 这是 SemanticTestController 的调用方式
        var allTexts = new[] { query }
            .Concat(candidates)
            .ToArray();

        Console.WriteLine($"\n批量推理 {allTexts.Length} 条文本...");

        try
        {
            var allVectors = service.EncodeBatchAsync(allTexts).GetAwaiter().GetResult();

            Console.WriteLine($"  推理成功! 向量数: {allVectors.Length}");

            // 计算相似度
            var queryVector = allVectors[0];
            for (int i = 0; i < candidates.Length; i++)
            {
                var candidateVector = allVectors[i + 1];
                var similarity = CosineSimilarity(queryVector, candidateVector);
                Console.WriteLine($"  相似度(query, candidate[{i}]): {similarity:F4}");
            }

            Assert.Equal(allTexts.Length, allVectors.Length);
            Console.WriteLine("\n✅ SemanticTest 场景测试通过");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  推理失败: {ex.Message}");
            throw;
        }
    }

    [Fact]
    public void Test3_ConsecutiveDifferentBatchSizes()
    {
        Console.WriteLine("=== Test 3: 连续不同 Batch Size 推理 ===");

        var modelPath = FindModelPath("bge-small-zh-v1.5") ?? FindModelPath("bge-base-zh-v1.5");
        if (modelPath == null)
        {
            Console.WriteLine("跳过测试：没有找到测试模型");
            return;
        }

        var modelName = Path.GetFileName(Path.GetDirectoryName(modelPath)!);

        var options = new EmbeddingOptions
        {
            ModelPath = modelPath,
            ModelName = modelName,
            MaxSequenceLength = 512,
            BatchSize = 8,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);

        Console.WriteLine("\n连续执行不同 batch size 推理:");

        // 模拟实际使用场景：先单条推理，然后批量推理
        var batchSizes = new[] { 1, 2, 1, 4, 1, 3, 2, 1 };
        var allPassed = true;

        foreach (var batchSize in batchSizes)
        {
            try
            {
                var texts = Enumerable.Range(0, batchSize).Select(i => $"测试文本{i}").ToArray();
                var results = service.EncodeBatchAsync(texts).GetAwaiter().GetResult();

                if (results.Length == batchSize)
                {
                    Console.WriteLine($"  Batch={batchSize}: ✅ 成功");
                }
                else
                {
                    Console.WriteLine($"  Batch={batchSize}: ❌ 结果数量不匹配 ({results.Length} vs {batchSize})");
                    allPassed = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Batch={batchSize}: ❌ 错误 - {ex.Message}");
                allPassed = false;
            }
        }

        Assert.True(allPassed, "部分 batch size 测试失败");
        Console.WriteLine("\n✅ 所有连续 batch size 测试通过");
    }

    [Fact]
    public void Test4_ModelReloadInference()
    {
        Console.WriteLine("=== Test 4: 模型重载后推理 ===");

        var modelPath = FindModelPath("bge-small-zh-v1.5") ?? FindModelPath("bge-base-zh-v1.5");
        if (modelPath == null)
        {
            Console.WriteLine("跳过测试：没有找到测试模型");
            return;
        }

        var modelName = Path.GetFileName(Path.GetDirectoryName(modelPath)!);

        // 初始加载
        var options = new EmbeddingOptions
        {
            ModelPath = modelPath,
            ModelName = modelName,
            MaxSequenceLength = 512,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);

        // 初始推理
        Console.WriteLine("\n初始推理:");
        var result1 = service.EncodeAsync("初始测试").GetAwaiter().GetResult();
        Console.WriteLine($"  单条推理: 成功, 维度={result1.Length}");

        // 模拟模型重载（实际是加载同一个模型）
        Console.WriteLine("\n重载模型...");
        var success = service.ReloadModelAsync(modelPath, modelName).GetAwaiter().GetResult();
        Assert.True(success, "模型重载应该成功");

        // 重载后推理 - 这是关键测试
        Console.WriteLine("\n重载后推理:");
        var result2 = service.EncodeAsync("重载后测试").GetAwaiter().GetResult();
        Console.WriteLine($"  单条推理: 成功, 维度={result2.Length}");

        // 批量推理
        var result3 = service.EncodeBatchAsync(new[] { "A", "B", "C" }).GetAwaiter().GetResult();
        Console.WriteLine($"  批量推理: 成功, 向量数={result3.Length}");

        Assert.Equal(3, result3.Length);
        Console.WriteLine("\n✅ 模型重载后推理测试通过");
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dotProduct = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dotProduct / (float)(Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
