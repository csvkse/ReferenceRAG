using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Tests;

/// <summary>
/// 向量相似度测试 - 验证文档查询的相似度是否合理
/// </summary>
public class VectorSimilarityTests : IDisposable
{
    private readonly string _testDataPath;
    private readonly string _testVaultPath;
    private readonly IServiceProvider _services;
    private readonly SqliteVectorStore _vectorStore;

    public VectorSimilarityTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"obsidian-rag-similarity-test-{Guid.NewGuid():N}");
        _testVaultPath = Path.Combine(_testDataPath, "vault");
        Directory.CreateDirectory(_testVaultPath);

        _vectorStore = new SqliteVectorStore(Path.Combine(_testDataPath, "test.db"), 384);

        var services = new ServiceCollection();
        services.AddSingleton<IVectorStore>(_vectorStore);
        services.AddSingleton<IEmbeddingService>(sp =>
        {
            // 使用配置的模型路径或模拟模式
            var configPath = Path.Combine(_testDataPath, "config.json");
            var configManager = new ConfigManager(configPath);
            var config = configManager.Load();

            if (File.Exists(config.Embedding.ModelPath))
            {
                return new EmbeddingService(new EmbeddingOptions
                {
                    ModelPath = config.Embedding.ModelPath,
                    ModelName = config.Embedding.ModelName,
                    MaxSequenceLength = config.Embedding.MaxSequenceLength,
                    BatchSize = config.Embedding.BatchSize
                });
            }

            // 如果没有真实模型，使用模拟模式
            Console.WriteLine("[测试] 使用模拟 EmbeddingService");
            return new MockEmbeddingService(384);
        });
        services.AddSingleton<MarkdownChunker>();
        services.AddSingleton<ContentHashDetector>();
        services.AddSingleton<ITextEnhancer, TextEnhancer>();
        services.AddLogging();

        _services = services.BuildServiceProvider();
    }

    [Fact]
    public async Task QueryExactContent_ReturnsHighSimilarity()
    {
        // Arrange - 创建测试文档
        var testContent = "这是一个关于向量数据库的测试文档。向量数据库是一种专门用于存储和检索向量嵌入的数据库系统。";
        var testFile = Path.Combine(_testVaultPath, "test-doc.md");
        await File.WriteAllTextAsync(testFile, testContent);

        // 索引文档
        var chunker = _services.GetRequiredService<MarkdownChunker>();
        var embeddingService = _services.GetRequiredService<IEmbeddingService>();
        var hashDetector = _services.GetRequiredService<ContentHashDetector>();

        var content = await File.ReadAllTextAsync(testFile);
        var hash = hashDetector.ComputeFingerprint(content);

        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid().ToString(),
            Path = testFile,
            Title = "测试文档",
            ContentHash = hash,
            ChunkCount = 1
        };
        await _vectorStore.UpsertFileAsync(fileRecord);

        var chunks = chunker.Chunk(content, fileRecord).ToList();
        await _vectorStore.UpsertChunksAsync(chunks);

        // 为每个 chunk 生成向量
        foreach (var chunk in chunks)
        {
            var embedding = await embeddingService.EncodeAsync(chunk.Content, EmbeddingMode.Symmetric);
            await _vectorStore.UpsertVectorAsync(new VectorRecord
            {
                Id = Guid.NewGuid().ToString(),
                ChunkId = chunk.Id,
                Vector = embedding,
                ModelName = embeddingService.ModelName
            });
        }

        // Act - 用相同内容查询（直接使用向量搜索）
        var queryEmbedding = await embeddingService.EncodeAsync(testContent, EmbeddingMode.Symmetric);
        var results = await _vectorStore.SearchAsync(queryEmbedding, topK: 1);
        var topResult = results.First();

        // Assert - 相同内容的相似度应该很高（> 0.8）
        Console.WriteLine($"[测试] 查询相似度: {topResult.Score:F4}");
        Console.WriteLine($"[测试] 匹配内容: {topResult.Content.Substring(0, Math.Min(50, topResult.Content.Length))}...");

        Assert.True(topResult.Score > 0.8f, $"相同内容查询的相似度应该 > 0.8，实际为 {topResult.Score:F4}");
    }

    [Fact(Skip = "MockEmbeddingService 不理解语义相似度，需要真实 BGE 模型")]
    public async Task QuerySimilarContent_ReturnsModerateSimilarity()
    {
        // 注意：此测试需要真实的 BGE 模型才能正确运行
        // MockEmbeddingService 基于文本哈希生成向量，不具备语义理解能力
        // 当配置正确的模型路径后，此测试应能通过

        // Arrange
        var originalContent = "向量数据库是一种高效存储和检索向量数据的技术。";
        var similarContent = "向量数据库用于存储和检索向量嵌入数据。";

        var testFile = Path.Combine(_testVaultPath, "similar-test.md");
        await File.WriteAllTextAsync(testFile, originalContent);

        // 索引
        var chunker = _services.GetRequiredService<MarkdownChunker>();
        var embeddingService = _services.GetRequiredService<IEmbeddingService>();
        var hashDetector = _services.GetRequiredService<ContentHashDetector>();

        var content = await File.ReadAllTextAsync(testFile);
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid().ToString(),
            Path = testFile,
            Title = "相似度测试",
            ContentHash = hashDetector.ComputeFingerprint(content),
            ChunkCount = 1
        };
        await _vectorStore.UpsertFileAsync(fileRecord);

        var chunks = chunker.Chunk(content, fileRecord).ToList();
        await _vectorStore.UpsertChunksAsync(chunks);

        foreach (var chunk in chunks)
        {
            var embedding = await embeddingService.EncodeAsync(chunk.Content, EmbeddingMode.Symmetric);
            await _vectorStore.UpsertVectorAsync(new VectorRecord
            {
                Id = Guid.NewGuid().ToString(),
                ChunkId = chunk.Id,
                Vector = embedding,
                ModelName = embeddingService.ModelName
            });
        }

        // Act - 用相似但不完全相同的内容查询
        var queryEmbedding = await embeddingService.EncodeAsync(similarContent, EmbeddingMode.Symmetric);
        var results = await _vectorStore.SearchAsync(queryEmbedding, topK: 1);
        var topResult = results.First();

        // Assert - 相似内容的相似度应该介于 0.5-0.95 之间（对于真实 BGE 模型）
        Console.WriteLine($"[测试] 相似内容查询相似度: {topResult.Score:F4}");

        Assert.True(topResult.Score > 0.5f, $"相似内容查询的相似度应该 > 0.5，实际为 {topResult.Score:F4}");
        Assert.True(topResult.Score < 0.98f, $"相似内容查询的相似度应该 < 0.98（不完全相同），实际为 {topResult.Score:F4}");
    }

    [Fact]
    public async Task QueryDifferentContent_ReturnsLowSimilarity()
    {
        // Arrange
        var originalContent = "向量数据库用于高效存储和检索向量嵌入。";

        var testFile = Path.Combine(_testVaultPath, "different-test.md");
        await File.WriteAllTextAsync(testFile, originalContent);

        // 索引
        var chunker = _services.GetRequiredService<MarkdownChunker>();
        var embeddingService = _services.GetRequiredService<IEmbeddingService>();
        var hashDetector = _services.GetRequiredService<ContentHashDetector>();

        var content = await File.ReadAllTextAsync(testFile);
        var fileRecord = new FileRecord
        {
            Id = Guid.NewGuid().ToString(),
            Path = testFile,
            Title = "不同内容测试",
            ContentHash = hashDetector.ComputeFingerprint(content),
            ChunkCount = 1
        };
        await _vectorStore.UpsertFileAsync(fileRecord);

        var chunks = chunker.Chunk(content, fileRecord).ToList();
        await _vectorStore.UpsertChunksAsync(chunks);

        foreach (var chunk in chunks)
        {
            var embedding = await embeddingService.EncodeAsync(chunk.Content, EmbeddingMode.Symmetric);
            await _vectorStore.UpsertVectorAsync(new VectorRecord
            {
                Id = Guid.NewGuid().ToString(),
                ChunkId = chunk.Id,
                Vector = embedding,
                ModelName = embeddingService.ModelName
            });
        }

        // Act - 用完全不同的内容查询
        var queryEmbedding = await embeddingService.EncodeAsync("今天天气真好，适合出去跑步锻炼身体。", EmbeddingMode.Symmetric);
        var results = await _vectorStore.SearchAsync(queryEmbedding, topK: 1);
        var topResult = results.First();

        // Assert - 不相关内容的相似度应该较低
        Console.WriteLine($"[测试] 不同内容查询相似度: {topResult.Score:F4}");

        Assert.True(topResult.Score < 0.7f, $"不相关内容查询的相似度应该 < 0.7，实际为 {topResult.Score:F4}");
    }

    [Fact]
    public async Task DirectSimilarity_ExactMatch_ShouldBeNearOne()
    {
        // Arrange - 直接测试 EmbeddingService 的相似度计算
        var embeddingService = _services.GetRequiredService<IEmbeddingService>();
        var text = "向量数据库是一种高效的向量检索技术。";

        // Act - 编码两次相同文本
        var v1 = await embeddingService.EncodeAsync(text);
        var v2 = await embeddingService.EncodeAsync(text);

        var similarity = embeddingService.Similarity(v1, v2);

        // Assert - 完全相同的文本，相似度应该接近 1.0
        Console.WriteLine($"[测试] 直接编码相同文本相似度: {similarity:F4}");
        Assert.True(similarity > 0.95f, $"完全相同文本的相似度应该 > 0.95，实际为 {similarity:F4}");
    }

    public void Dispose()
    {
        (_vectorStore as IDisposable)?.Dispose();
        if (Directory.Exists(_testDataPath))
        {
            try
            {
                Directory.Delete(_testDataPath, true);
            }
            catch { }
        }
    }
}

