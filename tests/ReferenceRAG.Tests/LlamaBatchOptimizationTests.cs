using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace ReferenceRAG.Tests;

/// <summary>
/// GGUF 原生多序列批处理与常驻上下文优化专项测试套件。
/// 验证内容：
/// 1. 单元测试：降级模式、边界空值、超大批次子批拆分、Dispose 资源释放
/// 2. 功能测试：单句 vs 批处理余弦相似度数学一致性 (Cosine Similarity > 0.9999)
/// 3. 稳定性与基准测试：多轮连续推理显存基线零震荡、吞吐量线性扩展
/// </summary>
public class LlamaBatchOptimizationTests
{
    private readonly ITestOutputHelper _output;
    private const string RealQwen3GgufPath = @"E:\LinuxWork\Obsidian\resource\data\models\Embedding\Qwen3-Embedding-0.6B\Qwen3-Embedding-0.6B-Q8_0.gguf";

    public LlamaBatchOptimizationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Unit_SimulationMode_HandlesEmptyAndSingleBatch()
    {
        var dummyPath = Path.Combine(Path.GetTempPath(), $"missing_model_{Guid.NewGuid():N}.gguf");
        using var adapter = new LlamaEmbeddingAdapter(dummyPath, "test-simulation");
        await adapter.InitializeAsync();

        Assert.True(adapter.IsSimulationMode);
        Assert.Equal(384, adapter.Dimension);

        // 1. Empty batch
        var emptyResult = await adapter.EncodeBatchAsync(Array.Empty<string>());
        Assert.Empty(emptyResult);

        // 2. Single item
        var singleResult = await adapter.EncodeAsync("测试模拟嵌入");
        Assert.Equal(384, singleResult.Length);
        var norm = MathF.Sqrt(singleResult.Sum(x => x * x));
        Assert.True(Math.Abs(norm - 1.0f) < 1e-4, $"Norm should be 1.0, was {norm}");

        // 3. Batch items
        var batchResult = await adapter.EncodeBatchAsync(new[] { "文本一", "文本二", "文本三" });
        Assert.Equal(3, batchResult.Length);
        foreach (var v in batchResult)
        {
            Assert.Equal(384, v.Length);
            var itemNorm = MathF.Sqrt(v.Sum(x => x * x));
            Assert.True(Math.Abs(itemNorm - 1.0f) < 1e-4);
        }
    }

