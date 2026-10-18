using Microsoft.Extensions.Logging;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Service.Hubs;
using ReferenceRAG.Service.Services;
using System.Diagnostics;

namespace ReferenceRAG.Business.Features.Indexing.StateMachine;

/// <summary>
/// 索引任务状态机：驱动任务生命周期、状态跃迁校验、全阶段细粒度进度计算与实时广播。
/// 彻底消除“一直卡在 0% 0/1 文件”假死现象。
/// </summary>
public class IndexJobStateMachine
{
    private readonly IndexJob _job;
    private readonly IIndexEventPublisher? _events;
    private readonly ILogger? _logger;
    private readonly object _stateLock = new();

    public IndexJobStateMachine(
        IndexJob job,
        IIndexEventPublisher? events = null,
        ILogger? logger = null)
    {
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _job.StateMachine = this;
        _events = events;
        _logger = logger;
        CurrentPhase = IndexJobPhase.Queued;
        StartTime = DateTime.UtcNow;
        PhaseStartTime = DateTime.UtcNow;
    }

    public string JobId => _job.Id;
    public IndexJob Job => _job;
    public string TargetResource => GetTargetResource();
    public string JobType => GetJobType();
    public IndexJobPhase CurrentPhase { get; private set; }
    public DateTime StartTime { get; }
    public DateTime PhaseStartTime { get; private set; }
    public string StatusMessage { get; private set; } = string.Empty;

    public int TotalFiles { get; set; }
    public int PreparedFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public int ErrorsCount { get; set; }

    public int TotalChunks { get; set; }
    public int ProcessedChunks { get; set; }
    public int TotalVectors { get; set; }

    public int CurrentBatch { get; set; }
    public int TotalBatches { get; set; }

    public string CurrentFile { get; set; } = string.Empty;
    public List<string> CurrentFiles { get; set; } = new();

