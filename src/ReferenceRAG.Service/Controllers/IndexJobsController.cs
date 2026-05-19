using Microsoft.AspNetCore.Mvc;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 索引任务管理 — /api/index/jobs
/// </summary>
[ApiController]
[Route("api/index/jobs")]
public class IndexJobsController : ControllerBase
{
    private readonly IndexService _indexService;
    private readonly ILogger<IndexJobsController> _logger;

    public IndexJobsController(IndexService indexService, ILogger<IndexJobsController> logger)
    {
        _indexService = indexService;
        _logger = logger;
    }

    private IndexJobResponse ToResponse(IndexJob job) => new()
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
    };

    /// <summary>启动索引任务（增量或强制）</summary>
    [HttpPost]
    public async Task<ActionResult<IndexJobResponse>> StartJob([FromBody] IndexJobRequest? request = null)
    {
        var config = request?.Sources?.Count > 0
            ? request.Sources
            : null; // resolved inside IndexService

        _logger.LogInformation("启动索引任务，sources: {S}", string.Join(",", request?.Sources ?? new()));

        var job = await _indexService.StartIndexAsync(new IndexRequest
        {
            Sources = request?.Sources,
            Force = request?.Force ?? false,
            VectorOnly = request?.VectorOnly ?? false
        });

        return Accepted(ToResponse(job));
    }

    /// <summary>获取活跃任务列表</summary>
    [HttpGet]
    public ActionResult<List<IndexJobResponse>> GetActive() =>
        Ok(_indexService.ActiveJobs.Values.Select(ToResponse).ToList());

    /// <summary>获取所有任务（活跃 + 历史）</summary>
    [HttpGet("all")]
    public ActionResult<AllJobsResponse> GetAll() => Ok(new AllJobsResponse
    {
        ActiveJobs = _indexService.ActiveJobs.Values
            .Select(ToResponse).ToList(),
        CompletedJobs = _indexService.CompletedJobs
            .OrderByDescending(j => j.EndTime ?? j.StartTime)
            .Select(ToResponse).ToList()
    });

    /// <summary>获取历史任务</summary>
    [HttpGet("history")]
    public ActionResult<List<IndexJobResponse>> GetHistory() =>
        Ok(_indexService.CompletedJobs.Select(ToResponse).ToList());

    /// <summary>清空历史记录</summary>
    [HttpDelete("history")]
    public ActionResult ClearHistory()
    {
        _indexService.ClearCompletedJobs();
        return Ok(new { message = "已清空历史记录" });
    }

    /// <summary>获取指定任务状态</summary>
    [HttpGet("{jobId}")]
    public ActionResult<IndexJobResponse> GetJob(string jobId)
    {
        var job = _indexService.GetStatus(jobId);
        return job == null
            ? NotFound(new { error = $"任务 '{jobId}' 不存在或已过期" })
            : Ok(ToResponse(job));
    }

    /// <summary>取消任务</summary>
    [HttpPost("{jobId}/cancel")]
    public async Task<ActionResult> CancelJob(string jobId)
    {
        var stopped = await _indexService.StopIndexAsync(jobId);
        return stopped
            ? Ok(new { message = "任务已取消" })
            : NotFound(new { error = $"任务 '{jobId}' 不存在或已完成" });
    }
}
