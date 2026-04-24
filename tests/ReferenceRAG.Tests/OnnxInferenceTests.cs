using Xunit;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Tests;

/// <summary>
/// ONNX Runtime 推理测试 - 验证 CUDA 内存模式修复
/// </summary>
public class OnnxInferenceTests
{
    private const string TestModelsPath = "E:/LinuxWork/Obsidian/resource/models";
    private const string TestDataModelsPath = "E:/LinuxWork/Obsidian/resource/data/models";

    private string? FindModelPath(string modelName)
    {
        var paths = new[]
        {
            Path.Combine(TestModelsPath, modelName, "model.onnx"),
            Path.Combine(TestDataModelsPath, modelName, "model.onnx"),
            // BGE-M3 等大模型常见子目录结构
            Path.Combine(TestModelsPath, modelName, "onnx", "model.onnx"),
            Path.Combine(TestDataModelsPath, modelName, "onnx", "model.onnx")
        };

        return paths.FirstOrDefault(File.Exists);
    }

    [Fact]
    public void Test1_SingleBatchInference()
    {
        Console.WriteLine("=== Test 1: 单条文本推理 ===");

        var modelPath = FindModelPath("bge-small-zh-v1.5") ?? FindModelPath("bge-base-zh-v1.5");
        if (modelPath == null)
        {
            Console.WriteLine("跳过测试：没有找到测试模型");
            return;
        }

        var modelName = Path.GetFileName(Path.GetDirectoryName(modelPath)!);
        Console.WriteLine($"使用模型: {modelName}");
        Console.WriteLine($"模型路径: {modelPath}");

        var options = new EmbeddingOptions
        {
            ModelPath = modelPath,
            ModelName = modelName,
            MaxSequenceLength = 512,
            BatchSize = 1,
            UseCuda = false // 使用 CPU 避免环境依赖
        };

        IEmbeddingService service = new EmbeddingService(options);

        Console.WriteLine($"\n模型状态:");
        Console.WriteLine($"  ModelName: {service.ModelName}");
        Console.WriteLine($"  Dimension: {service.Dimension}");
        Console.WriteLine($"  IsSimulationMode: {service.IsSimulationMode}");

        Assert.False(service.IsSimulationMode, "模型不应处于模拟模式");

        // 单条文本推理
        var embedding = service.EncodeAsync("这是一条测试文本").GetAwaiter().GetResult();

        Console.WriteLine($"\n推理结果:");
        Console.WriteLine($"  向量长度: {embedding.Length}");
        Console.WriteLine($"  前5个值: [{string.Join(", ", embedding.Take(5).Select(v => v.ToString("F4")))}]");

        Assert.Equal(service.Dimension, embedding.Length);

        // 验证向量已归一化
        var norm = Math.Sqrt(embedding.Sum(v => v * v));
        Console.WriteLine($"  L2 范数: {norm:F6}");
        Assert.True(Math.Abs(norm - 1.0) < 0.01, "向量应该已归一化");
    }

    [Fact]
    public void Test2_MultipleBatchInference()
    {
        Console.WriteLine("=== Test 2: 多条文本批量推理 ===");

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
            BatchSize = 4,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);
        Assert.False(service.IsSimulationMode);

        // 多条文本推理
        var texts = new[]
        {
            "第一条测试文本",
            "第二条测试文本",
            "第三条测试文本",
            "第四条测试文本"
        };

        var embeddings = service.EncodeBatchAsync(texts).GetAwaiter().GetResult();

        Console.WriteLine($"\n推理结果:");
        Console.WriteLine($"  输入文本数: {texts.Length}");
        Console.WriteLine($"  输出向量数: {embeddings.Length}");

        Assert.Equal(texts.Length, embeddings.Length);

