using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.ApiHost.Services;
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
    private readonly KnowledgeBaseJobCoordinator? _jobCoordinator;
    private readonly IIndexEventPublisher? _eventPublisher;

    public BM25IndexController(
        IBM25Store bm25Store,
        IVectorStore vectorStore,
        ConfigManager configManager,
        IndexService indexService,
        ILogger<BM25IndexController> logger,
        IServiceProvider? serviceProvider = null,
        IKnowledgeBaseStorageManager? kbStorageManager = null,
        ICatalogStore? catalogStore = null,
        KnowledgeBaseJobCoordinator? jobCoordinator = null,
        IIndexEventPublisher? eventPublisher = null)
    {
        _bm25Store = bm25Store;
        _vectorStore = vectorStore;
        _configManager = configManager;
        _indexService = indexService;
        _logger = logger;
        _kbStorageManager = kbStorageManager ?? serviceProvider?.GetService<IKnowledgeBaseStorageManager>();
        _catalogStore = catalogStore ?? serviceProvider?.GetService<ICatalogStore>();
        _jobCoordinator = jobCoordinator ?? serviceProvider?.GetService<KnowledgeBaseJobCoordinator>();
        _eventPublisher = eventPublisher ?? serviceProvider?.GetService<IIndexEventPublisher>();
    }

    // ==================== 索引操作 ====================

    /// <summary>
    /// 独立重建 BM25 索引（支持指定知识库或全局）
    /// </summary>
    [HttpPost("index")]
    public async Task<ActionResult<IndexProgressResponse>> IndexAllDocuments([FromQuery] string? kbId = null)
    {
        KnowledgeBaseScope scope;
        try
        {
            // 写接口：缺省作用域直接 400（重建索引必须知道动哪个库）。
            scope = await KnowledgeBaseScopeResolver.ResolveWriteAsync(_catalogStore, kbId);
        }
        catch (KnowledgeBaseScopeRequiredException ex)
        {
            return BadRequest(new { error = ex.Message, errorCode = "scope_required" });
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return NotFound(new { error = ex.Message, errorCode = "knowledge_base_not_found" });
        }

        if (_kbStorageManager != null && scope.Ids.Count > 0)
        {
            var targetKbId = scope.Ids[0];
            var jobId = Guid.NewGuid().ToString("N")[..16];
            var job = new JobDefinition(
                Id: jobId,
                Operation: "bm25_rebuild",
                KnowledgeBaseId: targetKbId,
                ExpectedIndexEpoch: 1,
                Status: JobStatus.Running,
                Phase: OperationPhase.Building,
                TotalUnits: scope.Ids.Count,
                ProcessedUnits: 0,
                IdempotencyKey: $"bm25:{targetKbId}:{DateTime.UtcNow.Ticks}",
                ErrorMessage: null,
                CreatedAt: DateTime.UtcNow,
                FinishedAt: null
            );

            if (_catalogStore != null)
            {
                await _catalogStore.SaveJobAsync(job);
            }

            var cts = new CancellationTokenSource();
            _jobCoordinator?.Register(jobId, cts);

            _ = Task.Run(async () =>
            {
                long total = 0;
                var touched = new List<string>();
                try
                {
                    await PublishJobEventAsync("IndexStarted", jobId, 0, scope.Ids.Count, "开始重建 BM25 词法倒排索引", 0);
                    await PublishJobEventAsync("ReindexJobStarted", jobId, 0, scope.Ids.Count, "开始重建 BM25 词法倒排索引", 0);

                    for (int idx = 0; idx < scope.Ids.Count; idx++)
                    {
                        var id = scope.Ids[idx];
                        if (cts.Token.IsCancellationRequested) break;
                        if (!await _kbStorageManager.ExistsAsync(id)) continue;

                        _logger.LogInformation("执行知识库 BM25 全文索引重建: {KbId}", id);
                        await using var lease = await _kbStorageManager.AcquireLeaseAsync(id, StorageAccess.ReadWrite, cts.Token);
                        total += await lease.Store.RebuildFtsIndexAsync(cts.Token);
                        touched.Add(id);

                        int processed = idx + 1;
                        var pct = (int)Math.Round((double)processed * 100 / scope.Ids.Count);
                        job = job with { ProcessedUnits = processed, TotalUnits = scope.Ids.Count };
                        if (_catalogStore != null) await _catalogStore.SaveJobAsync(job);

                        await PublishJobEventAsync("IndexProgress", jobId, processed, scope.Ids.Count, $"正在重建 {id} 词法索引", pct);
                        await PublishJobEventAsync("ReindexJobProgress", jobId, processed, scope.Ids.Count, $"正在重建 {id} 词法索引", pct);
                    }

                    if (cts.Token.IsCancellationRequested)
                    {
                        job = job with { Status = JobStatus.Cancelled, FinishedAt = DateTime.UtcNow };
                        if (_catalogStore != null) await _catalogStore.SaveJobAsync(job);
                        await PublishJobEventAsync("IndexCompleted", jobId, touched.Count, scope.Ids.Count, "BM25 重建已取消", 100);
                        await PublishJobEventAsync("ReindexJobCompleted", jobId, touched.Count, scope.Ids.Count, "BM25 重建已取消", 100);
                    }
                    else
                    {
                        job = job with { Status = JobStatus.Succeeded, FinishedAt = DateTime.UtcNow, ProcessedUnits = scope.Ids.Count };
                        if (_catalogStore != null) await _catalogStore.SaveJobAsync(job);
                        await PublishJobEventAsync("IndexCompleted", jobId, touched.Count, scope.Ids.Count, $"BM25 重建完成（{touched.Count} 个知识库）", 100);
                        await PublishJobEventAsync("ReindexJobCompleted", jobId, touched.Count, scope.Ids.Count, $"BM25 重建完成（{touched.Count} 个知识库）", 100);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BM25 重建异常失败: {JobId}", jobId);
                    job = job with { Status = JobStatus.Failed, ErrorMessage = ex.Message, FinishedAt = DateTime.UtcNow };
                    if (_catalogStore != null) await _catalogStore.SaveJobAsync(job);
                    await PublishJobEventAsync("IndexCompleted", jobId, 0, scope.Ids.Count, $"BM25 重建失败: {ex.Message}", 0);
                    await PublishJobEventAsync("ReindexJobCompleted", jobId, 0, scope.Ids.Count, $"BM25 重建失败: {ex.Message}", 0);
                }
                finally
                {
                    _jobCoordinator?.Unregister(jobId);
                    cts.Dispose();
                }
            });

            return Accepted(new IndexProgressResponse
            {
                JobId = jobId,
                TotalDocuments = scope.Ids.Count,
                ProcessedDocuments = 0,
                ProgressPercent = 0,
                Message = $"已启动 BM25 词法倒排索引重建任务（{scope.Ids.Count} 个知识库）",
                KnowledgeBaseIds = scope.Ids
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
        KnowledgeBaseScope scope;
        try
        {
            // 写接口：缺省作用域直接 400（清空索引必须知道动哪个库）。
            scope = await KnowledgeBaseScopeResolver.ResolveWriteAsync(_catalogStore, kbId);
        }
        catch (KnowledgeBaseScopeRequiredException ex)
        {
            return BadRequest(new { error = ex.Message, errorCode = "scope_required" });
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return NotFound(new { error = ex.Message, errorCode = "knowledge_base_not_found" });
        }

        if (_kbStorageManager != null && scope.Ids.Count > 0)
        {
            var touched = new List<string>();
            foreach (var id in scope.Ids)
            {
                if (!await _kbStorageManager.ExistsAsync(id)) continue;
                await using var lease = await _kbStorageManager.AcquireLeaseAsync(id, StorageAccess.ReadWrite);
                await lease.Store.EnsureLexicalIndexAsync();
                touched.Add(id);
            }
            return Ok(new
            {
                message = touched.Count == 1
                    ? $"知识库 {touched[0]} 词法索引已重置"
                    : $"词法索引已重置（{touched.Count} 个知识库）",
                knowledgeBaseIds = touched
            });
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

        KnowledgeBaseScope scope;
        try
        {
            scope = await KnowledgeBaseScopeResolver.ResolveReadAsync(_catalogStore, kbId);
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return NotFound(new { error = ex.Message, errorCode = "knowledge_base_not_found" });
        }

        if (LegacyIndexUnavailable() && (_kbStorageManager == null || scope.Ids.Count == 0))
            return Conflict(new { error = "词法模式下旧 BM25 索引不可用。" });

        if (_kbStorageManager != null && scope.Ids.Count > 0)
        {
            // 全量作用域：逐库召回后按 RRF 名次合并（与知识库检索同一口径）。
            if (scope.IsAll)
            {
                var merged = new List<(double Score, BM25SearchResultItem Item)>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in scope.Ids)
                {
                    if (!await _kbStorageManager.ExistsAsync(id)) continue;
                    await using var kbLease = await _kbStorageManager.AcquireLeaseAsync(id, StorageAccess.ReadOnly);
                    var kbSnapshot = await kbLease.Store.RecallAsync(new RecallRequest(query, topK, UseLexicalTokenizer: true));
                    foreach (var r in kbSnapshot.Items)
                    {
                        if (!seen.Add($"{id}::{r.ChunkId}")) continue;
                        merged.Add((r.Score, new BM25SearchResultItem
                        {
                            ChunkId = r.ChunkId,
                            Content = r.Text,
                            Score = r.Score,
                            Rank = 0
                        }));
                    }
                }
                var ordered = merged.OrderByDescending(m => m.Score).Take(topK).ToList();
                for (var i = 0; i < ordered.Count; i++) ordered[i].Item.Rank = i + 1;

                return Ok(new BM25SearchResponse
                {
                    Query = query,
                    TotalResults = ordered.Count,
                    DurationMs = (int)(DateTime.UtcNow - startTime).TotalMilliseconds,
                    Results = ordered.Select(o => o.Item).ToList()
                });
            }

            var targetKbId = scope.Ids[0];
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
        KnowledgeBaseScope scope;
        try
        {
            // 读接口：省略作用域 = 全部启用库（此前被解释成「第一个库」）。
            scope = await KnowledgeBaseScopeResolver.ResolveReadAsync(_catalogStore, kbId);
        }
        catch (KnowledgeBaseScopeNotFoundException ex)
        {
            return NotFound(new { error = ex.Message, errorCode = "knowledge_base_not_found" });
        }

        if (_kbStorageManager != null && scope.Ids.Count > 0)
        {
            int totalChunks = 0;
            int totalFiles = 0;
            int totalVocab = 0;
            // 平均长度必须按分块数加权：各库分块数可能相差一个数量级，
            // 对「平均值」再取算术平均得到的是另一个量（Simpson 型聚合偏差）。
            double weightedLengthSum = 0;
            var perKb = new List<BM25SummaryPerKb>();

            foreach (var id in scope.Ids)
            {
                try
                {
                    if (!await _kbStorageManager.ExistsAsync(id)) continue;
                    await using var lease = await _kbStorageManager.AcquireLeaseAsync(id, StorageAccess.ReadOnly);
                    var stats = await lease.Store.GetLexicalStatsAsync();

                    totalChunks += stats.TotalChunks;
                    totalFiles += stats.TotalDocuments;
                    totalVocab += stats.TotalVocabularySize;
                    weightedLengthSum += stats.TotalChunks * stats.AverageDocumentLength;

                    perKb.Add(new BM25SummaryPerKb(id, stats.TotalChunks, stats.TotalDocuments,
                        stats.TotalVocabularySize, stats.AverageDocumentLength));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BM25IndexController] 获取知识库 {KbId} 词法统计失败", id);
                }
            }

            return Ok(new BM25Summary
            {
                TotalIndexedDocuments = totalChunks,
                TotalVocabularySize = totalVocab,
                AverageDocLength = totalChunks > 0
                    ? Math.Round(weightedLengthSum / totalChunks, 2)
                    : 200.0,
                TotalFiles = totalFiles,
                TotalChunks = totalChunks,
                Scope = scope.KindText,
                KnowledgeBaseIds = scope.Ids,
                KnowledgeBases = perKb
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

    private async Task PublishJobEventAsync(string eventName, string jobId, int processed, int total, string message, int percent)
    {
        if (_eventPublisher == null) return;
        try
        {
            var payload = new
            {
                jobId,
                status = percent >= 100 ? "Completed" : "Running",
                phase = "Building",
                processedFiles = processed,
                totalFiles = total,
                progressPercent = percent,
                percent = (double)percent,
                message,
                currentFile = message
            };
            await _eventPublisher.PublishAsync(eventName, payload);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "发布 BM25 作业事件失败（可忽略）: {Msg}", ex.Message);
        }
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
    public string? JobId { get; set; }
    public int TotalDocuments { get; set; }
    public int ProcessedDocuments { get; set; }
    public int ProgressPercent { get; set; }
    public int TotalTerms { get; set; }
    public string Message { get; set; } = string.Empty;
    /// <summary>本次实际重建的库（全量作用域下可能是多个）。</summary>
    public IReadOnlyList<string>? KnowledgeBaseIds { get; set; }
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

    /// <summary>本次统计的作用域：all / single。</summary>
    public string Scope { get; set; } = "single";
    /// <summary>参与统计的库（全量时是全部启用库）。</summary>
    public IReadOnlyList<string> KnowledgeBaseIds { get; set; } = Array.Empty<string>();
    /// <summary>逐库明细：聚合值可被拆解核对。</summary>
    public List<BM25SummaryPerKb> KnowledgeBases { get; set; } = new();
}

public class BM25SummaryPerKb
{
    public BM25SummaryPerKb() { }
    public BM25SummaryPerKb(string knowledgeBaseId, int totalChunks, int totalDocuments,
        int totalVocabularySize, double averageDocLength)
    {
        KnowledgeBaseId = knowledgeBaseId;
        TotalChunks = totalChunks;
        TotalDocuments = totalDocuments;
        TotalVocabularySize = totalVocabularySize;
        AverageDocLength = averageDocLength;
    }

    public string KnowledgeBaseId { get; set; } = "";
    public int TotalChunks { get; set; }
    public int TotalDocuments { get; set; }
    public int TotalVocabularySize { get; set; }
    public double AverageDocLength { get; set; }
}