    /// <summary>
    /// 执行状态跃迁并进行合法性校验
    /// </summary>
    public bool TransitionTo(IndexJobPhase nextPhase, string? message = null)
    {
        lock (_stateLock)
        {
            if (CurrentPhase is IndexJobPhase.Completed or IndexJobPhase.Faulted or IndexJobPhase.Cancelled)
            {
                _logger?.LogDebug("任务 {JobId} 已处于终态 {Phase}，忽略向 {NextPhase} 的跃迁", JobId, CurrentPhase, nextPhase);
                return false;
            }

            var previousPhase = CurrentPhase;
            CurrentPhase = nextPhase;
            PhaseStartTime = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(message))
            {
                StatusMessage = message;
                _job.CurrentFile = message;
            }

            SyncJobState(nextPhase);

            _logger?.LogInformation("任务 {JobId} 状态跃迁: {Prev} -> {Next} ({Msg})",
                JobId, previousPhase, nextPhase, message ?? string.Empty);

            PublishProgressEvent();
            return true;
        }
    }

    /// <summary>
    /// 全局综合进度平滑计算算法：
    /// Scanning: 2%
    /// Preparing (Phase 1): 2% -> 20%
    /// Embedding (Phase 2): 20% -> 85%（随批次与 chunk 实时推进）
    /// Writing (Stage 3): 85% -> 88%
    /// Finalizing (Phase 3): 88% -> 99%
    /// Completed: 100%
    /// </summary>
    public double CalculateProgress()
    {
        switch (CurrentPhase)
        {
            case IndexJobPhase.Queued:
                return 0.0;

            case IndexJobPhase.Scanning:
                return 2.0;

            case IndexJobPhase.Preparing:
                if (TotalFiles <= 0) return 5.0;
                var prepRatio = Math.Clamp((double)(PreparedFiles + SkippedFiles) / TotalFiles, 0.0, 1.0);
                return 2.0 + (18.0 * prepRatio);

            case IndexJobPhase.Embedding:
                if (TotalBatches > 0)
                {
                    var batchRatio = Math.Clamp((double)CurrentBatch / TotalBatches, 0.0, 1.0);
                    return 20.0 + (65.0 * batchRatio);
                }
                if (TotalChunks > 0)
                {
                    var chunkRatio = Math.Clamp((double)ProcessedChunks / TotalChunks, 0.0, 1.0);
                    return 20.0 + (65.0 * chunkRatio);
                }
                return 25.0;

            case IndexJobPhase.Writing:
                return 86.0;

            case IndexJobPhase.Finalizing:
                if (TotalFiles <= 0) return 90.0;
                var finRatio = Math.Clamp((double)ProcessedFiles / TotalFiles, 0.0, 1.0);
                return 88.0 + (11.0 * finRatio);

            case IndexJobPhase.Completed:
                return 100.0;

            case IndexJobPhase.Faulted:
            case IndexJobPhase.Cancelled:
                return TotalFiles > 0 ? (double)(ProcessedFiles + SkippedFiles) / TotalFiles * 100.0 : 0.0;

            default:
                return 0.0;
        }
    }

    /// <summary>
    /// Phase 1 准备与分块进度上报
    /// </summary>
    public void ReportPreparingProgress(string currentFile, int preparedFiles, int totalFiles)
    {
        lock (_stateLock)
        {
            CurrentFile = currentFile;
            PreparedFiles = preparedFiles;
            TotalFiles = Math.Max(TotalFiles, totalFiles);
            StatusMessage = $"准备中: {Path.GetFileName(currentFile)} ({preparedFiles}/{totalFiles})";
            _job.CurrentFile = currentFile;
            _job.TotalFiles = TotalFiles;

            PublishProgressEvent();
        }
    }

    /// <summary>
    /// Phase 2 GPU 批次向量推理进度上报（关键：彻底解决 GPU 推理期间假死无进度）
    /// </summary>
    public void ReportEmbeddingProgress(
        int batchIndex,
        int totalBatches,
        int processedChunks,
        int totalChunks,
        string? currentFile = null)
    {
        lock (_stateLock)
        {
            CurrentBatch = batchIndex;
            TotalBatches = totalBatches;
            ProcessedChunks = processedChunks;
            TotalChunks = Math.Max(TotalChunks, totalChunks);
            if (!string.IsNullOrEmpty(currentFile))
            {
                CurrentFile = currentFile;
                _job.CurrentFile = currentFile;
            }

            var percent = CalculateProgress();
            StatusMessage = $"向量推理 (批次 {batchIndex}/{totalBatches}: {processedChunks}/{totalChunks} 分块, {percent:F1}%)";
            _job.CurrentFile = StatusMessage;
            _job.TotalChunks = TotalChunks;

            PublishProgressEvent();
        }
    }

    /// <summary>
    /// Phase 3 BM25 与图谱收尾后处理进度上报
    /// </summary>
    public void ReportFinalizingProgress(string currentFile, int processedFiles, int totalFiles)
    {
        lock (_stateLock)
        {
            CurrentFile = currentFile;
            ProcessedFiles = processedFiles;
            TotalFiles = Math.Max(TotalFiles, totalFiles);
            _job.ProcessedFiles = processedFiles;
            _job.TotalFiles = TotalFiles;
            _job.CurrentFile = currentFile;
            StatusMessage = $"后处理: {Path.GetFileName(currentFile)} ({processedFiles}/{totalFiles})";

            PublishProgressEvent();
        }
    }

    public void ReportCompleted(TimeSpan duration)
    {
        lock (_stateLock)
        {
            TransitionTo(IndexJobPhase.Completed, "索引完成");
            _job.Status = IndexStatus.Completed;
            _job.EndTime = DateTime.UtcNow;
            _job.Duration = duration;
            _job.ProcessedFiles = ProcessedFiles;
            _job.SkippedFiles = SkippedFiles;
            _job.Errors = ErrorsCount;
            _job.TotalChunks = TotalChunks;
            _job.TotalVectors = TotalVectors;

            _events?.PublishAsync("IndexCompleted", new IndexCompletedEvent
            {
                IndexId = JobId,
                TotalFiles = TotalFiles,
                TotalChunks = TotalChunks,
                TotalVectors = TotalVectors,
                Duration = duration,
                CompletedAt = _job.EndTime.Value,
                Errors = new List<string>(),
                Phase = CurrentPhase.ToString(),
                TargetResource = GetTargetResource(),
                JobType = GetJobType(),
                Success = true
            });
        }
    }

    public void ReportFaulted(Exception ex, string? context = null)
    {
        lock (_stateLock)
        {
            var msg = context != null ? $"{context}: {ex.Message}" : ex.Message;
            TransitionTo(IndexJobPhase.Faulted, msg);
            _job.Status = IndexStatus.Failed;
            _job.ErrorMessage = msg;
            _job.EndTime = DateTime.UtcNow;
            _job.Duration = _job.EndTime.Value - StartTime;
            _job.Errors = Math.Max(1, ErrorsCount);

            _events?.PublishAsync("IndexCompleted", new IndexCompletedEvent
            {
                IndexId = JobId,
                TotalFiles = TotalFiles,
                TotalChunks = TotalChunks,
                TotalVectors = TotalVectors,
                Duration = _job.Duration,
                CompletedAt = _job.EndTime.Value,
                Errors = new List<string> { msg },
                Phase = CurrentPhase.ToString(),
                TargetResource = GetTargetResource(),
                JobType = GetJobType(),
                Success = false
            });
        }
    }

    public void ReportCancelled()
    {
        lock (_stateLock)
        {
            TransitionTo(IndexJobPhase.Cancelled, "任务已取消");
            _job.Status = IndexStatus.Cancelled;
            _job.EndTime = DateTime.UtcNow;
            _job.Duration = _job.EndTime.Value - StartTime;

            _events?.PublishAsync("IndexCompleted", new IndexCompletedEvent
            {
                IndexId = JobId,
                TotalFiles = TotalFiles,
                TotalChunks = TotalChunks,
                TotalVectors = TotalVectors,
                Duration = _job.Duration,
                CompletedAt = _job.EndTime.Value,
                Phase = CurrentPhase.ToString(),
                TargetResource = GetTargetResource(),
                JobType = GetJobType(),
                Success = false
            });
        }
    }

    private void SyncJobState(IndexJobPhase phase)
    {
        _job.Status = phase switch
        {
            IndexJobPhase.Queued => IndexStatus.Pending,
            IndexJobPhase.Scanning => IndexStatus.Running,
            IndexJobPhase.Preparing => IndexStatus.Running,
            IndexJobPhase.Embedding => IndexStatus.Running,
            IndexJobPhase.Writing => IndexStatus.Running,
            IndexJobPhase.Finalizing => IndexStatus.Running,
            IndexJobPhase.Completed => IndexStatus.Completed,
            IndexJobPhase.Faulted => IndexStatus.Failed,
            IndexJobPhase.Cancelled => IndexStatus.Cancelled,
            _ => IndexStatus.Running
        };
    }

    private string GetTargetResource() =>
        _job.Request?.Sources?.Count == 1 ? $"source:{_job.Request.Sources[0]}" : "global";

    private string GetJobType() =>
        _job.Request?.Rechunk == true ? "Rechunk" :
        (_job.Request?.VectorOnly == true ? "VectorOnly" :
        (_job.Request?.Force == true ? "FullRebuild" : "Incremental"));

    private void PublishProgressEvent()
    {
        if (_events == null) return;

        try
        {
            _events.PublishAsync("IndexProgress", new IndexProgressEvent
            {
                IndexId = JobId,
                ProcessedFiles = ProcessedFiles,
                TotalFiles = TotalFiles,
                CurrentFile = !string.IsNullOrEmpty(StatusMessage) ? StatusMessage : CurrentFile,
                ProgressPercent = CalculateProgress(),
                Timestamp = DateTime.UtcNow,
                Phase = CurrentPhase.ToString(),
                TargetResource = GetTargetResource(),
                JobType = GetJobType()
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "广播 IndexProgress 失败");
        }
    }
}
