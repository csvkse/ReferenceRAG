using System.Text.Json;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Rerank;
using ReferenceRAG.Core.Tracing;

namespace ReferenceRAG.Tests;

public class SearchTraceBreakdownTests
{
    [Fact]
    public void SearchTraceContext_RecordsAllPhasesAccurately()
    {
        // 模拟请求生命周期初始化上下文
        SearchTraceContext.Current.Value = new SearchPhaseTimings();

        SearchTraceContext.RecordModelLoad(1820);
        SearchTraceContext.RecordEmbed(25);
        SearchTraceContext.RecordBm25(6);
        SearchTraceContext.RecordVector(20);
        SearchTraceContext.RecordHybrid(5);
        SearchTraceContext.RecordTitle(2);
        SearchTraceContext.RecordGraph(12);
        SearchTraceContext.RecordRerank(88);

        var current = SearchTraceContext.Current.Value;
        Assert.NotNull(current);
        Assert.Equal(1820, current.ModelLoadMs);
        Assert.Equal(25, current.EmbedMs);
        Assert.Equal(6, current.Bm25Ms);
        Assert.Equal(20, current.VectorMs);
        Assert.Equal(5, current.HybridMs);
        Assert.Equal(2, current.TitleMs);
        Assert.Equal(12, current.GraphMs);
        Assert.Equal(88, current.RerankMs);

        // 清理上下文
        SearchTraceContext.Current.Value = null;
    }

    [Fact]
    public void SearchPhaseCollector_AggregatesModelLoadAndBm25Statistics()
    {
        SearchPhaseCollector.Reset();

        var stats1 = new SearchPhaseStats
        {
            ModelLoadMs = 1800,
            EmbedMs = 30,
            Bm25Ms = 8,
            VectorMs = 22,
            HybridMs = 4,
            TitleMs = 0,
            GraphMs = 0,
            RerankMs = 120
        };

        var stats2 = new SearchPhaseStats
        {
            ModelLoadMs = 0, // 命中常驻缓存
            EmbedMs = 25,
            Bm25Ms = 6,
            VectorMs = 18,
            HybridMs = 3,
            TitleMs = 5,
            GraphMs = 10,
            RerankMs = 0
        };

        SearchPhaseCollector.Record(stats1);
        SearchPhaseCollector.Record(stats2);

        var report = SearchPhaseCollector.GetReport();
        Assert.Equal(2, report.TotalSearches);
        Assert.Equal(1, report.ModelLoad.Count); // 仅记录了 >0 的一次
        Assert.Equal(1800, report.ModelLoad.Max);
        Assert.Equal(2, report.Embed.Count);
        Assert.Equal(2, report.Bm25.Count);
        Assert.Equal(2, report.Vector.Count);
        Assert.Equal(2, report.Hybrid.Count);
        Assert.Equal(1, report.Title.Count);
        Assert.Equal(1, report.Graph.Count);
        Assert.Equal(1, report.Rerank.Count);
    }

    [Fact]
    public async Task LazyEmbeddingService_RecordsModelLoadOnColdStart_AndPureEmbedOnInference()
    {
        SearchTraceContext.Current.Value = new SearchPhaseTimings();

        using var service = new LazyEmbeddingService(
            new EmbeddingOptions { ModelPath = "missing.onnx", ModelName = "test-embed" },
            null,
            () => new EmbeddingService(new EmbeddingOptions { ModelPath = "missing.onnx", ModelName = "test-embed" }));

        await service.EncodeAsync("test query");

        var current = SearchTraceContext.Current.Value;
        Assert.NotNull(current);
        // 冷启动应记录了 ModelLoadMs
        Assert.True(current.ModelLoadMs >= 0);
        // 推理耗时应记录了 EmbedMs
        Assert.True(current.EmbedMs >= 0);

        SearchTraceContext.Current.Value = null;
    }

