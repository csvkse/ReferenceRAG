using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Service.Services;

/// <summary>
/// 后台定时清理孤立索引服务
/// 清理：孤立向量、孤立 BM25 文档、孤立图谱节点
/// </summary>
public class OrphanCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OrphanCleanupService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(6); // 每 6 小时清理一次

    public OrphanCleanupService(
        IServiceProvider serviceProvider,
        ILogger<OrphanCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[OrphanCleanupService] 启动，间隔: {Interval}", _interval);

        try
        {
            // 启动后延迟 5 分钟执行首次清理
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CleanupAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[OrphanCleanupService] 清理异常");
                }

                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("[OrphanCleanupService] 已停止");
        }
    }

    private async Task CleanupAsync()
    {
        using var scope = _serviceProvider.CreateScope();

        var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        var bm25Store = scope.ServiceProvider.GetRequiredService<IBM25Store>();
        var graphStore = scope.ServiceProvider.GetRequiredService<IGraphStore>();

        _logger.LogInformation("[OrphanCleanupService] 开始清理孤立数据...");

        // 1. 清理旧模型向量
        var existingModels = new List<string> { embeddingService.ModelName };
        var modelDeleted = await vectorStore.DeleteOrphanedVectorsAsync(existingModels);

        // 2. 清理孤立向量（分块已删除但向量残留）
        var chunkDeleted = await vectorStore.CleanupOrphanChunkVectorsAsync();

        // 3. 清理孤立 BM25 文档
        var validChunkIds = await vectorStore.GetAllChunkIdsAsync();
        var bm25Deleted = await bm25Store.CleanupOrphanDocumentsAsync(validChunkIds);

        // 4. 清理孤立图谱节点
        var allFiles = await vectorStore.GetAllFilesAsync();
        var validFileNodeIds = allFiles.Select(f => f.Path.Replace('\\', '/').TrimStart('/'));
        var graphDeleted = await graphStore.CleanupOrphanNodesAsync(validFileNodeIds);

        var total = modelDeleted + chunkDeleted + bm25Deleted + graphDeleted;

        if (total > 0)
        {
            _logger.LogInformation(
                "[OrphanCleanupService] 清理完成: 向量={Vector}, BM25={BM25}, 图谱={Graph}, 总计={Total}",
                modelDeleted + chunkDeleted, bm25Deleted, graphDeleted, total);
        }
        else
        {
            _logger.LogInformation("[OrphanCleanupService] 无孤立数据");
        }
    }
}
