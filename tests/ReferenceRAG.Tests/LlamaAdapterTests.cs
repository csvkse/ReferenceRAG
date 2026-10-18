using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;
using LLama.Native;
using System.Diagnostics;
using Xunit;

namespace ReferenceRAG.Tests;

public class LlamaAdapterTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public LlamaAdapterTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void InspectLLamaApi()
    {
        var embedderType = typeof(LLama.LLamaEmbedder);
        foreach (var p in embedderType.GetProperties())
        {
            _output.WriteLine($"[Embedder Prop] {p.PropertyType.Name} {p.Name}");
        }

        var contextType = typeof(LLama.LLamaContext);
        foreach (var p in contextType.GetProperties())
        {
            _output.WriteLine($"[Context Prop] {p.PropertyType.Name} {p.Name}");
        }

        var batchType = typeof(LLama.Native.LLamaBatch);
        foreach (var c in batchType.GetConstructors())
        {
            var p = string.Join(", ", c.GetParameters().Select(param => $"{param.ParameterType.Name} {param.Name}"));
            _output.WriteLine($"[LLamaBatch Ctor] {c.Name}({p})");
        }
        foreach (var m in batchType.GetMethods().Where(m => m.IsPublic && !m.IsSpecialName))
        {
            var p = string.Join(", ", m.GetParameters().Select(param => $"{param.ParameterType.Name} {param.Name}"));
            _output.WriteLine($"[LLamaBatch Method] {m.ReturnType.Name} {m.Name}({p})");
        }
        foreach (var p in batchType.GetProperties())
        {
            _output.WriteLine($"[LLamaBatch Prop] {p.PropertyType.Name} {p.Name}");
        }

        var paramType = typeof(LLama.Common.ModelParams);
        foreach (var p in paramType.GetProperties())
        {
            if (p.Name.Contains("Seq", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Batch", StringComparison.OrdinalIgnoreCase))
            {
                _output.WriteLine($"[ModelParams Prop] {p.PropertyType.Name} {p.Name}");
            }
        }
    }

    [Fact]
    public async Task LlamaEmbeddingAdapter_FallbackToSimulation_WhenModelFileNotFound()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "non_existent_" + Guid.NewGuid().ToString("N") + ".gguf");
        using var adapter = new LlamaEmbeddingAdapter(missingPath, "test-gguf");

        await adapter.InitializeAsync();

        Assert.True(adapter.IsSimulationMode);
        Assert.Equal(384, adapter.Dimension);

        var vector = await adapter.EncodeAsync("hello world");
        Assert.NotNull(vector);
        Assert.Equal(384, vector.Length);

        // Verify L2 normalization
        var sumSquares = vector.Sum(v => v * v);
        Assert.True(Math.Abs(sumSquares - 1.0f) < 1e-4);
    }

    [Fact]
    public async Task LlamaEmbeddingAdapter_Simulation_EncodeBatchAsync_MatchesDimensions()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "non_existent_" + Guid.NewGuid().ToString("N") + ".gguf");
        using var adapter = new LlamaEmbeddingAdapter(missingPath, "test-gguf");

        await adapter.InitializeAsync();

        var texts = new[] { "text 1", "text 2", "text 3" };
        var results = await adapter.EncodeBatchAsync(texts);

        Assert.Equal(3, results.Length);
        foreach (var v in results)
        {
            Assert.Equal(384, v.Length);
            var sumSquares = v.Sum(x => x * x);
            Assert.True(Math.Abs(sumSquares - 1.0f) < 1e-4);
        }
    }

    [Fact]
    public async Task LlamaRerankAdapter_ThrowsFileNotFound_WhenModelDoesNotExist()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "non_existent_" + Guid.NewGuid().ToString("N") + ".gguf");
        using var adapter = new LlamaRerankAdapter(missingPath, "test-rerank-gguf");

        await Assert.ThrowsAsync<FileNotFoundException>(() => adapter.InitializeAsync());
    }

    [Fact]
    public void LlamaNativeInitializer_EnsureInitialized_ConfiguresCudaSuccessfully()
    {
        var result = ReferenceRAG.Core.Features.HardwareAcceleration.LlamaNativeInitializer.EnsureInitialized(preferCuda: true);
        // On machines with CUDA libraries present, this will return true and configure CUDA with SkipCheck
        Assert.True(result);
    }

    [Fact]
    public async Task LlamaEmbeddingAdapter_LoadRealQwen3Gguf_ResidentContextAndBatchEncoding_Succeeds()
    {
        var realPath = @"E:\LinuxWork\Obsidian\resource\data\models\Embedding\Qwen3-Embedding-0.6B\Qwen3-Embedding-0.6B-Q8_0.gguf";
        if (!File.Exists(realPath)) return;

        using var adapter = new LlamaEmbeddingAdapter(realPath, "Qwen3-Embedding-0.6B", gpuLayers: -1, contextSize: 2048);
        await adapter.InitializeAsync();

        Assert.False(adapter.IsSimulationMode);
        Assert.Equal(1024, adapter.Dimension);

        // 1. Single encode
        var singleVec = await adapter.EncodeAsync("人工智能深度学习向量模型");
        Assert.NotNull(singleVec);
        Assert.Equal(1024, singleVec.Length);
        var singleNorm = MathF.Sqrt(singleVec.Sum(x => x * x));
        Assert.True(Math.Abs(singleNorm - 1.0f) < 1e-4);

        // 2. Batch encode (10 items spanning across sub-batches)
        var texts = Enumerable.Range(1, 10).Select(i => $"这是测试文本编号 {i}，用于测试重构后的常驻上下文与原生多序列批处理加速。").ToArray();
        var sw = Stopwatch.StartNew();
        var batchVecs = await adapter.EncodeBatchAsync(texts);
        sw.Stop();
        _output.WriteLine($"[Refactored LlamaEmbeddingAdapter] 10 texts encoded in {sw.ElapsedMilliseconds}ms");

        Assert.Equal(10, batchVecs.Length);
        foreach (var v in batchVecs)
        {
            Assert.Equal(1024, v.Length);
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            Assert.True(Math.Abs(norm - 1.0f) < 1e-4);
        }

        // 3. CountTokens
        var tokenCount = adapter.CountTokens("人工智能深度学习向量模型");
        Assert.True(tokenCount > 0);
    }

    [Fact]
    public void InspectVectorsDb()
    {
        var dbPath = @"E:\LinuxWork\Obsidian\resource\data\vectors.db";
        if (!File.Exists(dbPath)) return;

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys;";
        var fkStatus = cmd.ExecuteScalar();
        Console.WriteLine($"[DB Inspect] PRAGMA foreign_keys = {fkStatus}");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[DB Inspect] PRAGMA foreign_keys = {fkStatus}");

        cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE sql LIKE '%FOREIGN KEY%';";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                sb.AppendLine($"[FK Table] {reader[0]}: {reader[1]}");
            }
        }

        cmd.CommandText = "PRAGMA foreign_key_check;";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                sb.AppendLine($"[FK Check Violation] Table: {reader[0]}, Rowid: {reader[1]}, Parent: {reader[2]}, FkIdx: {reader[3]}");
            }
        }

        var cfg = new ReferenceRAG.Core.Services.ConfigManager().Load();
        foreach (var s in cfg.Sources)
        {
            var pat = s.FilePatterns != null ? string.Join(",", s.FilePatterns) : "";
            sb.AppendLine($"[Config Source] Name: '{s.Name}', Path: '{s.Path}', Patterns: '{pat}'");
        }

        cmd.CommandText = "SELECT chunk_index, id FROM chunks WHERE file_id = 'fda6b8b1-2be2-4043-8a32-649728146dd9' ORDER BY chunk_index;";
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                sb.AppendLine($"[Chunk] idx: {reader[0]}, id: {reader[1]}");
            }
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "inspect_out.txt"), sb.ToString());
    }

    [Fact]
    public async Task LlamaRerankAdapter_RealModel_DiagnoseScores()
    {
        var realPath = @"E:\LinuxWork\Obsidian\resource\data\models\Reranker\Qwen3-Reranker-0.6B\Qwen3-Reranker-0.6B.Q4_0.gguf";
        if (!File.Exists(realPath)) return;

        using var adapter = new LlamaRerankAdapter(realPath, "Qwen3-Reranker-0.6B", gpuLayers: -1, contextSize: 2048);
        await adapter.InitializeAsync();

        var score = await adapter.RerankAsync("你好", "你好，很高兴认识你。");
        _output.WriteLine($"Single rerank score: {score}");
        Assert.False(double.IsNaN(score), "Single rerank score must not be NaN");
        Assert.False(double.IsInfinity(score), "Single rerank score must not be Infinity");
        Assert.InRange(score, 0.0, 1.0);

        var docs = new[] { "你好，世界", "今天天气真好", "这是一个测试文档" };
        var batch = await adapter.RerankBatchAsync("你好", docs);
        _output.WriteLine($"Batch rerank duration: {batch.DurationMs}ms, count: {batch.Documents.Count}");

        Assert.Equal(docs.Length, batch.Documents.Count);
        foreach (var doc in batch.Documents)
        {
            _output.WriteLine($"  [{doc.Index}] score: {doc.RelevanceScore:F4}, doc: {doc.Document}");
            Assert.False(double.IsNaN(doc.RelevanceScore), "Document RelevanceScore must not be NaN");
            Assert.False(double.IsInfinity(doc.RelevanceScore), "Document RelevanceScore must not be Infinity");
            Assert.InRange(doc.RelevanceScore, 0.0, 1.0);
        }

        // Verify JSON serialization works seamlessly
        var json = System.Text.Json.JsonSerializer.Serialize(batch);
        Assert.NotNull(json);
        Assert.DoesNotContain("NaN", json);
    }
}

