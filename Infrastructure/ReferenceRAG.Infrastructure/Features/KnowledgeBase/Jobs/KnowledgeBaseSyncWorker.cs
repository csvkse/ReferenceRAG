using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Connectors;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Features.ModelManagement.Runtime;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Jobs;

/// <summary>
/// 知识库同步与索引执行器（单库单连接器执行，支持向量与文本双通道）
/// </summary>
public sealed class KnowledgeBaseSyncWorker
{
    private readonly ICatalogStore _catalog;
    private readonly IKnowledgeBaseStorageManager _storageManager;
    private readonly IModelRouter? _modelRouter;
    private readonly ILogger<KnowledgeBaseSyncWorker>? _logger;
    private readonly ILoggerFactory? _loggerFactory;

    public KnowledgeBaseSyncWorker(
        ICatalogStore catalog,
        IKnowledgeBaseStorageManager storageManager,
        IModelRouter? modelRouter = null,
        ILogger<KnowledgeBaseSyncWorker>? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _modelRouter = modelRouter;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task<JobDefinition> ExecuteSyncAsync(
        JobDefinition job,
        IDataConnector connector,
        CancellationToken ct = default)
    {
        var smLogger = _loggerFactory?.CreateLogger<JobStateMachine>();
        var stateMachine = new JobStateMachine(job, _catalog, smLogger);

        await stateMachine.TransitionToAsync(OperationPhase.Scanning, JobStatus.Running, ct: ct);

        try
        {
            var kbDef = await _catalog.GetKnowledgeBaseAsync(job.KnowledgeBaseId, ct);
            var specId = kbDef?.EmbeddingSpecId ?? "default-spec";

            // 1. 扫描全部变更项
            var changes = new List<SourceChange>();
            await foreach (var change in connector.ScanAsync(new SyncRequest(FullScan: true), ct))
            {
                changes.Add(change);
            }

            await stateMachine.UpdateProgressAsync(0, changes.Count, ct);

            // 2. 依次处理并分块提交
            await stateMachine.TransitionToAsync(OperationPhase.Chunking, ct: ct);

            int processed = 0;
            int errors = 0;

            foreach (var change in changes)
            {
                if (stateMachine.Status == JobStatus.Cancelling || ct.IsCancellationRequested)
                {
                    _logger?.LogInformation("[KnowledgeBaseSyncWorker] 检测到取消请求，停止后续文档处理: {JobId}", job.Id);
                    await stateMachine.TransitionToAsync(OperationPhase.None, JobStatus.Cancelled, ct: ct);
                    return stateMachine.CurrentJob;
                }

                if (change.Kind == ChangeKind.Delete)
                {
                    await using var lease = await _storageManager.AcquireLeaseAsync(job.KnowledgeBaseId, StorageAccess.ReadWrite, ct);
                    await lease.Store.DeleteDocumentAsync(connector.Id, change.SourceKey, ct);
                    processed++;
                    await stateMachine.UpdateProgressAsync(processed, ct: ct);
                    continue;
                }

                try
                {
                    string textContent;
                    await using (var stream = await connector.OpenReadAsync(change, ct))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        textContent = await reader.ReadToEndAsync(ct);
                    }

                    var hash = ComputeSha256(textContent);
                    var chunks = ChunkText(textContent);

                    // 可选向量提取
                    List<float[]>? vectors = null;
                    if (_modelRouter != null && chunks.Count > 0)
                    {
                        try
                        {
                            await using var modelLease = await _modelRouter.AcquireLeaseAsync<IEmbeddingBackendAdapter>(
                                new ModelSelection(specId, "embedding"),
                                new WorkloadContext($"job_{job.Id}", Priority: -1),
                                ct);
                            var texts = chunks.Select(c => c.EmbeddingText ?? c.Text).ToList();
                            var rawVectors = await modelLease.Client.EncodeBatchAsync(texts, null, ct);
                            vectors = rawVectors.ToList();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug("[KnowledgeBaseSyncWorker] 模型向量提取未就绪，降级为纯文本索引: {Msg}", ex.Message);
                        }
                    }

                    await using var lease = await _storageManager.AcquireLeaseAsync(job.KnowledgeBaseId, StorageAccess.ReadWrite, ct);

                    var batch = new IndexBatch(
                        KnowledgeBaseId: job.KnowledgeBaseId,
                        ExpectedIndexEpoch: lease.IndexEpochSnapshot,
                        ExpectedDocumentVersion: 0,
                        ConnectorId: connector.Id,
                        SourceKey: change.SourceKey,
                        Locator: change.Locator,
                        SourceRevision: change.SourceRevision,
                        ContentHash: hash,
                        Chunks: chunks,
                        Vectors: vectors,
                        ProducerCohort: specId
                    );

                    var commitResult = await lease.Store.CommitDocumentAsync(batch, ct);
                    if (!commitResult.Success)
                    {
                        _logger?.LogWarning("[KnowledgeBaseSyncWorker] 提交文档失败: {Key}, 错误: {Err}", change.SourceKey, commitResult.ErrorMessage);
                        errors++;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[KnowledgeBaseSyncWorker] 处理文档异常: {Key}", change.SourceKey);
                    errors++;
                }

                processed++;
                await stateMachine.UpdateProgressAsync(processed, ct: ct);
            }

            await stateMachine.TransitionToAsync(OperationPhase.Publishing, ct: ct);

            var finalStatus = errors == 0 ? JobStatus.Succeeded :
                (errors < processed ? JobStatus.PartiallySucceeded : JobStatus.Failed);

            await stateMachine.TransitionToAsync(
                OperationPhase.None,
                finalStatus,
                errorMessage: errors > 0 ? $"{errors} 个文档处理失败" : null,
                ct: ct);

            return stateMachine.CurrentJob;
        }
        catch (OperationCanceledException)
        {
            await stateMachine.TransitionToAsync(OperationPhase.None, JobStatus.Cancelled, ct: CancellationToken.None);
            return stateMachine.CurrentJob;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[KnowledgeBaseSyncWorker] 任务执行致命异常: {JobId}", job.Id);
            await stateMachine.TransitionToAsync(OperationPhase.None, JobStatus.Failed, errorMessage: ex.Message, ct: CancellationToken.None);
            return stateMachine.CurrentJob;
        }
    }

    private static IReadOnlyList<ChunkData> ChunkText(string text, int chunkSize = 500, int overlap = 50)
    {
        var list = new List<ChunkData>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        int ordinal = 0;
        int startIndex = 0;
        int length = text.Length;

        while (startIndex < length)
        {
            int currentChunkSize = Math.Min(chunkSize, length - startIndex);
            var chunkText = text.Substring(startIndex, currentChunkSize).Trim();

            if (!string.IsNullOrEmpty(chunkText))
            {
                list.Add(new ChunkData(
                    ChunkId: Guid.NewGuid().ToString("N"),
                    Ordinal: ordinal++,
                    Text: chunkText,
                    TokenCount: chunkText.Length / 4 + 1
                ));
            }

            startIndex += (chunkSize - overlap);
            if (startIndex >= length) break;
        }

        return list;
    }

    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