/// <summary>
/// 模拟 EmbeddingService - 基于文本内容生成确定性向量（用于测试）
/// </summary>
public class MockEmbeddingService : IEmbeddingService
{
    private readonly int _dimension;
    private readonly Dictionary<string, float[]> _cache = new();

    public string ModelName => "mock";
    public int Dimension => _dimension;
    public bool IsSimulationMode => false;
    public bool SupportsAsymmetricEncoding => false;

    public MockEmbeddingService(int dimension = 384)
    {
        _dimension = dimension;
    }

    public Task<bool> ReloadModelAsync(string modelPath, string modelName)
    {
        return Task.FromResult(true);
    }

    public void UnloadModel()
    {
        // Mock implementation - nothing to dispose
    }

    public Task<float[]> EncodeAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(text, out var cached))
        {
            return Task.FromResult(cached);
        }

        // 基于文本内容生成确定性向量
        var hash = text.GetHashCode();
        var random = new Random(hash);
        var vector = new float[_dimension];
        for (int i = 0; i < _dimension; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        }
        Normalize(vector);
        _cache[text] = vector;
        return Task.FromResult(vector);
    }

    public Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var result = texts.Select(t => EncodeAsync(t, cancellationToken).Result).ToArray();
        return Task.FromResult(result);
    }

    public Task<float[]> EncodeAsync(string text, EmbeddingMode mode, CancellationToken cancellationToken = default)
    {
        return EncodeAsync(text, cancellationToken);
    }

    public Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, EmbeddingMode mode, CancellationToken cancellationToken = default)
    {
        return EncodeBatchAsync(texts, cancellationToken);
    }

    public float Similarity(float[] a, float[] b)
    {
        float dot = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
    }

    public float[] Normalize(float[] vector)
    {
        float sum = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }
        var norm = MathF.Sqrt(sum);
        if (norm < 1e-10f) return vector;
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
        return vector;
    }
}
