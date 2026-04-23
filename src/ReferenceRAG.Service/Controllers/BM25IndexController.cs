using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// BM25 索引管理 API - 管理倒排索引和搜索
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class BM25IndexController : ControllerBase
{
    private readonly IBM25Store _bm25Store;
    private readonly IVectorStore _vectorStore;
    private readonly ConfigManager _configManager;
    private readonly ILogger<BM25IndexController> _logger;

    public BM25IndexController(
        IBM25Store bm25Store,
        IVectorStore vectorStore,
        ConfigManager configManager,
        ILogger<BM25IndexController> logger)
    {
        _bm25Store = bm25Store;
        _vectorStore = vectorStore;
        _configManager = configManager;
        _logger = logger;
    }

    // ==================== 索引操作 ====================

    /// <summary>
    /// 索引所有文档
    /// </summary>
    [HttpPost("index")]
    public async Task<ActionResult<IndexProgressResponse>> IndexAllDocuments()
    {
        _logger.LogInformation("开始索引所有文档");

        // 获取所有文档
        var files = await _vectorStore.GetAllFilesAsync();
        var chunksList = new List<(string chunkId, string content)>();

        foreach (var file in files)
        {
            var chunks = await _vectorStore.GetChunksByFileAsync(file.Id);
            foreach (var chunk in chunks)
            {
                if (!string.IsNullOrWhiteSpace(chunk.Content))
                {
                    chunksList.Add((chunk.Id, chunk.Content));
                }
            }
        }

        if (chunksList.Count == 0)
        {
            return Ok(new IndexProgressResponse
            {
                TotalDocuments = 0,
                ProcessedDocuments = 0,
                ProgressPercent = 100,
                Message = "没有文档需要索引"
            });
        }

        // 先清空再重建，避免孤立条目累积
        await _bm25Store.ClearIndexAsync();

        var progress = new Progress<int>(p =>
        {
            // 进度报告
        });

        await _bm25Store.IndexBatchAsync(chunksList, progress);

        var stats = await _bm25Store.GetStatsAsync();

        return Ok(new IndexProgressResponse
        {
            TotalDocuments = chunksList.Count,
            ProcessedDocuments = chunksList.Count,
            ProgressPercent = 100,
            TotalTerms = stats.VocabularySize,
            Message = $"已索引 {chunksList.Count} 个文档"
        });
    }

    /// <summary>
    /// 索引单个文档
    /// </summary>
    [HttpPost("documents/{chunkId}")]
    public async Task<ActionResult> IndexDocument(string chunkId, [FromBody] IndexDocumentRequest request)
    {
        await _bm25Store.IndexDocumentAsync(chunkId, request.Content);

        return Ok(new { message = $"文档 {chunkId} 已索引" });
    }

    /// <summary>
    /// 清空索引
    /// </summary>
    [HttpDelete("index")]
    public async Task<ActionResult> ClearIndex()
    {
        _logger.LogInformation("清空 BM25 索引");

        await _bm25Store.ClearIndexAsync();

        return Ok(new { message = "索引已清空" });
    }

    // ==================== 搜索操作 ====================

    /// <summary>
    /// BM25 搜索
    /// </summary>
    [HttpGet("search")]
    public async Task<ActionResult<BM25SearchResponse>> Search(
        [FromQuery] string query,
        [FromQuery] int topK = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "查询语句不能为空" });
        }

        var startTime = DateTime.UtcNow;
        var results = await _bm25Store.SearchAsync(query, topK);
        var durationMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;

        return Ok(new BM25SearchResponse
        {
            Query = query,
            TotalResults = results.Count,
            DurationMs = durationMs,
            Results = results.Select((r, index) => new BM25SearchResultItem
            {
                ChunkId = r.ChunkId,
                Content = r.Content,
                Score = r.Score,
                Rank = index + 1
            }).ToList()
        });
    }

    // ==================== 统计信息 ====================

    /// <summary>
    /// 获取 BM25 统计摘要
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<BM25Summary>> GetSummary()
    {
        var stats = await _bm25Store.GetStatsAsync();
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        return Ok(new BM25Summary
        {
            TotalIndexedDocuments = stats.TotalDocuments,
            TotalVocabularySize = stats.VocabularySize,
            AverageDocLength = stats.AverageDocLength,
            TotalFiles = fileList.Count,
            TotalChunks = fileList.Sum(f => f.ChunkCount)
        });
    }
}

// ==================== 请求模型 ====================

public class IndexDocumentRequest
{
    public string Content { get; set; } = string.Empty;
}

// ==================== 响应模型 ====================

public class IndexProgressResponse
{
    public int TotalDocuments { get; set; }
    public int ProcessedDocuments { get; set; }
    public int ProgressPercent { get; set; }
    public int TotalTerms { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class BM25SearchResponse
{
    public string Query { get; set; } = string.Empty;
    public int TotalResults { get; set; }
    public int DurationMs { get; set; }
    public List<BM25SearchResultItem> Results { get; set; } = new();
}

public class BM25SearchResultItem
{
    public string ChunkId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public double Score { get; set; }
    public int Rank { get; set; }
}

public class BM25Summary
{
    public int TotalIndexedDocuments { get; set; }
    public int TotalVocabularySize { get; set; }
    public double AverageDocLength { get; set; }
    public int TotalFiles { get; set; }
    public int TotalChunks { get; set; }
}