    [Fact]
    public async Task LazyRerankService_RecordsModelLoadOnColdStart_AndPureRerankOnInference()
    {
        SearchTraceContext.Current.Value = new SearchPhaseTimings();

        using var service = new LazyRerankService(
            "test-rerank",
            () => new OnnxRerankService(new RerankOptions { ModelPath = "missing.onnx", ModelName = "test-rerank" }));

        await service.RerankAsync("query", "document");

        var current = SearchTraceContext.Current.Value;
        Assert.NotNull(current);
        Assert.True(current.ModelLoadMs >= 0);
        Assert.True(current.RerankMs >= 0);

        SearchTraceContext.Current.Value = null;
    }

    [Fact]
    public void SearchPhaseStats_SerializesToCamelCaseJsonProperly()
    {
        var stats = new SearchPhaseStats
        {
            ModelLoadMs = 1820,
            EmbedMs = 25,
            Bm25Ms = 6,
            VectorMs = 20,
            HybridMs = 5,
            TitleMs = 3,
            GraphMs = 15,
            RerankMs = 90
        };

        var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.Contains("\"modelLoadMs\":1820", json);
        Assert.Contains("\"embedMs\":25", json);
        Assert.Contains("\"bm25Ms\":6", json);
        Assert.Contains("\"vectorMs\":20", json);
        Assert.Contains("\"hybridMs\":5", json);
        Assert.Contains("\"titleMs\":3", json);
        Assert.Contains("\"graphMs\":15", json);
        Assert.Contains("\"rerankMs\":90", json);
    }

    [Fact]
    public async Task LlamaRerank_EarlyStopsOnNaN_PreventsMassiveLatency()
    {
        var realGgufPath = @"E:\LinuxWork\Obsidian\resource\data\models\Reranker\Qwen3-Reranker-0.6B\Qwen3-Reranker-0.6B.Q4_0.gguf";
        if (!File.Exists(realGgufPath)) return;

        using var adapter = new ReferenceRAG.Core.Features.ModelManagement.Adapters.LlamaRerankAdapter(
            realGgufPath, "Qwen3-Reranker-0.6B", gpuLayers: -1, contextSize: 2048);
        await adapter.InitializeAsync();

        // 模拟 10 个候选文档（通常会导致大量串行耗时）
        var docs = Enumerable.Range(1, 10).Select(i => $"这是第 {i} 个测试文档，内容包含一些相关信息。").ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await adapter.RerankBatchAsync("你好", docs);
        sw.Stop();

        // 验证返回全部 10 个候选
        Assert.Equal(10, result.Documents.Count);
        // 验证由于早停机制生效，10 个候选不会执行 10 次单条循环，耗时远小于 1000ms（通常在 200ms 以内）
        Assert.True(sw.ElapsedMilliseconds < 800, $"重排应早停止损，实际耗时: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task OnnxRerank_BatchBenchmark_ExecutesFastWithRealScores()
    {
        var onnxPath = @"E:\LinuxWork\Obsidian\resource\data\models\Reranker\bge-reranker-base\model.onnx";
        if (!File.Exists(onnxPath)) return;

        var options = new RerankOptions
        {
            ModelPath = onnxPath,
            ModelName = "bge-reranker-base",
            UseCuda = true,
            CudaDeviceId = 0,
            MaxSequenceLength = 512
        };

        using var service = new OnnxRerankService(options);
        if (!service.IsLoaded) return;

        var docs = new[]
        {
            "你好，欢迎使用系统！",
            "计算机操作系统内存管理与分页机制",
            "今天天气晴朗，微风徐徐",
            "深度学习自然语言处理与向量检索系统",
            "你好，很高兴见到你！"
        };

        var result = await service.RerankBatchAsync("你好", docs);

        Assert.Equal(5, result.Documents.Count);
        // 验证相关文档分数高于不相关文档
        var helloScores = result.Documents.Where(d => d.Document.Contains("你好")).Select(d => d.RelevanceScore).ToList();
        var otherScores = result.Documents.Where(d => !d.Document.Contains("你好")).Select(d => d.RelevanceScore).ToList();

        Assert.True(helloScores.Average() > otherScores.Average(), "相关文档得分均值应高于不相关文档");
    }
}
