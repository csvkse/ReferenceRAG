using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 性能测试 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PerformanceController : ControllerBase
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IMarkdownChunker _chunker;
    private readonly ITokenizer _tokenizer;
    private readonly ConfigManager _configManager;
    private readonly ILogger<PerformanceController> _logger;

    public PerformanceController(
        IEmbeddingService embeddingService,
        IMarkdownChunker chunker,
        ITokenizer tokenizer,
        ConfigManager configManager,
        ILogger<PerformanceController> logger)
    {
        _embeddingService = embeddingService;
        _chunker = chunker;
        _tokenizer = tokenizer;
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>
    /// 长文本向量索引性能测试
    /// </summary>
    [HttpPost("benchmark")]
    public async Task<ActionResult<BenchmarkResult>> RunBenchmark([FromBody] BenchmarkRequest request)
    {
        // 优先使用前端传入值，否则使用配置文件的值
        var config = _configManager.Load();
        var batchSize = request.BatchSize ?? config.Embedding.BatchSize;

        var result = new BenchmarkResult
        {
            TextLength = request.TextLength,
            ChunkSize = request.ChunkSize,
            OverlapSize = request.OverlapSize,
            BatchSize = batchSize
        };

        try
        {
            // 1. 生成测试文本
            var sw = Stopwatch.StartNew();
            var textLength = Math.Min(request.TextLength, 500000);
            var text = GenerateTestText(textLength, request.IncludeCodeBlocks, request.IncludeHeadings);
            sw.Stop();
            result.TextGenerationMs = sw.ElapsedMilliseconds;

            // 2. 分段测试
            sw.Restart();
            var chunks = _chunker.Chunk(text, new ChunkingOptions
            {
                MaxTokens = request.ChunkSize,
                MinTokens = 50,
                OverlapTokens = request.OverlapSize,
                PreserveHeadings = request.IncludeHeadings,
                PreserveCodeBlocks = request.IncludeCodeBlocks
            });
            sw.Stop();
            result.ChunkingMs = sw.ElapsedMilliseconds;
            result.ChunkCount = chunks.Count;
            result.AvgChunkTokens = chunks.Average(c => _tokenizer.CountTokens(c.Content));

            // 3. Token 统计（单独计时）
            var texts = chunks.Select(c => c.Content).ToList();

            // 字符统计（无需计时，O(1) 操作）
            result.TotalChars = texts.Sum(t => t.Length);

            sw.Restart();
            var totalTokens = 0;
            foreach (var t in texts)
            {
                totalTokens += _tokenizer.CountTokens(t);
            }
            sw.Stop();
            result.TokenCountMs = sw.ElapsedMilliseconds;
            result.TotalTokens = totalTokens;

            // 4. 向量化测试（使用配置的 BatchSize）
            sw.Restart();
            var allVectors = new List<float[]>();
            var batches = SplitIntoBatches(texts, batchSize);

            // 并行处理批次
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4)
            };

            var batchResults = new ConcurrentBag<(int Index, float[][] Vectors)>();
            Parallel.For(0, batches.Count, parallelOptions, i =>
            {
                var batchVectors = _embeddingService.EncodeBatchAsync(batches[i], EmbeddingMode.Document).GetAwaiter().GetResult();
                batchResults.Add((i, batchVectors));
            });

            // 按顺序合并结果
            foreach (var (_, vectors) in batchResults.OrderBy(x => x.Index))
            {
                allVectors.AddRange(vectors);
            }
            sw.Stop();

            result.EmbeddingMs = sw.ElapsedMilliseconds;
            result.VectorCount = allVectors.Count;
            result.VectorDimension = allVectors.FirstOrDefault()?.Length ?? 0;

            // 5. 模拟存储测试
            sw.Restart();
            var records = allVectors.Select((v, i) => new Core.Models.VectorRecord
            {
                Id = Guid.NewGuid().ToString(),
                ChunkId = $"chunk-{i}",
                Content = texts[i],
                Vector = v,
                Source = "benchmark",
                FilePath = "benchmark.md",
                Title = $"Benchmark Chunk {i}"
            }).ToList();
            sw.Stop();
            result.StoragePrepMs = sw.ElapsedMilliseconds;

            // 6. 总时间
            result.TotalMs = result.TextGenerationMs + result.ChunkingMs + result.TokenCountMs + result.EmbeddingMs + result.StoragePrepMs;

            // 7. 性能指标
            var processingMs = result.TokenCountMs + result.EmbeddingMs;
            result.TokensPerSecond = processingMs > 0 ? totalTokens / (processingMs / 1000.0) : 0;
            result.CharsPerSecond = processingMs > 0 ? result.TotalChars / (processingMs / 1000.0) : 0;
            result.ChunksPerSecond = result.ChunkingMs > 0 ? result.ChunkCount / (result.ChunkingMs / 1000.0) : 0;
            result.VectorsPerSecond = result.EmbeddingMs > 0 ? result.VectorCount / (result.EmbeddingMs / 1000.0) : 0;

            // 8. 内存估算
            result.EstimatedMemoryMB = allVectors.Count > 0 && allVectors[0].Length > 0
                ? (allVectors.Count * allVectors[0].Length * sizeof(float)) / (1024.0 * 1024.0)
                : 0;

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Benchmark failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// 快速性能测试
    /// </summary>
    [HttpGet("quick-test")]
    public async Task<ActionResult<QuickTestResult>> QuickTest([FromQuery] int textLength = 10000)
    {
        if (textLength > 500000)
            return BadRequest(new { error = "textLength 不能超过 500000" });

        var result = new QuickTestResult { TextLength = Math.Min(textLength, 500000) };

        try
        {
            var text = GenerateTestText(textLength, false, false);

            var sw = Stopwatch.StartNew();
            var chunks = _chunker.Chunk(text, new ChunkingOptions { MaxTokens = 512 });
            sw.Stop();
            result.ChunkingTimeMs = sw.ElapsedMilliseconds;
            result.ChunkCount = chunks.Count;

            sw.Restart();
            var vectors = await _embeddingService.EncodeBatchAsync(chunks.Select(c => c.Content).Take(5).ToList(), EmbeddingMode.Document);
            sw.Stop();
            result.SampleEmbeddingTimeMs = sw.ElapsedMilliseconds;

            result.EstimatedFullTimeMs = (result.ChunkCount / 5.0) * result.SampleEmbeddingTimeMs;

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Batch optimization test failed");
            return StatusCode(500, new { error = "测试执行失败，请查看服务日志" });
        }
    }

    /// <summary>
    /// 批量大小优化测试
    /// </summary>
    [HttpPost("batch-sizes")]
    public async Task<ActionResult<BatchOptimizationResult>> TestBatchSizes([FromBody] BatchOptimizationRequest request)
    {
        var text = GenerateTestText(request.TextLength, false, false);
        var chunks = _chunker.Chunk(text, new ChunkingOptions { MaxTokens = 512 });
        var texts = chunks.Select(c => c.Content).ToList();

        var results = new List<BatchSizeResult>();
        var batchSizes = new[] { 1, 2, 4, 8, 16, 32, 64 };

        foreach (var batchSize in batchSizes)
        {
            if (batchSize > texts.Count) break;

            var batches = SplitIntoBatches(texts, batchSize);
            var times = new List<long>();

            foreach (var batch in batches.Take(3)) // 只测试前3批
            {
                var sw = Stopwatch.StartNew();
                await _embeddingService.EncodeBatchAsync(batch, EmbeddingMode.Document);
                times.Add(sw.ElapsedMilliseconds);
            }

            results.Add(new BatchSizeResult
            {
                BatchSize = batchSize,
                AvgTimeMs = times.Average(),
                TotalBatches = (int)Math.Ceiling((double)texts.Count / batchSize),
                EstimatedTotalTimeMs = times.Average() * Math.Ceiling((double)texts.Count / batchSize)
            });
        }

        return Ok(new BatchOptimizationResult
        {
            TextLength = request.TextLength,
            ChunkCount = chunks.Count,
            Results = results
        });
    }

    /// <summary>
    /// 内存使用测试
    /// </summary>
    [HttpGet("memory-test")]
    public ActionResult<MemoryTestResult> MemoryTest([FromQuery] int vectorCount = 1000, [FromQuery] int dimension = 512)
    {
        // Input validation to prevent DoS via excessive memory allocation
        if (vectorCount < 1 || vectorCount > 10000)
        {
            return BadRequest(new { error = "vectorCount 必须在 1-10000 之间" });
        }
        if (dimension < 1 || dimension > 2048)
        {
            return BadRequest(new { error = "dimension 必须在 1-2048 之间" });
        }
        var sw = Stopwatch.StartNew();

        // 分配内存
        var vectors = new List<float[]>();
        for (int i = 0; i < vectorCount; i++)
        {
            var vector = new float[dimension];
            for (int j = 0; j < dimension; j++)
            {
                vector[j] = Random.Shared.NextSingle();
            }
            vectors.Add(vector);
        }

        sw.Stop();

        var memoryBytes = vectorCount * dimension * sizeof(float);
        var memoryMB = memoryBytes / (1024.0 * 1024.0);

        return Ok(new MemoryTestResult
        {
            VectorCount = vectorCount,
            Dimension = dimension,
            MemoryBytes = memoryBytes,
            MemoryMB = memoryMB,
            AllocationTimeMs = sw.ElapsedMilliseconds
        });
    }

    private string GenerateTestText(int length, bool includeCodeBlocks, bool includeHeadings)
    {
        var sb = new System.Text.StringBuilder();
        var random = new Random();

        var paragraphs = new[]
        {
            "这是一个测试段落，用于性能基准测试。我们正在测试向量索引的性能表现。",
            "Obsidian 是一个强大的知识管理工具，支持 Markdown 格式的笔记。",
            "向量检索技术可以让我们通过语义相似度来查找相关内容。",
            "机器学习模型可以将文本转换为高维向量表示。",
            "RAG（检索增强生成）是一种结合检索和生成的技术方案。",
            "自然语言处理是人工智能的重要分支领域。",
            "深度学习模型在文本理解任务上表现出色。",
            "知识图谱可以帮助组织和关联信息。",
            "语义搜索比关键词搜索更加智能和准确。",
            "文本分段是向量索引的重要预处理步骤。"
        };

        var codeBlocks = new[]
        {
            "```python\ndef hello():\n    print('Hello, World!')\n```",
            "```javascript\nconst app = express();\napp.get('/', (req, res) => res.send('Hi'));\n```",
            "```csharp\npublic class Test {\n    public void Run() { }\n}\n```"
        };

        var headings = new[] { "## 简介", "### 详细说明", "#### 补充内容", "## 总结" };

        var currentLength = 0;
        var paragraphIndex = 0;

        while (currentLength < length)
        {
            if (includeHeadings && random.Next(10) == 0)
            {
                var heading = headings[random.Next(headings.Length)];
                sb.AppendLine(heading);
                currentLength += heading.Length;
            }

            if (includeCodeBlocks && random.Next(15) == 0)
            {
                var code = codeBlocks[random.Next(codeBlocks.Length)];
                sb.AppendLine(code);
                currentLength += code.Length;
            }

            var paragraph = paragraphs[paragraphIndex % paragraphs.Length];
            sb.AppendLine(paragraph);
            sb.AppendLine();
            currentLength += paragraph.Length + 2;
            paragraphIndex++;
        }

        return sb.ToString();
    }

    private List<List<string>> SplitIntoBatches(List<string> items, int batchSize)
    {
        if (items.Count == 0) return new List<List<string>>();

        var batches = new List<List<string>>((items.Count + batchSize - 1) / batchSize);
        for (int i = 0; i < items.Count; i += batchSize)
        {
            var remaining = Math.Min(batchSize, items.Count - i);
            var batch = new List<string>(remaining);
            for (int j = 0; j < remaining; j++)
            {
                batch.Add(items[i + j]);
            }
            batches.Add(batch);
        }
        return batches;
    }
}

