using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 宿主启动期孤儿作业恢复与对账服务。
/// 当容器或进程因重启、崩溃、OOM 退出后，历史保存在 Catalog 中的 Running/Queued 作业失去了执行它的进程与内存 CTS。
/// 本服务在宿主启动时执行一次性扫描，将执行者已消失的孤儿作业安全收口为终止状态（Failed/Cancelled），
/// 杜绝前端与监控接口永久卡在历史进度（例如 54%）。
/// </summary>
public sealed class JobRecoveryService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly KnowledgeBaseJobCoordinator _coordinator;
    private readonly ILogger<JobRecoveryService> _logger;

    public JobRecoveryService(
        IServiceProvider serviceProvider,
        KnowledgeBaseJobCoordinator coordinator,
        ILogger<JobRecoveryService> logger)
    {
        _serviceProvider = serviceProvider;
        _coordinator = coordinator;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[JobRecoveryService] 正在执行宿主启动期作业恢复与孤儿对账扫描...");
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogStore>();
            var activeJobs = await catalog.GetActiveJobsAsync(cancellationToken);

            if (activeJobs.Count == 0)
            {
                _logger.LogInformation("[JobRecoveryService] 启动对账扫描完成：未发现遗留未结束作业。");
                return;
            }

            int recoveredCount = 0;
            foreach (var job in activeJobs)
            {
                // 如果本进程内存协调器中没有该 Job 的 CTS，说明它是前一进程或宿主重启遗留的孤儿作业
                if (!_coordinator.TryGetCts(job.Id, out _))
                {
                    JobDefinition updated;
                    if (job.Status == JobStatus.Cancelling)
                    {
                        updated = job with
                        {
                            Status = JobStatus.Cancelled,
                            ErrorMessage = job.ErrorMessage ?? "服务重启前已请求取消，启动对账已收口为已取消",
                            FinishedAt = DateTime.UtcNow
                        };
                    }
                    else
                    {
                        updated = job with
                        {
                            Status = JobStatus.Failed,
                            ErrorMessage = "服务进程重启或异常中断，任务已终止（可重新触发）",
                            FinishedAt = DateTime.UtcNow
                        };
                    }

                    await catalog.SaveJobAsync(updated, CancellationToken.None);
                    recoveredCount++;

                    _logger.LogWarning(
                        "[JobRecoveryService] 成功恢复孤儿作业 {JobId} (Op: {Op}, 原状态: {OldStatus}, 进度: {Processed}/{Total}) -> 已收口终态: {NewStatus}",
                        job.Id, job.Operation, job.Status, job.ProcessedUnits, job.TotalUnits, updated.Status);
                }
            }

            _logger.LogInformation("[JobRecoveryService] 启动对账扫描完成：共对账收口 {Count} 个孤儿作业。", recoveredCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[JobRecoveryService] 启动期孤儿作业对账扫描异常");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
