using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Jobs;

/// <summary>
/// 两层任务状态机（管理 JobStatus 外部状态与 OperationPhase 内部阶段跃迁，并持久化至 Catalog）
/// </summary>
public sealed class JobStateMachine
{
    private readonly ICatalogStore _catalog;
    private readonly ILogger<JobStateMachine>? _logger;
    private readonly object _stateLock = new();

    public JobDefinition CurrentJob { get; private set; }

    public string JobId => CurrentJob.Id;
    public JobStatus Status => CurrentJob.Status;
    public OperationPhase Phase => CurrentJob.Phase;

    public JobStateMachine(
        JobDefinition initialJob,
        ICatalogStore catalog,
        ILogger<JobStateMachine>? logger = null)
    {
        CurrentJob = initialJob ?? throw new ArgumentNullException(nameof(initialJob));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger;
    }

    /// <summary>
    /// 推进阶段与状态
    /// </summary>
    public async Task<bool> TransitionToAsync(
        OperationPhase nextPhase,
        JobStatus? nextStatus = null,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        JobDefinition updated;
        lock (_stateLock)
        {
            if (CurrentJob.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
            {
                _logger?.LogDebug("[JobStateMachine] 任务 {JobId} 已在终态 {Status}，忽略跃迁", JobId, CurrentJob.Status);
                return false;
            }

            var status = nextStatus ?? CurrentJob.Status;
            DateTime? finishedAt = CurrentJob.FinishedAt;
            if (status is JobStatus.Succeeded or JobStatus.PartiallySucceeded or JobStatus.Failed or JobStatus.Cancelled)
            {
                finishedAt = DateTime.UtcNow;
            }

            updated = CurrentJob with
            {
                Phase = nextPhase,
                Status = status,
                ErrorMessage = errorMessage ?? CurrentJob.ErrorMessage,
                FinishedAt = finishedAt
            };
            CurrentJob = updated;
        }

        await _catalog.SaveJobAsync(updated, ct);
        _logger?.LogInformation("[JobStateMachine] 任务 {JobId} 状态跃迁 -> Status: {Status}, Phase: {Phase}",
            JobId, updated.Status, updated.Phase);

        return true;
    }

    /// <summary>
    /// 请求取消作业
    /// </summary>
    public async Task<bool> RequestCancelAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (CurrentJob.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)
            {
                return false;
            }

            if (CurrentJob.Status == JobStatus.Queued)
            {
                CurrentJob = CurrentJob with { Status = JobStatus.Cancelled, FinishedAt = DateTime.UtcNow };
            }
            else
            {
                CurrentJob = CurrentJob with { Status = JobStatus.Cancelling };
            }
        }

        await _catalog.SaveJobAsync(CurrentJob, ct);
        _logger?.LogInformation("[JobStateMachine] 任务 {JobId} 请求取消，当前状态: {Status}", JobId, CurrentJob.Status);
        return true;
    }

    /// <summary>
    /// 更新进度单位
    /// </summary>
    public async Task UpdateProgressAsync(int processedUnits, int? totalUnits = null, CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            CurrentJob = CurrentJob with
            {
                ProcessedUnits = processedUnits,
                TotalUnits = totalUnits ?? CurrentJob.TotalUnits
            };
        }

        await _catalog.SaveJobAsync(CurrentJob, ct);
    }
}