/// <summary>
/// 基准测试请求
/// </summary>
public class BenchmarkRequest
{
    /// <summary>
    /// 文本长度（字符数），最大 500000
    /// </summary>
    public int TextLength { get; set; } = 10000;

    /// <summary>
    /// 分段大小（tokens）
    /// </summary>
    public int ChunkSize { get; set; } = 512;

    /// <summary>
    /// 重叠大小（tokens）
    /// </summary>
    public int OverlapSize { get; set; } = 50;

    /// <summary>
    /// 批处理大小（默认从配置读取）
    /// </summary>
    public int? BatchSize { get; set; }

    /// <summary>
    /// 是否包含代码块
    /// </summary>
    public bool IncludeCodeBlocks { get; set; } = true;

    /// <summary>
    /// 是否包含标题
    /// </summary>
    public bool IncludeHeadings { get; set; } = true;
}

/// <summary>
/// 基准测试结果
/// </summary>
public class BenchmarkResult
{
    public int TextLength { get; set; }
    public int ChunkSize { get; set; }
    public int OverlapSize { get; set; }
    public int BatchSize { get; set; }

    public long TextGenerationMs { get; set; }
    public long ChunkingMs { get; set; }
    public long TokenCountMs { get; set; }
    public long EmbeddingMs { get; set; }
    public long StoragePrepMs { get; set; }
    public long TotalMs { get; set; }

