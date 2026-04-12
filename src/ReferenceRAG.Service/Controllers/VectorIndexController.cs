using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 向量索引管理 API - 统一的索引和向量管理接口
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class VectorIndexController : ControllerBase
{
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly IndexService _indexService;
    private readonly ConfigManager _configManager;
    private readonly ILogger<VectorIndexController> _logger;

    public VectorIndexController(
        IVectorStore vectorStore,
        IEmbeddingService embeddingService,
        IndexService indexService,
        ConfigManager configManager,
        ILogger<VectorIndexController> logger)
    {
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _indexService = indexService;
        _configManager = configManager;
        _logger = logger;
    }

    // ==================== 索引任务管理 ====================

    /// <summary>
    /// 启动增量索引任务（只处理有变化的文件）
    /// </summary>
    /// <param name="request">
    /// sources: 要索引的源列表（为空则索引所有启用的源）
    /// force: 是否强制重新索引（忽略内容哈希检测）
    /// </param>
    [HttpPost("index")]
    public async Task<ActionResult<IndexJobResponse>> StartIndex([FromBody] IndexJobRequest? request = null)
    {
        var config = _configManager.Load();
        var sources = request?.Sources 
            ?? config.Sources.Where(s => s.Enabled).Select(s => s.Name).ToList();

        if (sources.Count == 0)
        {
            return BadRequest(new { error = "没有可用的源" });
        }

        _logger.LogInformation("启动增量索引任务，源: {Sources}", string.Join(", ", sources));

        var job = await _indexService.StartIndexAsync(new IndexRequest
        {
            Sources = sources,
            Force = request?.Force ?? false
        });

        return Accepted(new IndexJobResponse
        {
            JobId = job.Id,
            Status = job.Status.ToString(),
            Sources = sources,
            Force = request?.Force ?? false,
            Message = "索引任务已启动"
        });
    }

    /// <summary>
    /// 获取所有活跃的索引任务
    /// </summary>
    [HttpGet("jobs")]
    public ActionResult<List<IndexJobResponse>> GetActiveJobs()
    {
        var jobs = _indexService.ActiveJobs.Values.Select(job => new IndexJobResponse
        {
            JobId = job.Id,
            Status = job.Status.ToString(),
            Sources = job.Request?.Sources ?? new List<string>(),
            Force = job.Request?.Force ?? false,
            TotalFiles = job.TotalFiles,
            ProcessedFiles = job.ProcessedFiles,
            CurrentFile = job.CurrentFile,
            Errors = job.Errors,
            ProgressPercent = (int)job.ProgressPercent,
            StartTime = job.StartTime,
            EndTime = job.EndTime,
            Duration = job.Duration.ToString(),
            ErrorMessage = job.ErrorMessage
        }).ToList();

        return Ok(jobs);
    }

    /// <summary>
    /// 获取索引任务状态
    /// </summary>
    [HttpGet("jobs/{indexId}")]
    public ActionResult<IndexJobResponse> GetJobStatus(string indexId)
    {
        var job = _indexService.GetStatus(indexId);
        if (job == null)
        {
            return NotFound(new { error = $"索引任务 '{indexId}' 不存在或已过期" });
        }

        return Ok(new IndexJobResponse
        {
            JobId = job.Id,
            Status = job.Status.ToString(),
            Sources = job.Request?.Sources ?? new List<string>(),
            Force = job.Request?.Force ?? false,
            TotalFiles = job.TotalFiles,
            ProcessedFiles = job.ProcessedFiles,
            CurrentFile = job.CurrentFile,
            Errors = job.Errors,
            ProgressPercent = (int)job.ProgressPercent,
            StartTime = job.StartTime,
            EndTime = job.EndTime,
            Duration = job.Duration.ToString(),
            ErrorMessage = job.ErrorMessage
        });
    }

    /// <summary>
    /// 停止索引任务
    /// </summary>
    [HttpPost("jobs/{indexId}/stop")]
    public async Task<ActionResult> StopJob(string indexId)
    {
        var stopped = await _indexService.StopIndexAsync(indexId);
        if (!stopped)
        {
            return NotFound(new { error = $"索引任务 '{indexId}' 不存在或已完成" });
        }
        return Ok(new { message = "索引任务已停止" });
    }

    // ==================== 向量索引重建 ====================

    /// <summary>
    /// 使用当前模型重建所有向量索引（删除旧向量，强制重新处理所有文件）
    /// </summary>
    [HttpPost("rebuild")]
    public async Task<ActionResult<RebuildJob>> RebuildIndex([FromBody] RebuildRequest? request = null)
    {
        var currentModel = _embeddingService.ModelName;
        var currentDimension = _embeddingService.Dimension;

        _logger.LogInformation("开始使用模型 '{Model}' (维度: {Dimension}) 重建向量索引", 
            currentModel, currentDimension);

        // 先删除当前模型的旧向量（如果存在）
        if (request?.DeleteExisting ?? true)
        {
            await _vectorStore.DeleteVectorsByModelAsync(currentModel);
        }

        // 获取所有源
        var config = _configManager.Load();
        var sources = request?.Sources ?? config.Sources.Where(s => s.Enabled).Select(s => s.Name).ToList();

        if (sources.Count == 0)
        {
            return BadRequest(new { error = "没有可用的源" });
        }

        // 启动重新索引任务
        var job = await _indexService.StartIndexAsync(new IndexRequest
        {
            Sources = sources,
            Force = true // 强制重建
        });

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

    /// <summary>
    /// 使用当前模型重建指定源的向量索引
    /// </summary>
    [HttpPost("rebuild/{sourceName}")]
    public async Task<ActionResult<RebuildJob>> RebuildSourceIndex(
        string sourceName, 
        [FromBody] RebuildRequest? request = null)
    {
        var config = _configManager.Load();
        var source = config.Sources.FirstOrDefault(s => s.Name == sourceName);

        if (source == null)
        {
            return NotFound(new { error = $"源 '{sourceName}' 不存在" });
        }

        var currentModel = _embeddingService.ModelName;
        var currentDimension = _embeddingService.Dimension;

        _logger.LogInformation("开始使用模型 '{Model}' (维度: {Dimension}) 重建源 '{Source}' 的向量索引", 
            currentModel, currentDimension, sourceName);

        // 启动重新索引任务
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
            Dimension = currentDimension,
            Sources = new List<string> { sourceName },
            Message = $"源 '{sourceName}' 的向量索引重建任务已启动"
        });
    }

    // ==================== 向量状态查询 ====================

    /// <summary>
    /// 获取所有模型的向量索引状态
    /// </summary>
    [HttpGet("models")]
    public async Task<ActionResult<List<VectorModelIndex>>> GetModels()
    {
        var stats = await _vectorStore.GetVectorStatsAsync();
        var currentModel = _embeddingService.ModelName;
        var currentDimension = _embeddingService.Dimension;

        var result = stats.Select(s => new VectorModelIndex
        {
            ModelName = s.ModelName,
            Dimension = s.Dimension,
            VectorCount = s.VectorCount,
            StorageBytes = s.StorageBytes,
            LastUpdated = s.LastUpdated,
            IsCurrentModel = s.ModelName == currentModel,
            DimensionMatch = s.Dimension == currentDimension
        }).ToList();

        // 如果当前模型不在列表中，添加一个空条目
        if (!result.Any(m => m.ModelName == currentModel))
        {
            result.Add(new VectorModelIndex
            {
                ModelName = currentModel,
                Dimension = currentDimension,
                VectorCount = 0,
                StorageBytes = 0,
                IsCurrentModel = true,
                DimensionMatch = true
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// 获取当前模型的向量索引状态
    /// </summary>
    [HttpGet("current")]
    public async Task<ActionResult<VectorModelIndex>> GetCurrentModel()
    {
        var stats = await _vectorStore.GetVectorStatsAsync();
        var currentModel = _embeddingService.ModelName;
        var currentDimension = _embeddingService.Dimension;

        var currentStats = stats.FirstOrDefault(s => s.ModelName == currentModel);

        var result = new VectorModelIndex
        {
            ModelName = currentModel,
            Dimension = currentDimension,
            VectorCount = currentStats?.VectorCount ?? 0,
            StorageBytes = currentStats?.StorageBytes ?? 0,
            LastUpdated = currentStats?.LastUpdated,
            IsCurrentModel = true,
            DimensionMatch = true
        };

        return Ok(result);
    }

    /// <summary>
    /// 获取向量索引统计摘要
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<IndexSummary>> GetSummary()
    {
        var stats = await _vectorStore.GetVectorStatsAsync();
        var files = await _vectorStore.GetAllFilesAsync();
        var fileList = files.ToList();

        var summary = new IndexSummary
        {
            CurrentModel = _embeddingService.ModelName,
            CurrentDimension = _embeddingService.Dimension,
            TotalFiles = fileList.Count,
            TotalChunks = fileList.Sum(f => f.ChunkCount),
            ModelStats = stats.Select(s => new ModelStat
            {
                ModelName = s.ModelName,
                Dimension = s.Dimension,
                VectorCount = s.VectorCount,
                StorageMB = s.StorageBytes / (1024.0 * 1024.0),
                IsCurrentModel = s.ModelName == _embeddingService.ModelName
            }).ToList()
        };

        return Ok(summary);
    }

    // ==================== 向量索引删除 ====================

    /// <summary>
    /// 删除指定模型的向量索引
    /// </summary>
    [HttpDelete("models/{modelName}")]
    public async Task<ActionResult<DeleteResult>> DeleteModelIndex(string modelName)
    {
        _logger.LogInformation("删除模型 '{ModelName}' 的向量索引", modelName);

        var deletedCount = await _vectorStore.DeleteVectorsByModelAsync(modelName);

        _logger.LogInformation("已删除模型 '{ModelName}' 的 {Count} 条向量", modelName, deletedCount);

        return Ok(new DeleteResult
        {
            ModelName = modelName,
            DeletedCount = deletedCount,
            Message = $"已删除 {deletedCount} 条向量"
        });
    }

    /// <summary>
    /// 删除所有向量索引（保留分段数据）
    /// </summary>
    [HttpDelete("all")]
    public async Task<ActionResult<BulkDeleteResult>> DeleteAllIndexes()
    {
        _logger.LogWarning("删除所有向量索引");

        var stats = await _vectorStore.GetVectorStatsAsync();
        var results = new List<DeleteResult>();
        var totalCount = 0;

        foreach (var stat in stats)
        {
            var deleted = await _vectorStore.DeleteVectorsByModelAsync(stat.ModelName);
            results.Add(new DeleteResult
            {
                ModelName = stat.ModelName,
                DeletedCount = deleted,
                Message = $"已删除 {deleted} 条向量"
            });
            totalCount += deleted;
        }

        _logger.LogInformation("已删除所有向量索引，共 {Count} 条", totalCount);

        return Ok(new BulkDeleteResult
        {
            Results = results,
            TotalDeleted = totalCount
        });
    }

    /// <summary>
    /// 清理孤立的向量索引（模型已不存在的索引）
    /// </summary>
    [HttpPost("cleanup")]
    public async Task<ActionResult<CleanupResult>> CleanupOrphanedIndexes()
    {
        _logger.LogInformation("清理孤立的向量索引");

        // 获取当前可用的模型列表
        var existingModels = new List<string> { _embeddingService.ModelName };

        var deletedCount = await _vectorStore.DeleteOrphanedVectorsAsync(existingModels);

        _logger.LogInformation("已清理 {Count} 条孤立向量", deletedCount);

        return Ok(new CleanupResult
        {
            DeletedCount = deletedCount,
            Message = deletedCount > 0 
                ? $"已清理 {deletedCount} 条孤立向量" 
                : "没有发现孤立向量"
        });
    }

    // ==================== 数据迁移 ====================

    /// <summary>
    /// 迁移旧版向量数据到新结构
    /// </summary>
    [HttpPost("migrate")]
    public ActionResult<MigrateResult> MigrateLegacyData()
    {
        // 迁移逻辑已在 SqliteVectorStore 初始化时自动执行
        return Ok(new MigrateResult
        {
            Success = true,
            Message = "数据迁移在服务启动时已自动完成，如需重新迁移请重启服务"
        });
    }
}

// ==================== 响应模型 ====================

/// <summary>
/// 向量模型索引状态
/// </summary>
public class VectorModelIndex
{
    /// <summary>
    /// 模型名称
    /// </summary>
    public string ModelName { get; set; } = "";

    /// <summary>
    /// 向量维度
    /// </summary>
    public int Dimension { get; set; }

    /// <summary>
    /// 向量数量
    /// </summary>
    public int VectorCount { get; set; }

    /// <summary>
    /// 存储字节数
    /// </summary>
    public long StorageBytes { get; set; }

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// 是否为当前使用的模型
    /// </summary>
    public bool IsCurrentModel { get; set; }

    /// <summary>
    /// 维度是否与当前模型匹配
    /// </summary>
    public bool DimensionMatch { get; set; }
}

/// <summary>
/// 删除结果
/// </summary>
public class DeleteResult
{
    public string ModelName { get; set; } = "";
    public int DeletedCount { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// 批量删除结果
/// </summary>
public class BulkDeleteResult
{
    public List<DeleteResult> Results { get; set; } = new();
    public int TotalDeleted { get; set; }
}

/// <summary>
/// 清理结果
/// </summary>
public class CleanupResult
{
    public int DeletedCount { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// 重建任务
/// </summary>
public class RebuildJob
{
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int Dimension { get; set; }
    public List<string> Sources { get; set; } = new();
    public string Message { get; set; } = "";
}

/// <summary>
/// 重建请求
/// </summary>
public class RebuildRequest
{
    /// <summary>
    /// 要重建的源列表（为空则重建所有）
    /// </summary>
    public List<string>? Sources { get; set; }

    /// <summary>
    /// 是否先删除现有向量
    /// </summary>
    public bool DeleteExisting { get; set; } = true;
}

/// <summary>
/// 迁移结果
/// </summary>
public class MigrateResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// 索引摘要
/// </summary>
public class IndexSummary
{
    public string CurrentModel { get; set; } = "";
    public int CurrentDimension { get; set; }
    public int TotalFiles { get; set; }
    public int TotalChunks { get; set; }
    public List<ModelStat> ModelStats { get; set; } = new();
}

/// <summary>
/// 模型统计
/// </summary>
public class ModelStat
{
    public string ModelName { get; set; } = "";
    public int Dimension { get; set; }
    public int VectorCount { get; set; }
    public double StorageMB { get; set; }
    public bool IsCurrentModel { get; set; }
}

/// <summary>
/// 索引任务请求
/// </summary>
public class IndexJobRequest
{
    /// <summary>
    /// 要索引的源列表（为空则索引所有启用的源）
    /// </summary>
    public List<string>? Sources { get; set; }

    /// <summary>
    /// 是否强制重新索引（忽略内容哈希检测）
    /// </summary>
    public bool Force { get; set; }
}

/// <summary>
/// 索引任务响应
/// </summary>
public class IndexJobResponse
{
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> Sources { get; set; } = new();
    public bool Force { get; set; }
    public string Message { get; set; } = "";

    // 进度信息
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int ProgressPercent { get; set; }
    public string? CurrentFile { get; set; }
    public int Errors { get; set; }

    // 时间信息
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Duration { get; set; }
    public string? ErrorMessage { get; set; }
}
