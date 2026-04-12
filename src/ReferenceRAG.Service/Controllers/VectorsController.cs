using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 向量统计与管理 API
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class VectorsController : ControllerBase
{
    private readonly IVectorStore _vectorStore;
    private readonly IModelManager _modelManager;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<VectorsController> _logger;

    public VectorsController(
        IVectorStore vectorStore,
        IModelManager modelManager,
        IEmbeddingService embeddingService,
        ILogger<VectorsController> logger)
    {
        _vectorStore = vectorStore;
        _modelManager = modelManager;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有模型的向量统计
    /// </summary>
    [HttpGet("stats")]
    public async Task<ActionResult<List<VectorStats>>> GetAllStats(CancellationToken cancellationToken)
    {
        var stats = await _vectorStore.GetVectorStatsAsync(cancellationToken);

        // 补充模型是否存在的信息
        var downloadedModels = _modelManager.GetDownloadedModels();
        foreach (var stat in stats)
        {
            stat.ModelExists = downloadedModels.Any(m => m.Name == stat.ModelName);
        }

        return Ok(stats);
    }

    /// <summary>
    /// 获取指定模型的向量统计
    /// </summary>
    [HttpGet("stats/{modelName}")]
    public async Task<ActionResult<VectorStats>> GetStatsByModel(string modelName, CancellationToken cancellationToken)
    {
        var stats = await _vectorStore.GetVectorStatsAsync(cancellationToken);
        var modelStat = stats.FirstOrDefault(s => s.ModelName == modelName);

        if (modelStat == null)
        {
            return NotFound(new { error = $"未找到模型 {modelName} 的向量数据" });
        }

        // 补充模型是否存在的信息
        var downloadedModels = _modelManager.GetDownloadedModels();
        modelStat.ModelExists = downloadedModels.Any(m => m.Name == modelName);

        return Ok(modelStat);
    }

    /// <summary>
    /// 删除指定模型的向量
    /// </summary>
    [HttpDelete("model/{modelName}")]
    public async Task<ActionResult> DeleteByModel(string modelName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(modelName))
        {
            return BadRequest(new { error = "模型名称不能为空" });
        }

        // 不允许删除当前正在使用的模型的向量
        if (_embeddingService.ModelName == modelName)
        {
            return BadRequest(new { error = "无法删除当前正在使用的模型的向量" });
        }

        _logger.LogInformation("开始删除模型 {ModelName} 的向量数据", modelName);

        var deletedCount = await _vectorStore.DeleteVectorsByModelAsync(modelName, cancellationToken);

        _logger.LogInformation("已删除模型 {ModelName} 的 {Count} 条向量数据", modelName, deletedCount);

        return Ok(new { message = $"已删除模型 {modelName} 的 {deletedCount} 条向量数据", deletedCount });
    }

    /// <summary>
    /// 删除无关联模型的向量（孤立项清理）
    /// </summary>
    [HttpDelete("orphaned")]
    public async Task<ActionResult> DeleteOrphaned(CancellationToken cancellationToken)
    {
        var downloadedModels = _modelManager.GetDownloadedModels();
        var existingModelNames = downloadedModels.Select(m => m.Name);

        _logger.LogInformation("开始清理孤立项向量，当前有效模型: {Models}", string.Join(", ", existingModelNames));

        var deletedCount = await _vectorStore.DeleteOrphanedVectorsAsync(existingModelNames, cancellationToken);

        _logger.LogInformation("已清理 {Count} 条孤立项向量数据", deletedCount);

        return Ok(new { message = $"已清理 {deletedCount} 条孤立项向量数据", deletedCount });
    }
}
