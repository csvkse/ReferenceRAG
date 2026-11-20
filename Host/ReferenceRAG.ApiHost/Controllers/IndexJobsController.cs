using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Service.Controllers;

/// <summary>
/// 索引任务管理 — /api/index/jobs
/// 聚合传统索引任务与知识库后台长时任务（适配器模式）
/// </summary>
[ApiController]
[Route("api/index/jobs")]
public class IndexJobsController : ControllerBase
{
    private readonly IndexService _indexService;
    private readonly ILogger<IndexJobsController> _logger;
    private readonly ICatalogStore? _catalogStore;
    private readonly IKnowledgeBaseService? _kbService;
    private readonly KnowledgeBaseJobCoordinator? _coordinator;

    public IndexJobsController(
        IndexService indexService,
        ILogger<IndexJobsController> logger,
        IServiceProvider? serviceProvider = null,
        ICatalogStore? catalogStore = null,
        IKnowledgeBaseService? kbService = null,
        KnowledgeBaseJobCoordinator? coordinator = null)
    {
        _indexService = indexService;
        _logger = logger;
        _catalogStore = catalogStore ?? serviceProvider?.GetService<ICatalogStore>();
        _kbService = kbService ?? serviceProvider?.GetService<IKnowledgeBaseService>();
        _coordinator = coordinator ?? serviceProvider?.GetService<KnowledgeBaseJobCoordinator>();
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
        ErrorMessage = job.ErrorMessage,
        Phase = job.StateMachine?.CurrentPhase.ToString() ?? job.Status.ToString(),
        TargetResource = job.Request?.Sources?.Count == 1 ? $"source:{job.Request.Sources[0]}" : "global",
        JobType = job.Request?.Rechunk == true ? "Rechunk" : (job.Request?.VectorOnly == true ? "VectorOnly" : (job.Request?.Force == true ? "FullRebuild" : "Incremental"))
    };

    private static IndexJobResponse MapKbJobToResponse(JobDefinition job)
    {
        var isFinished = job.Status is JobStatus.Succeeded or JobStatus.PartiallySucceeded or JobStatus.Failed or JobStatus.Cancelled;
        var statusStr = job.Status switch
        {
            JobStatus.Queued => "Pending",
            JobStatus.Running => "Running",
            JobStatus.Cancelling => "Running",
            JobStatus.Succeeded => "Completed",
            JobStatus.PartiallySucceeded => "Completed",
            JobStatus.Failed => "Failed",
            JobStatus.Cancelled => "Cancelled",
            _ => job.Status.ToString()
        };

        var jobType = job.Operation switch
        {
            "bm25_rebuild" => "BM25 倒排",
            "graph_rebuild" => "知识图谱",
            "sync" => "文档同步",
            "reindex" => "向量重构",
            "rechunk" => "重新切片",
            _ => job.Operation
        };

        var pct = job.TotalUnits > 0
            ? (int)Math.Min(100, Math.Round((double)job.ProcessedUnits * 100 / job.TotalUnits))
            : (isFinished ? 100 : 0);

        var duration = (job.FinishedAt ?? DateTime.UtcNow) - job.CreatedAt;
        var durationStr = duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s"
            : duration.TotalMinutes >= 1
                ? $"{duration.Minutes}m {duration.Seconds}s"
                : $"{duration.TotalSeconds:F1}s";

        var currentMsg = job.Operation switch
        {
            "bm25_rebuild" => $"正在重建词法倒排索引 ({job.ProcessedUnits}/{job.TotalUnits})",
            "graph_rebuild" => $"正在提取知识图谱关系 ({job.ProcessedUnits}/{Math.Max(job.ProcessedUnits, job.TotalUnits)})",
            "sync" => $"正在同步文档 ({job.ProcessedUnits}/{job.TotalUnits})",
            "reindex" => $"正在重构向量 ({job.ProcessedUnits}/{job.TotalUnits})",
            _ => job.Phase.ToString()
        };

        return new IndexJobResponse
        {
            JobId = job.Id,
            Status = statusStr,
            Sources = new List<string> { string.IsNullOrEmpty(job.KnowledgeBaseId) ? "全量知识库" : job.KnowledgeBaseId },
            Force = false,
            TotalFiles = job.TotalUnits,
            ProcessedFiles = job.ProcessedUnits,
            CurrentFile = currentMsg,
            Errors = string.IsNullOrEmpty(job.ErrorMessage) ? 0 : 1,
            ProgressPercent = pct,
            StartTime = job.CreatedAt,
            EndTime = job.FinishedAt,
            Duration = durationStr,
            ErrorMessage = job.ErrorMessage,
            Phase = job.Phase.ToString(),
            TargetResource = $"kb:{job.KnowledgeBaseId}",
            JobType = jobType
        };
    }

