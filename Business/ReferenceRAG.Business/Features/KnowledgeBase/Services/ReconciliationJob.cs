using Microsoft.Extensions.Hosting;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 定时对账作业（remaining-plan §1.2）。首版只报告不自动改：
/// 每 30 分钟跑一次 ReconcileAsync，把报告存入 ReconciliationState 并打日志，
/// 不删除任何连接器、不清理任何语料。手动触发走 POST /api/knowledge-bases/reconcile。
/// </summary>
public sealed class ReconciliationJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IReconciliationState _state;
    private readonly ILogger<ReconciliationJob> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _initialDelay;

    public ReconciliationJob(
        IServiceProvider serviceProvider,
        IReconciliationState state,
        ILogger<ReconciliationJob> logger,
        TimeSpan? interval = null,
        TimeSpan? initialDelay = null)
    {
        _serviceProvider = serviceProvider;
        _state = state;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromMinutes(30);
        _initialDelay = initialDelay ?? TimeSpan.FromMinutes(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[ReconciliationJob] 启动，首次延迟 {InitialDelay}，间隔 {Interval}（仅报告，不自动修复）",
            _initialDelay, _interval);
        try
        {
            await Task.Delay(_initialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ReconciliationJob] 对账异常");
                }
                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("[ReconciliationJob] 已停止");
        }
    }

    /// <summary>单次对账；IKnowledgeBaseService 是 Scoped，后台服务必须开 scope 解析。</summary>
    public async Task<SourceLinkReconcileReport> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var kbService = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseService>();
        var report = await kbService.ReconcileAsync(ct);
        _state.Set(report);

        if (report.Issues.Count > 0)
        {
            _logger.LogWarning(
                "[ReconciliationJob] 检测到 {Count} 项挂载漂移：孤儿 {Orphaned} / 未挂载 {Unlinked} / 可重绑 {Drifted}",
                report.Issues.Count, report.OrphanedCount, report.UnlinkedCount, report.DriftedCount);
        }
        else
        {
            _logger.LogInformation(
                "[ReconciliationJob] 对账完成：{KbCount} 个知识库 / {ConnCount} 个连接器 / {SourceCount} 个源，无漂移",
                report.KnowledgeBaseCount, report.ConnectorCount, report.SourceCount);
        }
        return report;
    }
}
