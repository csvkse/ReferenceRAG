using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Connectors;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Jobs;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Runtime;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 知识库与数据连接器管理服务实现
/// </summary>
public sealed class KnowledgeBaseService : IKnowledgeBaseService
{
    private readonly ICatalogStore _catalog;
    private readonly IKnowledgeBaseStorageManager _storageManager;
    private readonly IModelRouter? _modelRouter;
    private readonly ILogger<KnowledgeBaseService>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Dictionary<string, CancellationTokenSource> _runningJobs = new();
    private readonly object _jobsLock = new();

    public KnowledgeBaseService(
        ICatalogStore catalog,
        IKnowledgeBaseStorageManager storageManager,
        IModelRouter? modelRouter = null,
        ILogger<KnowledgeBaseService>? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _modelRouter = modelRouter;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public async Task<KnowledgeBaseDefinition> CreateKnowledgeBaseAsync(
        string name,
        string? description,
        string embeddingSpecId = "default-spec",
        EmbeddingSpec? customSpec = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("知识库名称不能为空", nameof(name));

        if (customSpec != null)
        {
            await _catalog.SaveEmbeddingSpecAsync(customSpec, ct);
            embeddingSpecId = customSpec.Id;
        }

        var id = Guid.NewGuid().ToString("N")[..16];
        var now = DateTime.UtcNow;

        var kb = new KnowledgeBaseDefinition(
            Id: id,
            Name: name.Trim(),
            Description: description?.Trim(),
            Enabled: true,
            DesiredConfigRevision: 1,
            EffectiveConfigRevision: 1,
            EmbeddingSpecId: embeddingSpecId,
            CreatedAt: now,
            UpdatedAt: now
        );

        // 初始化底层存储
        await using var lease = await _storageManager.AcquireLeaseAsync(id, StorageAccess.ReadWrite, ct);
        await lease.Store.InitializeAsync(ct);

        await _catalog.SaveKnowledgeBaseAsync(kb, ct);
        _logger?.LogInformation("[KnowledgeBaseService] 创建知识库成功: {Id} ({Name})", id, name);
        return kb;
    }

    public Task<KnowledgeBaseDefinition?> GetKnowledgeBaseAsync(string id, CancellationToken ct = default)
    {
        return _catalog.GetKnowledgeBaseAsync(id, ct);
    }

    public Task<IReadOnlyList<KnowledgeBaseDefinition>> ListKnowledgeBasesAsync(CancellationToken ct = default)
    {
        return _catalog.ListKnowledgeBasesAsync(ct);
    }

    public async Task<bool> DeleteKnowledgeBaseAsync(string id, CancellationToken ct = default)
    {
        var deleted = await _catalog.DeleteKnowledgeBaseAsync(id, ct);
        if (deleted)
        {
            try
            {
                await _storageManager.DeleteKnowledgeBaseAsync(id, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[KnowledgeBaseService] 物理删除知识库存储目录失败: {Id}", id);
            }
        }
        return deleted;
    }

    public async Task<ConnectorDefinition> AddConnectorAsync(
        string knowledgeBaseId,
        string kind,
        string targetPath,
        string? filePattern = null,
        CancellationToken ct = default)
    {
        var kb = await _catalog.GetKnowledgeBaseAsync(knowledgeBaseId, ct);
        if (kb == null)
        {
            throw new InvalidOperationException($"知识库未找到: {knowledgeBaseId}");
        }

        var id = Guid.NewGuid().ToString("N")[..16];
        var now = DateTime.UtcNow;

        var connector = new ConnectorDefinition(
            Id: id,
            KnowledgeBaseId: knowledgeBaseId,
            Kind: kind,
            TargetPath: targetPath,
            FilePattern: filePattern,
            ConfigRevision: 1,
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now
        );

        await _catalog.SaveConnectorAsync(connector, ct);
        _logger?.LogInformation("[KnowledgeBaseService] 新增连接器成功: {Id} -> KB {KbId}", id, knowledgeBaseId);
        return connector;
    }

    public Task<IReadOnlyList<ConnectorDefinition>> GetConnectorsAsync(string knowledgeBaseId, CancellationToken ct = default)
    {
        return _catalog.GetConnectorsByKnowledgeBaseAsync(knowledgeBaseId, ct);
    }

    public Task<bool> DeleteConnectorAsync(string connectorId, CancellationToken ct = default)
    {
        return _catalog.DeleteConnectorAsync(connectorId, ct);
    }

    public async Task<JobDefinition> TriggerSyncJobAsync(string knowledgeBaseId, string connectorId, CancellationToken ct = default)
    {
        var kb = await _catalog.GetKnowledgeBaseAsync(knowledgeBaseId, ct);
        if (kb == null) throw new InvalidOperationException($"知识库未找到: {knowledgeBaseId}");

        var connectors = await _catalog.GetConnectorsByKnowledgeBaseAsync(knowledgeBaseId, ct);
        var connectorDef = connectors.FirstOrDefault(c => c.Id == connectorId)
            ?? throw new InvalidOperationException($"连接器未找到: {connectorId}");

        // 创建连接器实例
        IDataConnector connectorInstance = connectorDef.Kind.ToLowerInvariant() switch
        {
            "singlefile" or "file" => new SingleFileConnector(connectorDef.Id, knowledgeBaseId, connectorDef.TargetPath),
            _ => new LocalFolderConnector(connectorDef.Id, knowledgeBaseId, connectorDef.TargetPath, connectorDef.FilePattern)
        };

        var jobId = Guid.NewGuid().ToString("N")[..16];
        var job = new JobDefinition(
            Id: jobId,
            Operation: "sync",
            KnowledgeBaseId: knowledgeBaseId,
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Queued,
            Phase: OperationPhase.None,
            TotalUnits: 0,
            ProcessedUnits: 0,
            IdempotencyKey: $"{knowledgeBaseId}:{connectorId}:{DateTime.UtcNow.Ticks}",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow,
            FinishedAt: null
        );

        await _catalog.SaveJobAsync(job, ct);

        // 注册取消源并启动后台工作流
        var cts = new CancellationTokenSource();
        lock (_jobsLock)
        {
            _runningJobs[jobId] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var workerLogger = _loggerFactory?.CreateLogger<KnowledgeBaseSyncWorker>();
                var worker = new KnowledgeBaseSyncWorker(_catalog, _storageManager, _modelRouter, workerLogger, _loggerFactory);
                await worker.ExecuteSyncAsync(job, connectorInstance, cts.Token);
            }
            finally
            {
                lock (_jobsLock)
                {
                    _runningJobs.Remove(jobId);
                }
                cts.Dispose();
                await connectorInstance.DisposeAsync();
            }
        });

        return job;
    }

    public Task<JobDefinition?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        return _catalog.GetJobAsync(jobId, ct);
    }

    public Task<IReadOnlyList<JobDefinition>> ListJobsAsync(string? knowledgeBaseId = null, CancellationToken ct = default)
    {
        return _catalog.ListJobsAsync(knowledgeBaseId, ct);
    }

    public async Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        CancellationTokenSource? cts;
        lock (_jobsLock)
        {
            _runningJobs.TryGetValue(jobId, out cts);
        }

        if (cts != null)
        {
            cts.Cancel();
            var smLogger = _loggerFactory?.CreateLogger<JobStateMachine>();
            var job = await _catalog.GetJobAsync(jobId, ct);
            if (job != null)
            {
                var sm = new JobStateMachine(job, _catalog, smLogger);
                await sm.RequestCancelAsync(ct);
            }
            return true;
        }

        return false;
    }

    public Task<IReadOnlyList<EmbeddingSpec>> ListEmbeddingSpecsAsync(CancellationToken ct = default)
    {
        return _catalog.ListEmbeddingSpecsAsync(ct);
    }
}