    /// <summary>启动索引任务（增量或强制）</summary>
    [HttpPost]
    public async Task<ActionResult<IndexJobResponse>> StartJob([FromBody] IndexJobRequest? request = null)
    {
        var config = request?.Sources?.Count > 0
            ? request.Sources
            : null;

        _logger.LogInformation("启动索引任务，sources: {S}", string.Join(",", request?.Sources ?? new()));

        var job = await _indexService.StartIndexAsync(new IndexRequest
        {
            Sources = request?.Sources,
            Force = request?.Force ?? false,
            VectorOnly = request?.VectorOnly ?? false,
            Rechunk = request?.Rechunk ?? false
        });

        return Accepted(ToResponse(job));
    }

    /// <summary>获取活跃任务列表（包含向量索引任务与知识库后台长时任务）</summary>
    [HttpGet]
    public async Task<ActionResult<List<IndexJobResponse>>> GetActive()
    {
        var list = _indexService.ActiveJobs.Values.Select(ToResponse).ToList();
        if (_catalogStore != null)
        {
            var kbJobs = await _catalogStore.ListJobsAsync();
            var activeKb = kbJobs
                .Where(j => j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.WaitingForInput or JobStatus.Cancelling)
                .Select(MapKbJobToResponse);
            list.AddRange(activeKb);
        }
        return Ok(list);
    }

    /// <summary>获取所有任务（活跃 + 历史）</summary>
    [HttpGet("all")]
    public async Task<ActionResult<AllJobsResponse>> GetAll()
    {
        var active = _indexService.ActiveJobs.Values.Select(ToResponse).ToList();
        var history = _indexService.CompletedJobs.Select(ToResponse).ToList();
        if (_catalogStore != null)
        {
            var kbJobs = await _catalogStore.ListJobsAsync();
            active.AddRange(kbJobs
                .Where(j => j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.WaitingForInput or JobStatus.Cancelling)
                .Select(MapKbJobToResponse));
            history.AddRange(kbJobs
                .Where(j => j.Status is JobStatus.Succeeded or JobStatus.PartiallySucceeded or JobStatus.Failed or JobStatus.Cancelled)
                .Select(MapKbJobToResponse));
        }

        return Ok(new AllJobsResponse
        {
            ActiveJobs = active,
            CompletedJobs = history.OrderByDescending(j => j.EndTime ?? j.StartTime).ToList()
        });
    }

    /// <summary>获取历史任务</summary>
    [HttpGet("history")]
    public async Task<ActionResult<List<IndexJobResponse>>> GetHistory()
    {
        var history = _indexService.CompletedJobs.Select(ToResponse).ToList();
        if (_catalogStore != null)
        {
            var kbJobs = await _catalogStore.ListJobsAsync();
            history.AddRange(kbJobs
                .Where(j => j.Status is JobStatus.Succeeded or JobStatus.PartiallySucceeded or JobStatus.Failed or JobStatus.Cancelled)
                .Select(MapKbJobToResponse));
        }
        return Ok(history.OrderByDescending(j => j.EndTime ?? j.StartTime).Take(50).ToList());
    }

    /// <summary>清空历史记录</summary>
    [HttpDelete("history")]
    public async Task<ActionResult> ClearHistory()
    {
        _indexService.ClearCompletedJobs();
        if (_catalogStore != null)
        {
            await _catalogStore.PruneJobsAsync(null, TimeSpan.Zero);
        }
        return Ok(new { message = "已清空历史记录" });
    }

    /// <summary>获取指定任务状态</summary>
    [HttpGet("{jobId}")]
    public async Task<ActionResult<IndexJobResponse>> GetJob(string jobId)
    {
        var job = _indexService.GetStatus(jobId);
        if (job != null) return Ok(ToResponse(job));

        if (_catalogStore != null)
        {
            var kbJob = await _catalogStore.GetJobAsync(jobId);
            if (kbJob != null) return Ok(MapKbJobToResponse(kbJob));
        }

        return NotFound(new { error = $"任务 '{jobId}' 不存在或已过期" });
    }

    /// <summary>取消任务</summary>
    [HttpPost("{jobId}/cancel")]
    public async Task<ActionResult> CancelJob(string jobId)
    {
        var stopped = await _indexService.StopIndexAsync(jobId);
        if (stopped) return Ok(new { message = "任务已取消" });

        if (_kbService != null)
        {
            var kbCancelled = await _kbService.CancelJobAsync(jobId);
            if (kbCancelled) return Ok(new { message = "任务已取消" });
        }

        return NotFound(new { error = $"任务 '{jobId}' 不存在或已完成" });
    }
}
