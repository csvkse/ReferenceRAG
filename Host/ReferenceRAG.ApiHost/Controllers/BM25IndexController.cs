using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// BM25 索引管理 API - 管理倒排索引和搜索
/// </summary>
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/[controller]")]
public class BM25IndexController : ControllerBase
{
    private readonly IBM25Store _bm25Store;
    private readonly IVectorStore _vectorStore;
    private readonly ConfigManager _configManager;
    private readonly IndexService _indexService;
    private readonly ILogger<BM25IndexController> _logger;
    private readonly IKnowledgeBaseStorageManager? _kbStorageManager;
    private readonly ICatalogStore? _catalogStore;

    public BM25IndexController(
        IBM25Store bm25Store,
        IVectorStore vectorStore,
        ConfigManager configManager,
        IndexService indexService,
        ILogger<BM25IndexController> logger,
        IServiceProvider? serviceProvider = null,
        IKnowledgeBaseStorageManager? kbStorageManager = null,
        ICatalogStore? catalogStore = null)
    {
        _bm25Store = bm25Store;
        _vectorStore = vectorStore;
        _configManager = configManager;
        _indexService = indexService;
        _logger = logger;
        _kbStorageManager = kbStorageManager ?? serviceProvider?.GetService<IKnowledgeBaseStorageManager>();
        _catalogStore = catalogStore ?? serviceProvider?.GetService<ICatalogStore>();
    }

    private async Task<string?> ResolveKbIdAsync(string? kbId)
    {
        if (kbId == "__root__" || kbId == "root" || kbId == "__global__")
            return null;

        if (!string.IsNullOrWhiteSpace(kbId) && kbId != "__all__")
            return kbId;

        if (_catalogStore != null)
        {
            var kbs = await _catalogStore.ListKnowledgeBasesAsync();
            return kbs.FirstOrDefault(k => k.Enabled)?.Id ?? kbs.FirstOrDefault()?.Id;
        }

        return null;
    }

    // ==================== 索引操作 ====================

