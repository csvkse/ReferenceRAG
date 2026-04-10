using Microsoft.AspNetCore.Mvc;
using ObsidianRAG.Service.Services;

namespace ObsidianRAG.Service.Controllers;

/// <summary>
/// 索引管理 API（已废弃，请使用 /api/VectorIndex）
/// </summary>
[Obsolete("请使用 /api/VectorIndex 接口")]
[ApiController]
[Route("api/[controller]")]
public class IndexController : ControllerBase
{
    private readonly IndexService _indexService;

    public IndexController(IndexService indexService)
    {
        _indexService = indexService;
    }

    /// <summary>
    /// 启动索引任务（已废弃，请使用 POST /api/VectorIndex/index）
    /// </summary>
    [HttpPost("start")]
    [Obsolete("请使用 POST /api/VectorIndex/index")]
    public async Task<ActionResult> StartIndex([FromBody] IndexRequest request)
    {
        // 转发到新的 API 格式
        var job = await _indexService.StartIndexAsync(request);
        return Accepted($"/api/VectorIndex/jobs/{job.Id}", new
        {
            jobId = job.Id,
            status = job.Status.ToString(),
            sources = request.Sources,
            force = request.Force,
            message = "索引任务已启动（此接口已废弃，请使用 POST /api/VectorIndex/index）"
        });
    }

    /// <summary>
    /// 获取索引状态（已废弃，请使用 GET /api/VectorIndex/jobs/{indexId}）
    /// </summary>
    [HttpGet("{indexId}/status")]
    [Obsolete("请使用 GET /api/VectorIndex/jobs/{indexId}")]
    public ActionResult GetStatus(string indexId)
    {
        var job = _indexService.GetStatus(indexId);
        if (job == null)
        {
            return NotFound(new { error = $"索引任务 '{indexId}' 不存在或已过期" });
        }
        return Ok(new
        {
            jobId = job.Id,
            status = job.Status.ToString(),
            totalFiles = job.TotalFiles,
            processedFiles = job.ProcessedFiles,
            progressPercent = job.ProgressPercent,
            currentFile = job.CurrentFile,
            errors = job.Errors,
            startTime = job.StartTime,
            endTime = job.EndTime,
            duration = job.Duration.ToString(),
            errorMessage = job.ErrorMessage,
            _deprecated = "此接口已废弃，请使用 GET /api/VectorIndex/jobs/{indexId}"
        });
    }

    /// <summary>
    /// 获取所有活跃索引任务（已废弃，请使用 GET /api/VectorIndex/jobs）
    /// </summary>
    [HttpGet("active")]
    [Obsolete("请使用 GET /api/VectorIndex/jobs")]
    public ActionResult GetActiveJobs()
    {
        var jobs = _indexService.ActiveJobs.Values.Select(job => new
        {
            jobId = job.Id,
            status = job.Status.ToString(),
            totalFiles = job.TotalFiles,
            processedFiles = job.ProcessedFiles,
            progressPercent = job.ProgressPercent,
            startTime = job.StartTime,
            _deprecated = "此接口已废弃，请使用 GET /api/VectorIndex/jobs"
        }).ToList();

        return Ok(jobs);
    }

    /// <summary>
    /// 停止索引任务（已废弃，请使用 POST /api/VectorIndex/jobs/{indexId}/stop）
    /// </summary>
    [HttpPost("{indexId}/stop")]
    [Obsolete("请使用 POST /api/VectorIndex/jobs/{indexId}/stop")]
    public async Task<ActionResult> StopIndex(string indexId)
    {
        var stopped = await _indexService.StopIndexAsync(indexId);
        if (!stopped)
        {
            return NotFound(new { error = $"索引任务 '{indexId}' 不存在或已完成" });
        }
        return Ok(new
        {
            message = "索引任务已停止",
            _deprecated = "此接口已废弃，请使用 POST /api/VectorIndex/jobs/{indexId}/stop"
        });
    }
}
