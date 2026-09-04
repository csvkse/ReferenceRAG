using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 索引统计与管理 — /api/index
/// 合并原 VectorIndexController（统计/重建/清理部分）、VectorsController、DashboardController。
/// </summary>
[ApiController]
[Route("api/index")]
public class IndexController : ControllerBase
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly IBM25Store _bm25Store;
    private readonly IGraphStore _graphStore;
    private readonly IFileIndexPipeline _pipeline;
    private readonly IModelManager _modelManager;
    private readonly ConfigManager _configManager;
    private readonly IndexService _indexService;
    private readonly IQueryStatsService _queryStats;
    private readonly ILogger<IndexController> _logger;

    public IndexController(
        IVectorStore vectorStore,
        IEmbeddingService embeddingService,
        IBM25Store bm25Store,
        IGraphStore graphStore,
        IFileIndexPipeline pipeline,
        IModelManager modelManager,
        ConfigManager configManager,
        IndexService indexService,
        IQueryStatsService queryStats,
        ILogger<IndexController> logger)
    {
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _bm25Store = bm25Store;
        _graphStore = graphStore;
        _pipeline = pipeline;
        _modelManager = modelManager;
        _configManager = configManager;
        _indexService = indexService;
        _queryStats = queryStats;
        _logger = logger;
    }

    // ==================== 统计 ====================

    /// <summary>
    /// 综合统计：文件数、分块数、向量模型、源数量、平均查询时间。
    /// 合并原 /VectorIndex/summary + /Dashboard/stats。
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<IndexSummary>> GetSummary()
    {
        var stats = await _vectorStore.GetVectorStatsAsync();
        var config = _configManager.Load();
        var sourceNames = config.Sources.Select(s => s.Name).ToHashSet();

        var sourceStats = (await _vectorStore.GetSourceStatsAsync())
            .Where(s => sourceNames.Contains(s.Source))
            .ToList();

        var avgQueryTime = await _queryStats.GetAverageQueryTimeAsync();

        return Ok(new IndexSummary
        {
            CurrentModel = _embeddingService.ModelName,
            CurrentDimension = _embeddingService.Dimension,
            TotalFiles = sourceStats.Sum(s => s.FileCount),
            TotalChunks = sourceStats.Sum(s => s.ChunkCount),
            SourceCount = config.Sources.Count,
            AvgQueryTime = avgQueryTime,
            ModelStats = stats.Select(s => new ModelStat
            {
                ModelName = s.ModelName,
                Dimension = s.Dimension,
                VectorCount = s.VectorCount,
                StorageMB = s.StorageBytes / (1024.0 * 1024.0),
                IsCurrentModel = s.ModelName == _embeddingService.ModelName
            }).ToList()
        });
    }

    /// <summary>
    /// 所有模型的向量索引列表（含模型文件是否存在）。
    /// 合并原 /VectorIndex/models + /Vectors/stats。
    /// </summary>
    [HttpGet("models")]
    public async Task<ActionResult<List<VectorModelIndex>>> GetModels()
    {
        var stats = await _vectorStore.GetVectorStatsAsync();
        var currentModel = _embeddingService.ModelName;
        var currentDimension = _embeddingService.Dimension;
        var downloadedModels = _modelManager.GetDownloadedModels();

        var result = stats.Select(s => new VectorModelIndex
        {
            ModelName = s.ModelName,
            Dimension = s.Dimension,
            VectorCount = s.VectorCount,
            StorageBytes = s.StorageBytes,
            LastUpdated = s.LastUpdated,
            IsCurrentModel = s.ModelName == currentModel,
            DimensionMatch = s.Dimension == currentDimension,
            ModelExists = downloadedModels.Any(m => m.Name == s.ModelName)
        }).ToList();

        if (!result.Any(m => m.ModelName == currentModel))
            result.Add(new VectorModelIndex
            {
                ModelName = currentModel,
                Dimension = currentDimension,
                IsCurrentModel = true,
                DimensionMatch = true,
                ModelExists = downloadedModels.Any(m => m.Name == currentModel)
            });

        return Ok(result);
    }

    /// <summary>当前模型的向量索引状态。</summary>
    [HttpGet("models/current")]
    public async Task<ActionResult<VectorModelIndex>> GetCurrentModel()
    {
        var stats = await _vectorStore.GetVectorStatsAsync();
        var currentModel = _embeddingService.ModelName;
        var currentDimension = _embeddingService.Dimension;
        var currentStats = stats.FirstOrDefault(s => s.ModelName == currentModel);
        var downloadedModels = _modelManager.GetDownloadedModels();

        return Ok(new VectorModelIndex
        {
            ModelName = currentModel,
            Dimension = currentDimension,
            VectorCount = currentStats?.VectorCount ?? 0,
            StorageBytes = currentStats?.StorageBytes ?? 0,
            LastUpdated = currentStats?.LastUpdated,
            IsCurrentModel = true,
            DimensionMatch = true,
            ModelExists = downloadedModels.Any(m => m.Name == currentModel)
        });
    }

    // ==================== 删除 ====================

    /// <summary>删除指定模型的向量索引。</summary>
    [HttpDelete("models/{modelName}")]
    public async Task<ActionResult<DeleteResult>> DeleteModel(string modelName)
    {
        _logger.LogInformation("删除模型向量: {Model}", modelName);
        var deletedCount = await _vectorStore.DeleteVectorsByModelAsync(modelName);
        return Ok(new DeleteResult
        {
            ModelName = modelName,
            DeletedCount = deletedCount,
            Message = $"已删除 {deletedCount} 条向量"
        });
    }

    /// <summary>删除所有模型的向量索引（保留 files/chunks/BM25/图谱）。</summary>
    [HttpDelete("models")]
    public async Task<ActionResult<BulkDeleteResult>> DeleteAllModels()
    {
        _logger.LogWarning("删除所有模型向量索引");
        var stats = await _vectorStore.GetVectorStatsAsync();
        var results = new List<DeleteResult>();
        var total = 0;

        foreach (var stat in stats)
        {
            var deleted = await _vectorStore.DeleteVectorsByModelAsync(stat.ModelName);
            results.Add(new DeleteResult { ModelName = stat.ModelName, DeletedCount = deleted });
            total += deleted;
        }

        return Ok(new BulkDeleteResult { Results = results, TotalDeleted = total });
    }

    /// <summary>清空全部索引数据（files + chunks + 向量 + BM25 + 图谱）。</summary>
    [HttpDelete("data")]
    public async Task<ActionResult> DeleteAllData()
    {
        _logger.LogWarning("清空全部索引数据");

        var config = _configManager.Load();
        var existingSourceNames = config.Sources.Select(s => s.Name).ToHashSet();

        var allFiles = await _vectorStore.GetAllFilesAsync();
        var dbSources = allFiles
            .Select(f => f.Source)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .ToList();

        var processed = new List<string>();
        foreach (var source in dbSources.Union(existingSourceNames).Distinct())
        {
            await _pipeline.DeleteSourceAsync(source);
            processed.Add(source);
        }

        await _bm25Store.ClearIndexAsync();
        await _graphStore.ClearAllAsync();

        return Ok(new { message = $"已清空全部索引数据（{processed.Count} 个源）", sources = processed });
    }

    // ==================== 重建 ====================

    /// <summary>使用当前模型重建所有向量索引。</summary>
    [HttpPost("rebuild")]
    public async Task<ActionResult<RebuildJob>> Rebuild([FromBody] RebuildRequest? request = null)
    {
        var currentModel = _embeddingService.ModelName;
        var currentDimension = _embeddingService.Dimension;

        _logger.LogInformation("全量重建: 模型={Model}", currentModel);

        if (request?.DeleteExisting ?? true)
        {
            await _vectorStore.DeleteVectorsByModelAsync(currentModel);
            await _bm25Store.ClearIndexAsync();
            await _graphStore.ClearAllAsync();
        }

        var config = _configManager.Load();
        var sources = request?.Sources ?? config.Sources.Where(s => s.Enabled).Select(s => s.Name).ToList();

        if (sources.Count == 0)
            return BadRequest(new { error = "没有可用的源" });

        var job = await _indexService.StartIndexAsync(new IndexRequest { Sources = sources, Force = true });

        return Accepted(new RebuildJob
        {
            JobId = job.Id,
            Status = job.Status.ToString(),
            ModelName = currentModel,
            Dimension = currentDimension,
            Sources = sources,
            Message = "向量索引重建任务已启动"
        });
    }

    /// <summary>重建指定源的所有索引（向量 + BM25 + 图谱）。</summary>
    [HttpPost("rebuild/{sourceName}")]
    public async Task<ActionResult<RebuildJob>> RebuildSource(string sourceName)
    {
        var config = _configManager.Load();
        var source = config.Sources.FirstOrDefault(s => s.Name == sourceName);
        if (source == null)
            return NotFound(new { error = $"源 '{sourceName}' 不存在" });

        var currentModel = _embeddingService.ModelName;
        _logger.LogInformation("源重建: {Source}", sourceName);

        await _pipeline.DeleteSourceAsync(sourceName);
        var job = await _indexService.StartIndexAsync(new IndexRequest
        {
            Sources = new List<string> { sourceName },
            Force = true
        });

        return Accepted(new RebuildJob
        {
            JobId = job.Id,
            Status = job.Status.ToString(),
            ModelName = currentModel,
            Dimension = _embeddingService.Dimension,
            Sources = new List<string> { sourceName },
            Message = $"源 '{sourceName}' 重建任务已启动"
        });
    }

    // ==================== 清理 ====================

    /// <summary>清理孤立索引（向量 + BM25 + 图谱，含 heading 孤儿节点）。</summary>
    [HttpPost("cleanup")]
    public async Task<ActionResult<CleanupResult>> Cleanup()
    {
        _logger.LogInformation("清理孤立索引");

        var existingModels = new List<string> { _embeddingService.ModelName };
        var modelDeleted = await _vectorStore.DeleteOrphanedVectorsAsync(existingModels);
        var chunkDeleted = await _vectorStore.CleanupOrphanChunkVectorsAsync();

        // BM25 / Graph 现在各自独立 DB，需调用方提供有效 ID 集合
        var validChunkIds = await _vectorStore.GetAllChunkIdsAsync();
        var bm25Deleted = await _bm25Store.CleanupOrphanDocumentsAsync(validChunkIds);

        var allFiles = await _vectorStore.GetAllFilesAsync();
        var validFileNodeIds = allFiles.Select(f => f.Path.Replace('\\', '/').TrimStart('/'));
        var graphDeleted = await _graphStore.CleanupOrphanNodesAsync(validFileNodeIds);

        var total = modelDeleted + chunkDeleted + bm25Deleted + graphDeleted;
        _logger.LogInformation("清理完成（模型级: {M}, chunk级: {C}, BM25: {B}, 图谱: {G}）",
            modelDeleted, chunkDeleted, bm25Deleted, graphDeleted);

        return Ok(new CleanupResult
        {
            DeletedCount = total,
            Message = total > 0
                ? $"已清理孤立数据（向量: {modelDeleted + chunkDeleted}, BM25: {bm25Deleted}, 图谱: {graphDeleted}）"
                : "没有发现孤立数据"
        });
    }
}