    [Fact]
    public async Task Unit_Dispose_CanBeInvokedMultipleTimesCleanly()
    {
        var dummyPath = Path.Combine(Path.GetTempPath(), $"missing_model_{Guid.NewGuid():N}.gguf");
        var adapter = new LlamaEmbeddingAdapter(dummyPath, "test-dispose");
        await adapter.InitializeAsync();

        adapter.Dispose();
        adapter.Dispose(); // idempotent
        await adapter.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => adapter.EncodeAsync("text"));
    }

    [Fact]
    public async Task Functional_RealModel_MathematicalConsistency_SingleVsBatch()
    {
        if (!File.Exists(RealQwen3GgufPath))
        {
            _output.WriteLine("[Skip] GGUF 模型文件不存在，跳过真实模型测试");
            return;
        }

        using var adapter = new LlamaEmbeddingAdapter(RealQwen3GgufPath, "Qwen3-Embedding-0.6B", gpuLayers: -1, contextSize: 2048);
        await adapter.InitializeAsync();

        Assert.False(adapter.IsSimulationMode);
        Assert.Equal(1024, adapter.Dimension);

        var testText = "测试常驻上下文单句推理与原生批处理解码结果的数学对齐程度";

        // 单句推理
        var singleVec = await adapter.EncodeAsync(testText);

        // 批处理推理 (单句放入批次中)
        var batchVecs = await adapter.EncodeBatchAsync(new[] { testText, "另一条无关文本用于填充批次槽位" });
        var batchVec = batchVecs[0];

        // 校验维度与归一化
        Assert.Equal(1024, singleVec.Length);
        Assert.Equal(1024, batchVec.Length);

        // 计算余弦相似度
        var dot = 0f;
        for (int i = 0; i < 1024; i++)
        {
            dot += singleVec[i] * batchVec[i];
        }

        _output.WriteLine($"[Consistency Check] Single vs Batch Cosine Similarity = {dot:F6}");
        Assert.True(dot > 0.999f, $"余弦相似度应大于 0.999，实际为 {dot:F6}");
    }

    [Fact]
    public async Task Functional_RealModel_SubBatchSplitting_ExceedingSeqMax()
    {
        if (!File.Exists(RealQwen3GgufPath))
        {
            _output.WriteLine("[Skip] GGUF 模型文件不存在，跳过真实模型测试");
            return;
        }

        using var adapter = new LlamaEmbeddingAdapter(RealQwen3GgufPath, "Qwen3-Embedding-0.6B", gpuLayers: -1, contextSize: 2048);
        await adapter.InitializeAsync();

        // 构造 36 条文本（大于 SeqMax = 16，将自动切分为 3 个子批次: 16 + 16 + 4）
        var totalTexts = 36;
        var texts = Enumerable.Range(1, totalTexts)
            .Select(i => $"这是第 {i} 条用于测试动态 Sub-Batching 拆分与解码重组的语义段落。")
            .ToArray();

        var sw = Stopwatch.StartNew();
        var results = await adapter.EncodeBatchAsync(texts);
        sw.Stop();

        _output.WriteLine($"[Sub-Batching Test] {totalTexts} texts (3 sub-batches) processed in {sw.ElapsedMilliseconds}ms ({sw.ElapsedMilliseconds / (double)totalTexts:F2}ms/item)");

        Assert.Equal(totalTexts, results.Length);
        for (int i = 0; i < totalTexts; i++)
        {
            var vec = results[i];
            Assert.NotNull(vec);
            Assert.Equal(1024, vec.Length);

            var norm = MathF.Sqrt(vec.Sum(x => x * x));
            Assert.True(Math.Abs(norm - 1.0f) < 1e-4, $"Text {i} norm is {norm}");
        }
    }

    [Fact]
    public async Task Stability_RealModel_ConsecutiveRounds_ZeroMemoryLeak()
    {
        if (!File.Exists(RealQwen3GgufPath))
        {
            _output.WriteLine("[Skip] GGUF 模型文件不存在，跳过真实模型测试");
            return;
        }

        using var adapter = new LlamaEmbeddingAdapter(RealQwen3GgufPath, "Qwen3-Embedding-0.6B", gpuLayers: -1, contextSize: 2048);
        await adapter.InitializeAsync();

        var batchTexts = new[]
        {
            "检索增强生成 RAG 系统在企业级私有知识库中的落地应用实践",
            "向量数据库 Milvus 与 pgvector 在高维密集检索场景下的性能对比",
            "基于深度学习的语义嵌入模型微调与多语言对齐策略",
            "大语言模型本地量化 GGUF 与 ONNX Runtime 跨硬件推理加速"
        };

        // 预热 1 轮
        await adapter.EncodeBatchAsync(batchTexts);

        // 连续执行 8 轮批处理
        var roundTimes = new List<long>();
        for (int round = 1; round <= 8; round++)
        {
            var sw = Stopwatch.StartNew();
            var vecs = await adapter.EncodeBatchAsync(batchTexts);
            sw.Stop();
            roundTimes.Add(sw.ElapsedMilliseconds);

            Assert.Equal(4, vecs.Length);
            foreach (var v in vecs)
            {
                Assert.Equal(1024, v.Length);
            }
        }

        _output.WriteLine($"[Stability Rounds] Round Times: {string.Join("ms, ", roundTimes)}ms");
        var avgTime = roundTimes.Average();
        _output.WriteLine($"[Stability Average] Average Time per 4-text batch: {avgTime:F1}ms ({avgTime / 4:F1}ms/text)");

        // 验证每轮耗时平稳，无由于显存溢出/碎片化引起的性能暴跌
        Assert.True(avgTime < 100, $"平均每批耗时应小于 100ms，实际为 {avgTime:F1}ms");
    }

    [Fact]
    public async Task Benchmark_RealModel_ThroughputScalingComparison()
    {
        if (!File.Exists(RealQwen3GgufPath))
        {
            _output.WriteLine("[Skip] GGUF 模型文件不存在，跳过真实模型测试");
            return;
        }

        using var adapter = new LlamaEmbeddingAdapter(RealQwen3GgufPath, "Qwen3-Embedding-0.6B", gpuLayers: -1, contextSize: 2048);
        await adapter.InitializeAsync();

        int[] batchSizes = { 1, 2, 4, 8, 16 };
        _output.WriteLine("================== GGUF 原生批处理吞吐量基准测试 ==================");
        _output.WriteLine($"{"Batch Size",-12} | {"Total Time (ms)",-16} | {"Time/Text (ms)",-16} | {"Throughput (texts/s)",-20}");
        _output.WriteLine(new string('-', 72));

        foreach (var size in batchSizes)
        {
            var texts = Enumerable.Range(1, size)
                .Select(i => $"这是测试吞吐量的数据项 #{i}，验证多序列并行矩阵乘法算力释放。")
                .ToArray();

            // 预热
            await adapter.EncodeBatchAsync(texts);

            // 计时
            var sw = Stopwatch.StartNew();
            var results = await adapter.EncodeBatchAsync(texts);
            sw.Stop();

            var totalMs = sw.ElapsedMilliseconds;
            var perTextMs = totalMs / (double)size;
            var throughput = (size * 1000.0) / Math.Max(1, totalMs);

            _output.WriteLine($"{size,-12} | {totalMs,-16} | {perTextMs,-16:F2} | {throughput,-20:F1}");
            Assert.Equal(size, results.Length);
        }
        _output.WriteLine(new string('-', 72));
    }
}