    public int ChunkCount { get; set; }
    public int TotalChars { get; set; }
    public int TotalTokens { get; set; }
    public double AvgChunkTokens { get; set; }
    public int VectorCount { get; set; }
    public int VectorDimension { get; set; }

    public double TokensPerSecond { get; set; }
    public double CharsPerSecond { get; set; }
    public double ChunksPerSecond { get; set; }
    public double VectorsPerSecond { get; set; }

    public double EstimatedMemoryMB { get; set; }
}

/// <summary>
/// 快速测试结果
/// </summary>
public class QuickTestResult
{
    public int TextLength { get; set; }
    public long ChunkingTimeMs { get; set; }
    public int ChunkCount { get; set; }
    public long SampleEmbeddingTimeMs { get; set; }
    public double EstimatedFullTimeMs { get; set; }
}

/// <summary>
/// 批量大小优化请求
/// </summary>
public class BatchOptimizationRequest
{
    public int TextLength { get; set; } = 20000;
}

/// <summary>
/// 批量大小优化结果
/// </summary>
public class BatchOptimizationResult
{
    public int TextLength { get; set; }
    public int ChunkCount { get; set; }
    public List<BatchSizeResult> Results { get; set; } = new();
}

/// <summary>
/// 批量大小结果
/// </summary>
public class BatchSizeResult
{
    public int BatchSize { get; set; }
    public double AvgTimeMs { get; set; }
    public int TotalBatches { get; set; }
    public double EstimatedTotalTimeMs { get; set; }
}

/// <summary>
/// 内存测试结果
/// </summary>
public class MemoryTestResult
{
    public int VectorCount { get; set; }
    public int Dimension { get; set; }
    public long MemoryBytes { get; set; }
    public double MemoryMB { get; set; }
    public long AllocationTimeMs { get; set; }
}
