using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 词法倒排索引后台补齐 Worker。
/// 设计要点：
///  1. 查询读路径永不同步建索引，只投递 <see cref="LexicalIndexBackfillSignal"/>；
///  2. 启动后延迟扫描一次（避开启动风暴），之后按需 + 定时兜底；
///  3. 全局串行（单 Worker 顺序处理），避免多库同时构建导致磁盘随机写踩踏；
///  4. 使用独立 CancellationToken（仅受宿主停机控制），不受用户请求取消影响。
/// </summary>
public sealed class LexicalIndexBackfillWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LexicalIndexBackfillWorker> _logger;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _idleInterval;

    public LexicalIndexBackfillWorker(
        IServiceProvider serviceProvider,
        ILogger<LexicalIndexBackfillWorker> logger,
        TimeSpan? initialDelay = null,
        TimeSpan? idleInterval = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(20);
        _idleInterval = idleInterval ?? TimeSpan.FromMinutes(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[LexicalIndexBackfill] 启动，首次扫描延迟 {Delay}，空闲轮询 {Interval}",
            _initialDelay, _idleInterval);

        try
        {
            await Task.Delay(_initialDelay, stoppingToken);
            await ScanAllAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                var requested = await LexicalIndexBackfillSignal.ReadAsync(_idleInterval, stoppingToken);
                if (requested != null)
                {
                    await BackfillOneAsync(requested, stoppingToken);
                }
                else
                {
                    // 空闲周期兜底扫描：覆盖"补齐过程中又写入新文档"导致的 Stale
                    await ScanAllAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("[LexicalIndexBackfill] 已停止");
        }
    }

    /// <summary>扫描全部启用知识库，串行补齐未就绪的词法索引。</summary>
    public async Task ScanAllAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogStore>();
            var kbs = await catalog.ListKnowledgeBasesAsync(ct);
            foreach (var kb in kbs.Where(k => k.Enabled))
            {
                if (ct.IsCancellationRequested) return;
                await BackfillOneAsync(kb.Id, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[LexicalIndexBackfill] 全量扫描异常");
        }
    }

    /// <summary>补齐单个知识库（幂等：已就绪则直接跳过）。</summary>
    public async Task<bool> BackfillOneAsync(string knowledgeBaseId, CancellationToken ct = default)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var storage = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseStorageManager>();
            await using var lease = await storage.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, ct);

            var status = await lease.Store.GetLexicalIndexStatusAsync(ct);
            if (status.Status is LexicalIndexStatus.Ready or LexicalIndexStatus.Empty)
            {
                return false;
            }

            _logger.LogInformation(
                "[LexicalIndexBackfill] 知识库 {KbId} 词法索引状态 {Status}（{Indexed}/{Chunks}），开始后台补齐",
                knowledgeBaseId, status.Status, status.IndexedCount, status.ChunkCount);

            var result = await lease.Store.BackfillLexicalIndexAsync(500, null, ct);
            if (!result.Completed)
            {
                _logger.LogWarning(
                    "[LexicalIndexBackfill] 知识库 {KbId} 补齐未完成（{Indexed}/{Total}）：{Msg}",
                    knowledgeBaseId, result.Indexed, result.Total, result.Message);
            }
            return result.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LexicalIndexBackfill] 知识库 {KbId} 补齐异常", knowledgeBaseId);
            return false;
        }
    }
}