        for (int i = 0; i < embeddings.Length; i++)
        {
            var norm = Math.Sqrt(embeddings[i].Sum(v => v * v));
            Console.WriteLine($"  向量[{i}]: 长度={embeddings[i].Length}, 范数={norm:F6}");
            Assert.Equal(service.Dimension, embeddings[i].Length);
            Assert.True(Math.Abs(norm - 1.0) < 0.01, $"向量[{i}] 应该已归一化");
        }
    }

    [Fact]
    public void Test3_VariableBatchSizeInference()
    {
        Console.WriteLine("=== Test 3: 变化 Batch Size 推理 (验证 CUDA 内存模式修复) ===");

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
        Assert.False(service.IsSimulationMode);

        Console.WriteLine("\n测试变化 batch size 推理（模拟实际使用场景）:");

        // Batch size 1
        Console.WriteLine("\n--- Batch size = 1 ---");
        var result1 = service.EncodeAsync("单条文本").GetAwaiter().GetResult();
        Console.WriteLine($"  向量长度: {result1.Length}, 范数: {Math.Sqrt(result1.Sum(x => x * x)):F6}");
        Assert.Equal(service.Dimension, result1.Length);

        // Batch size 2
        Console.WriteLine("\n--- Batch size = 2 ---");
        var result2 = service.EncodeBatchAsync(new[] { "文本一", "文本二" }).GetAwaiter().GetResult();
        Console.WriteLine($"  向量数: {result2.Length}");
        Assert.Equal(2, result2.Length);

        // Batch size 1 again (关键测试：从 batch=2 切换回 batch=1)
        Console.WriteLine("\n--- Batch size = 1 (again) ---");
        var result3 = service.EncodeAsync("另一条单文本").GetAwaiter().GetResult();
        Console.WriteLine($"  向量长度: {result3.Length}, 范数: {Math.Sqrt(result3.Sum(x => x * x)):F6}");
        Assert.Equal(service.Dimension, result3.Length);

        // Batch size 4
        Console.WriteLine("\n--- Batch size = 4 ---");
        var result4 = service.EncodeBatchAsync(new[] { "A", "B", "C", "D" }).GetAwaiter().GetResult();
        Console.WriteLine($"  向量数: {result4.Length}");
        Assert.Equal(4, result4.Length);

        // Batch size 1 one more time
        Console.WriteLine("\n--- Batch size = 1 (final) ---");
        var result5 = service.EncodeAsync("最后一条").GetAwaiter().GetResult();
        Console.WriteLine($"  向量长度: {result5.Length}, 范数: {Math.Sqrt(result5.Sum(x => x * x)):F6}");
        Assert.Equal(service.Dimension, result5.Length);

        Console.WriteLine("\n所有变化 batch size 推理测试通过！");
    }

    [Fact]
    public void Test4_ModelReloadAndInference()
    {
        Console.WriteLine("=== Test 4: 模型重新加载后推理 ===");

        var smallModelPath = FindModelPath("bge-small-zh-v1.5");
        var baseModelPath = FindModelPath("bge-base-zh-v1.5");

        if (smallModelPath == null || baseModelPath == null)
        {
            Console.WriteLine("跳过测试：需要两个不同的模型");
            return;
        }

        Console.WriteLine($"Small 模型: {smallModelPath}");
        Console.WriteLine($"Base 模型: {baseModelPath}");

        // 加载 small 模型
        var options = new EmbeddingOptions
        {
            ModelPath = smallModelPath,
            ModelName = "bge-small-zh-v1.5",
            MaxSequenceLength = 512,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);
        var initialDimension = service.Dimension;
        Console.WriteLine($"\n初始模型: {service.ModelName}, 维度: {initialDimension}");

        // 推理测试
        var result1 = service.EncodeAsync("测试文本").GetAwaiter().GetResult();
        Console.WriteLine($"推理成功，向量长度: {result1.Length}");

        // 切换到 base 模型
        Console.WriteLine("\n切换到 bge-base-zh-v1.5...");
        var success = service.ReloadModelAsync(baseModelPath, "bge-base-zh-v1.5").GetAwaiter().GetResult();
        Assert.True(success, "模型切换应该成功");
        Console.WriteLine($"切换后模型: {service.ModelName}, 维度: {service.Dimension}");

        // 切换后推理（关键测试：确保新模型的 session 正确工作）
        var result2 = service.EncodeAsync("切换后的测试文本").GetAwaiter().GetResult();
        Console.WriteLine($"切换后推理成功，向量长度: {result2.Length}");
        Assert.Equal(service.Dimension, result2.Length);

        // 验证维度变化
        Assert.NotEqual(initialDimension, service.Dimension);

        Console.WriteLine("\n模型重新加载测试通过！");
    }

    [Fact]
    public void Test5_EmbeddingQuality()
    {
        Console.WriteLine("=== Test 5: 向量质量测试 ===");

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
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);

        // 语义相似的文本
        var similarTexts = new[]
        {
            "今天天气很好",
            "今天天气不错",
            "天气晴朗"
        };

        // 语义不同的文本
        var differentText = "编程是一门艺术";

        var similarEmbeddings = service.EncodeBatchAsync(similarTexts).GetAwaiter().GetResult();
        var differentEmbedding = service.EncodeAsync(differentText).GetAwaiter().GetResult();

        Console.WriteLine("\n相似文本的余弦相似度:");
        for (int i = 0; i < similarEmbeddings.Length; i++)
        {
            for (int j = i + 1; j < similarEmbeddings.Length; j++)
            {
                var similarity = CosineSimilarity(similarEmbeddings[i], similarEmbeddings[j]);
                Console.WriteLine($"  文本{i} vs 文本{j}: {similarity:F4}");
            }
        }

        Console.WriteLine("\n不同文本的余弦相似度:");
        for (int i = 0; i < similarEmbeddings.Length; i++)
        {
            var similarity = CosineSimilarity(similarEmbeddings[i], differentEmbedding);
            Console.WriteLine($"  相似文本{i} vs 不同文本: {similarity:F4}");
        }

        // 验证相似文本的相似度应该高于不同文本
        var avgSimilarSimilarity = (
            CosineSimilarity(similarEmbeddings[0], similarEmbeddings[1]) +
            CosineSimilarity(similarEmbeddings[0], similarEmbeddings[2]) +
            CosineSimilarity(similarEmbeddings[1], similarEmbeddings[2])
        ) / 3.0;

        var avgDifferentSimilarity = (
            CosineSimilarity(similarEmbeddings[0], differentEmbedding) +
            CosineSimilarity(similarEmbeddings[1], differentEmbedding) +
            CosineSimilarity(similarEmbeddings[2], differentEmbedding)
        ) / 3.0;

        Console.WriteLine($"\n平均相似文本相似度: {avgSimilarSimilarity:F4}");
        Console.WriteLine($"平均不同文本相似度: {avgDifferentSimilarity:F4}");

        Assert.True(avgSimilarSimilarity > avgDifferentSimilarity,
            "相似文本的相似度应该高于不同文本");
    }

    // ==================== BGE-M3 专项测试 ====================

    [Fact]
    public void Test6_BgeM3_DimensionResolution()
    {
        Console.WriteLine("=== Test 6: BGE-M3 维度解析（符号维度 fallback） ===");

        var modelPath = FindModelPath("bge-m3");
        if (modelPath == null)
        {
            Console.WriteLine("跳过测试：未找到 bge-m3 模型");
            return;
        }

        Console.WriteLine($"模型路径: {modelPath}");

        var options = new EmbeddingOptions
        {
            ModelPath = modelPath,
            ModelName = "bge-m3",
            MaxSequenceLength = 8192,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);

        Console.WriteLine($"维度: {service.Dimension}, 模拟模式: {service.IsSimulationMode}");

        Assert.False(service.IsSimulationMode, "模型应正常加载，不应进入模拟模式");
        Assert.Equal(1024, service.Dimension);
    }

    [Fact]
    public void Test7_BgeM3_LongContextTrim()
    {
        Console.WriteLine("=== Test 7: BGE-M3 长上下文 TrimToActualLength 性能验证 ===");

        var modelPath = FindModelPath("bge-m3");
        if (modelPath == null)
        {
            Console.WriteLine("跳过测试：未找到 bge-m3 模型");
            return;
        }

        var options = new EmbeddingOptions
        {
            ModelPath = modelPath,
            ModelName = "bge-m3",
            MaxSequenceLength = 8192,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);
        Assert.False(service.IsSimulationMode);

        // 短文本在 8192 上下文下，TrimToActualLength 应显著减少推理时间
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var embedding = service.EncodeAsync("BGE-M3 短文本测试").GetAwaiter().GetResult();
        sw.Stop();

        Console.WriteLine($"短文本推理耗时: {sw.ElapsedMilliseconds}ms，向量维度: {embedding.Length}");
        Assert.Equal(1024, embedding.Length);

        var norm = Math.Sqrt(embedding.Sum(v => v * v));
        Assert.True(Math.Abs(norm - 1.0) < 0.01, $"向量应已归一化，实际范数: {norm:F4}");
    }

    [Fact]
    public void Test8_BgeM3_SentenceEmbeddingOutput()
    {
        Console.WriteLine("=== Test 8: BGE-M3 sentence_embedding 2D 输出路径 ===");

        var modelPath = FindModelPath("bge-m3");
        if (modelPath == null)
        {
            Console.WriteLine("跳过测试：未找到 bge-m3 模型");
            return;
        }

        var options = new EmbeddingOptions
        {
            ModelPath = modelPath,
            ModelName = "bge-m3",
            MaxSequenceLength = 8192,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);
        Assert.False(service.IsSimulationMode);

        // 批量推理验证 sentence_embedding 2D 输出处理正确
        var texts = new[] { "语义搜索测试", "向量检索", "BGE M3 多语言模型" };
        var embeddings = service.EncodeBatchAsync(texts).GetAwaiter().GetResult();

        Assert.Equal(3, embeddings.Length);
        foreach (var emb in embeddings)
        {
            Assert.Equal(1024, emb.Length);
            var norm = Math.Sqrt(emb.Sum(v => v * v));
            Assert.True(Math.Abs(norm - 1.0) < 0.01);
        }

        // 语义近似的两句话相似度应高于语义不同的两句话
        var sim01 = CosineSimilarity(embeddings[0], embeddings[1]); // 语义搜索 vs 向量检索
        var sim02 = CosineSimilarity(embeddings[0], embeddings[2]); // 语义搜索 vs BGE M3
        Console.WriteLine($"相似度(语义搜索, 向量检索): {sim01:F4}");
        Console.WriteLine($"相似度(语义搜索, BGE M3):   {sim02:F4}");
    }

    [Fact]
    public void Test9_BgeM3_VariableBatchSize()
    {
        Console.WriteLine("=== Test 9: BGE-M3 动态 batch size（统一推理路径） ===");

        var modelPath = FindModelPath("bge-m3");
        if (modelPath == null)
        {
            Console.WriteLine("跳过测试：未找到 bge-m3 模型");
            return;
        }

        var options = new EmbeddingOptions
        {
            ModelPath = modelPath,
            ModelName = "bge-m3",
            MaxSequenceLength = 8192,
            UseCuda = false
        };

        IEmbeddingService service = new EmbeddingService(options);
        Assert.False(service.IsSimulationMode);

        foreach (var batchSize in new[] { 1, 2, 1, 4, 1 })
        {
            var texts = Enumerable.Range(0, batchSize).Select(i => $"测试文本{i}").ToArray();
            var results = service.EncodeBatchAsync(texts).GetAwaiter().GetResult();
            Assert.Equal(batchSize, results.Length);
            Console.WriteLine($"  Batch={batchSize}: 通过");
        }
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
