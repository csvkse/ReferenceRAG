using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;
using ReferenceRAG.Core.Features.DocumentExtraction.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Connectors;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Jobs;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Runtime;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 知识库长时作业受管宿主工作者（托管生命周期，由 BackgroundService 驱动，替代请求内未托管 Task.Run）
/// </summary>
public sealed class KnowledgeBaseJobWorker : BackgroundService
{
    private readonly IKnowledgeBaseJobQueue _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly KnowledgeBaseJobCoordinator _coordinator;
    private readonly ILogger<KnowledgeBaseJobWorker> _logger;

    public KnowledgeBaseJobWorker(
        IKnowledgeBaseJobQueue queue,
        IServiceProvider serviceProvider,
        KnowledgeBaseJobCoordinator coordinator,
        ILogger<KnowledgeBaseJobWorker> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _coordinator = coordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[KnowledgeBaseJobWorker] 知识库受管作业调度工作者已启动。");

        while (!stoppingToken.IsCancellationRequested)
        {
            KnowledgeBaseJobRequest request;
            try
            {
                request = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessJobRequestAsync(request, stoppingToken);
        }

        _logger.LogInformation("[KnowledgeBaseJobWorker] 知识库受管作业调度工作者已平稳停止。");
    }

    private async Task ProcessJobRequestAsync(KnowledgeBaseJobRequest request, CancellationToken stoppingToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(request.JobCts.Token, stoppingToken);
        var linkedToken = linkedCts.Token;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogStore>();
            var storageManager = scope.ServiceProvider.GetRequiredService<IKnowledgeBaseStorageManager>();
            var modelRouter = scope.ServiceProvider.GetService<IModelRouter>();
            var configManager = scope.ServiceProvider.GetService<ConfigManager>();
            var markdownChunker = scope.ServiceProvider.GetService<IMarkdownChunker>();
            var extractorRouter = scope.ServiceProvider.GetService<IDocumentExtractorRouter>();
            var loggerFactory = scope.ServiceProvider.GetService<ILoggerFactory>();
            var syncLogger = loggerFactory?.CreateLogger<KnowledgeBaseSyncWorker>();

            // 检查排队期间是否已被取消
            var freshJob = await catalog.GetJobAsync(request.Job.Id, linkedToken);
            if (freshJob == null || freshJob.Status is JobStatus.Cancelled or JobStatus.Failed or JobStatus.Succeeded or JobStatus.PartiallySucceeded)
            {
                _logger.LogInformation("[KnowledgeBaseJobWorker] 作业 {JobId} 已在排队期间结束（状态: {Status}），放弃执行",
                    request.Job.Id, freshJob?.Status);
                return;
            }

            var enableEmbedding = configManager?.Load().Indexing.EmbeddingEnabled ?? true;
            var syncWorker = new KnowledgeBaseSyncWorker(
                catalog,
                storageManager,
                modelRouter,
                syncLogger,
                loggerFactory,
                enableEmbedding,
                markdownChunker,
                extractorRouter);

            if (request.Kind == KnowledgeBaseJobKind.Sync && request.Connector != null)
            {
                var connectorInstance = CreateConnectorInstance(request.Connector, request.Job.KnowledgeBaseId);
                try
                {
                    await syncWorker.ExecuteSyncAsync(freshJob, connectorInstance, linkedToken);
                }
                finally
                {
                    await connectorInstance.DisposeAsync();
                }
            }
            else if (request.Kind == KnowledgeBaseJobKind.Rechunk)
            {
                var smLogger = loggerFactory?.CreateLogger<JobStateMachine>();
                var sm = new JobStateMachine(freshJob, catalog, smLogger);
                await sm.TransitionToAsync(OperationPhase.Chunking, JobStatus.Running, ct: linkedToken);

                var connectors = request.AllConnectors ?? Array.Empty<ConnectorDefinition>();
                int totalConnectors = connectors.Count;
                int succeeded = 0;
                int failed = 0;

                await sm.UpdateProgressAsync(0, totalConnectors, linkedToken);

                for (int i = 0; i < connectors.Count; i++)
                {
                    if (linkedToken.IsCancellationRequested) break;

                    var connDef = connectors[i];
                    var connectorInstance = CreateConnectorInstance(connDef, request.Job.KnowledgeBaseId);
                    try
                    {
                        var childJobId = $"{request.Job.Id}_{connDef.Id}";
                        var childJob = new JobDefinition(
                            Id: childJobId,
                            Operation: "sync",
                            KnowledgeBaseId: request.Job.KnowledgeBaseId,
                            ExpectedIndexEpoch: freshJob.ExpectedIndexEpoch,
                            Status: JobStatus.Queued,
                            Phase: OperationPhase.None,
                            TotalUnits: 0,
                            ProcessedUnits: 0,
                            IdempotencyKey: $"{request.Job.KnowledgeBaseId}:{connDef.Id}:{DateTime.UtcNow.Ticks}",
                            ErrorMessage: null,
                            CreatedAt: DateTime.UtcNow,
                            FinishedAt: null
                        );
                        await catalog.SaveJobAsync(childJob, linkedToken);
                        var childResult = await syncWorker.ExecuteSyncAsync(childJob, connectorInstance, linkedToken);
                        if (childResult.Status is JobStatus.Succeeded or JobStatus.PartiallySucceeded)
                        {
                            succeeded++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[KnowledgeBaseJobWorker] 连接器重新分段异常: {ConnId}", connDef.Id);
                        failed++;
                    }
                    finally
                    {
                        await connectorInstance.DisposeAsync();
                    }

                    await sm.UpdateProgressAsync(i + 1, totalConnectors, linkedToken);
                }

                if (linkedToken.IsCancellationRequested)
                {
                    await sm.TransitionToAsync(OperationPhase.None, JobStatus.Cancelled, errorMessage: "任务已取消", ct: CancellationToken.None);
                }
                else
                {
                    var finalStatus = failed == 0 ? JobStatus.Succeeded :
                        (succeeded > 0 ? JobStatus.PartiallySucceeded : JobStatus.Failed);

                    await sm.TransitionToAsync(
                        OperationPhase.None,
                        finalStatus,
                        errorMessage: failed > 0 ? $"{failed}/{totalConnectors} 个连接器分段失败" : null,
                        ct: CancellationToken.None);

                    // 仅当全部成功时推进知识库生效版本 (EffectiveConfigRevision)
                    if (finalStatus == JobStatus.Succeeded)
                    {
                        var kb = await catalog.GetKnowledgeBaseAsync(request.Job.KnowledgeBaseId, CancellationToken.None);
                        if (kb != null && kb.EffectiveConfigRevision < kb.DesiredConfigRevision)
                        {
                            var updatedKb = kb with { EffectiveConfigRevision = kb.DesiredConfigRevision, UpdatedAt = DateTime.UtcNow };
                            await catalog.SaveKnowledgeBaseAsync(updatedKb, CancellationToken.None);
                            _logger.LogInformation("[KnowledgeBaseJobWorker] 知识库 {KbId} 全量重切完毕，生效版本提升至 {Rev}",
                                kb.Id, updatedKb.EffectiveConfigRevision);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("[KnowledgeBaseJobWorker] 宿主正在停机，作业 {JobId} 记录中断终态", request.Job.Id);
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var catalog = scope.ServiceProvider.GetRequiredService<ICatalogStore>();
                var current = await catalog.GetJobAsync(request.Job.Id, CancellationToken.None);
                if (current != null && current.Status is JobStatus.Queued or JobStatus.Running or JobStatus.Cancelling)
                {
                    var interrupted = current with
                    {
                        Status = JobStatus.Failed,
                        ErrorMessage = "服务进程停机中断，任务已终止（可重新触发）",
                        FinishedAt = DateTime.UtcNow
                    };
                    await catalog.SaveJobAsync(interrupted, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[KnowledgeBaseJobWorker] 写入停机中断状态异常: {JobId}", request.Job.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[KnowledgeBaseJobWorker] 作业执行未处理异常: {JobId}", request.Job.Id);
        }
        finally
        {
            _coordinator.EndJob(request.Job.KnowledgeBaseId, request.Job.Id);
            try
            {
                request.JobCts.Dispose();
            }
            catch { }
        }
    }

    private static IDataConnector CreateConnectorInstance(ConnectorDefinition connectorDef, string knowledgeBaseId)
    {
        return connectorDef.Kind.ToLowerInvariant() switch
        {
            "singlefile" or "file" => new SingleFileConnector(connectorDef.Id, knowledgeBaseId, connectorDef.TargetPath),
            _ => new LocalFolderConnector(connectorDef.Id, knowledgeBaseId, connectorDef.TargetPath, connectorDef.FilePattern)
        };
    }
}