    /// <summary>
    /// 独立重建 BM25 索引（支持指定知识库或全局）
    /// </summary>
    [HttpPost("index")]
    public async Task<ActionResult<IndexProgressResponse>> IndexAllDocuments([FromQuery] string? kbId = null)
    {
        var targetKbId = await ResolveKbIdAsync(kbId);
        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            _logger.LogInformation("执行知识库单库 BM25 全文索引重建: {KbId}", targetKbId);
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadWrite);
            var count = await lease.Store.RebuildFtsIndexAsync();
            return Ok(new IndexProgressResponse
            {
                TotalDocuments = (int)count,
                ProcessedDocuments = (int)count,
                ProgressPercent = 100,
                Message = $"知识库词法索引重建完成，共更新 {count} 个分块"
            });
        }

        if (LegacyIndexUnavailable()) return Conflict(new { error = "词法模式下旧 BM25 索引不可用，请使用词法索引任务。" });
        var activeJobs = _indexService.ActiveJobs;
        if (activeJobs.Any(j => j.Value.Status == IndexStatus.Running || j.Value.Status == IndexStatus.Pending))
        {
            return Conflict(new { error = "向量索引任务正在进行，请等待完成后再执行 BM25 独立重建，以避免数据不一致。" });
        }

        _logger.LogInformation("开始独立重建 BM25 索引");

        var config = _configManager.Load();
        var enabledSources = config.Sources
            .Where(s => s.Enabled)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = (await _vectorStore.GetAllFilesAsync())
            .Where(f => enabledSources.Contains(f.Source))
            .ToList();

        var fileIds = files.Select(f => f.Id).ToList();
        var fileTitleMap = files.ToDictionary(f => f.Id, f => f.Title ?? string.Empty);
        var allChunks = await _vectorStore.GetChunksByFileIdsAsync(fileIds);
        var chunksList = new List<(string chunkId, string title, string heading, string content)>();

        foreach (var chunk in allChunks)
        {
            if (!string.IsNullOrWhiteSpace(chunk.Content))
            {
                var title = fileTitleMap.GetValueOrDefault(chunk.FileId, string.Empty);
                var heading = chunk.HeadingPath ?? string.Empty;
                chunksList.Add((chunk.Id, title, heading, chunk.Content));
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

        await _bm25Store.ClearIndexAsync();

        var progress = new Progress<int>(p => { });
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
        if (LegacyIndexUnavailable()) return Conflict(new { error = "词法模式下旧 BM25 索引不可用。" });
        await _bm25Store.IndexDocumentAsync(chunkId, request.Content);
        return Ok(new { message = $"文档 {chunkId} 已索引" });
    }

    /// <summary>
    /// 清空索引
    /// </summary>
    [HttpDelete("index")]
    public async Task<ActionResult> ClearIndex([FromQuery] string? kbId = null)
    {
        var targetKbId = await ResolveKbIdAsync(kbId);
        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadWrite);
            await lease.Store.EnsureLexicalIndexAsync();
            return Ok(new { message = $"知识库 {targetKbId} 词法索引已重置" });
        }

        if (LegacyIndexUnavailable()) return Conflict(new { error = "词法模式下旧 BM25 索引不可用。" });
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
        [FromQuery] int topK = 10,
        [FromQuery] string? kbId = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "查询语句不能为空" });
        }

        var startTime = DateTime.UtcNow;
        var targetKbId = await ResolveKbIdAsync(kbId);
        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
            var snapshot = await lease.Store.RecallAsync(new RecallRequest(query, topK, UseLexicalTokenizer: true));
            var duration = (int)(DateTime.UtcNow - startTime).TotalMilliseconds;
            return Ok(new BM25SearchResponse
            {
                Query = query,
                TotalResults = snapshot.Items.Count,
                DurationMs = duration,
                Results = snapshot.Items.Select((r, index) => new BM25SearchResultItem
                {
                    ChunkId = r.ChunkId,
                    Content = r.Text,
                    Score = r.Score,
                    Rank = index + 1
                }).ToList()
            });
        }

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
    public async Task<ActionResult<BM25Summary>> GetSummary([FromQuery] string? kbId = null)
    {
        if (kbId == "__all__" && _catalogStore != null && _kbStorageManager != null)
        {
            var kbs = await _catalogStore.ListKnowledgeBasesAsync();
            int totalChunks = 0;
            int totalFiles = 0;
            int totalVocab = 0;
            double totalLengthSum = 0;
            int kbCount = 0;

            foreach (var kb in kbs.Where(k => k.Enabled))
            {
                try
                {
                    await using var lease = await _kbStorageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadOnly);
                    var stats = await lease.Store.GetLexicalStatsAsync();
                    totalChunks += stats.TotalChunks;
                    totalFiles += stats.TotalDocuments;
                    totalVocab += stats.TotalVocabularySize;
                    totalLengthSum += stats.AverageDocumentLength;
                    kbCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BM25IndexController] 获取知识库 {KbId} 词法统计失败", kb.Id);
                }
            }

            return Ok(new BM25Summary
            {
                TotalIndexedDocuments = totalChunks,
                TotalVocabularySize = totalVocab,
                AverageDocLength = kbCount > 0 ? Math.Round(totalLengthSum / kbCount, 2) : 200.0,
                TotalFiles = totalFiles,
                TotalChunks = totalChunks
            });
        }

        var targetKbId = await ResolveKbIdAsync(kbId);
        if (!string.IsNullOrEmpty(targetKbId) && _kbStorageManager != null)
        {
            await using var lease = await _kbStorageManager.AcquireLeaseAsync(targetKbId, StorageAccess.ReadOnly);
            var stats = await lease.Store.GetLexicalStatsAsync();
            return Ok(new BM25Summary
            {
                TotalIndexedDocuments = stats.TotalChunks,
                TotalVocabularySize = stats.TotalVocabularySize,
                AverageDocLength = stats.AverageDocumentLength,
                TotalFiles = stats.TotalDocuments,
                TotalChunks = stats.TotalChunks
            });
        }

        var legacyStats = await _bm25Store.GetStatsAsync();
        var sourceStats = (await _vectorStore.GetSourceStatsAsync()).ToList();

        return Ok(new BM25Summary
        {
            TotalIndexedDocuments = legacyStats.TotalDocuments,
            TotalVocabularySize = legacyStats.VocabularySize,
            AverageDocLength = legacyStats.AverageDocLength,
            TotalFiles = sourceStats.Sum(s => s.FileCount),
            TotalChunks = sourceStats.Sum(s => s.ChunkCount)
        });
    }

    private bool LegacyIndexUnavailable()
    {
        var config = _configManager.Load();
        return !config.Indexing.EmbeddingEnabled || config.Search.EnableLexical ||
            LexicalIndexCoordinator.HasPersistedStaleVectors(Path.Combine(config.DataPath ?? "data", "vectors.db"));
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
