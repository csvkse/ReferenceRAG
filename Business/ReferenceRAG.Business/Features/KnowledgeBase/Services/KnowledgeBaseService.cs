using ReferenceRAG.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Connectors;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Jobs;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Runtime;
using ReferenceRAG.Core.Services;

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
    private readonly ConfigManager? _configManager;
    private readonly IModelManager? _modelManager;
    private readonly IEmbeddingService? _embeddingService;
    private readonly Dictionary<string, CancellationTokenSource> _runningJobs = new();
    private readonly object _jobsLock = new();

    public KnowledgeBaseService(
        ICatalogStore catalog,
        IKnowledgeBaseStorageManager storageManager,
        IModelRouter? modelRouter = null,
        ILogger<KnowledgeBaseService>? logger = null,
        ILoggerFactory? loggerFactory = null,
        ConfigManager? configManager = null,
        IModelManager? modelManager = null,
        IEmbeddingService? embeddingService = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _modelRouter = modelRouter;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configManager = configManager;
        _modelManager = modelManager;
        _embeddingService = embeddingService;
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
        else if (embeddingSpecId != "default-spec")
        {
            var existingSpec = await _catalog.GetEmbeddingSpecAsync(embeddingSpecId, ct);
            if (existingSpec == null)
            {
                var allSpecs = await ListEmbeddingSpecsAsync(ct);
                var matched = allSpecs.FirstOrDefault(s => s.Id == embeddingSpecId);
                if (matched != null)
                {
                    await _catalog.SaveEmbeddingSpecAsync(matched, ct);
                }
            }
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

        var enableEmbedding = _configManager?.Load().Indexing.EmbeddingEnabled ?? true;
        _ = Task.Run(async () =>
        {
            try
            {
                var workerLogger = _loggerFactory?.CreateLogger<KnowledgeBaseSyncWorker>();
                var worker = new KnowledgeBaseSyncWorker(_catalog, _storageManager, _modelRouter, workerLogger, _loggerFactory, enableEmbedding);
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

    public async Task<IReadOnlyList<EmbeddingSpec>> ListEmbeddingSpecsAsync(CancellationToken ct = default)
    {
        var result = new List<EmbeddingSpec>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 系统默认模板 (Default Template)
        var defaultSpec = new EmbeddingSpec(
            Id: "default-spec",
            ModelName: "bge-small-zh-v1.5",
            Dimension: 512,
            Metric: "cosine",
            MaxTokens: 512
        );
        result.Add(defaultSpec);
        seenIds.Add(defaultSpec.Id);

        // 2. 本地已下载、物理存在且已启用的 Embedding 模型 (Strict Physical Validity Gate)
        if (_modelManager != null)
        {
            var downloaded = _modelManager.GetDownloadedModels()
                .Where(m => m.ModelType.Equals("embedding", StringComparison.OrdinalIgnoreCase)
                            && m.IsDownloaded
                            && m.PhysicalExists
                            && m.IsEnabled)
                .ToList();

            foreach (var m in downloaded)
            {
                var specId = $"local_{m.Name}";
                if (!seenIds.Contains(specId))
                {
                    result.Add(new EmbeddingSpec(
                        Id: specId,
                        ModelName: m.DisplayName ?? m.Name,
                        Dimension: m.Dimension > 0 ? m.Dimension : 512,
                        Metric: "cosine",
                        MaxTokens: m.MaxSequenceLength > 0 ? m.MaxSequenceLength : 512,
                        SearchBackend: "LocalOnnx"
                    ));
                    seenIds.Add(specId);
                }
            }
        }

        // 3. 集中式模型服务商已启用的 Embedding 模型 (Centralized Model Providers)
        if (_configManager != null)
        {
            var config = _configManager.Load();
            if (config.ModelProviders != null)
            {
                foreach (var prov in config.ModelProviders.Where(p => p.Enabled))
                {
                    if (prov.Models != null)
                    {
                        foreach (var mod in prov.Models.Where(m => m.Enabled && m.ModelType.Equals("embedding", StringComparison.OrdinalIgnoreCase)))
                        {
                            var specId = $"prov_{prov.Id}_{mod.ModelId}";
                            if (!seenIds.Contains(specId))
                            {
                                result.Add(new EmbeddingSpec(
                                    Id: specId,
                                    ModelName: $"{prov.Name} / {mod.DisplayName ?? mod.ModelId}",
                                    Dimension: mod.Dimension > 0 ? mod.Dimension : 1024,
                                    Metric: "cosine",
                                    MaxTokens: mod.MaxTokens > 0 ? mod.MaxTokens : 8192,
                                    EnableDualChannel: false,
                                    IngestionBackend: "OpenAiApi",
                                    IngestionBaseUrl: prov.BaseUrl,
                                    IngestionApiKey: prov.ApiKey,
                                    SearchBackend: null
                                ));
                                seenIds.Add(specId);
                            }
                        }
                    }
                }
            }
        }

        // 4. 合并 catalog 中已存在的自定义 specs (如有，剔除历史幽灵硬编码)
        var customSpecs = await _catalog.ListEmbeddingSpecsAsync(ct);
        foreach (var cs in customSpecs)
        {
            if (cs.Id == "bge-m3-default" || cs.Id == "bge-small-zh-default" || cs.Id == "openai-small-default" || cs.Id == "minilm-default")
            {
                continue;
            }
            if (!seenIds.Contains(cs.Id))
            {
                result.Add(cs);
                seenIds.Add(cs.Id);
            }
        }

        return result;
    }

    public async Task<KnowledgeBaseDefinition> UpdateKnowledgeBaseAsync(
        string id,
        UpdateKnowledgeBaseRequest request,
        CancellationToken ct = default)
    {
        var existing = await _catalog.GetKnowledgeBaseAsync(id, ct)
            ?? throw new KeyNotFoundException($"知识库不存在: {id}");

        var name = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim();
        var desc = request.Description != null ? request.Description.Trim() : existing.Description;
        var enabled = request.Enabled ?? existing.Enabled;
        var specId = string.IsNullOrWhiteSpace(request.EmbeddingSpecId) ? existing.EmbeddingSpecId : request.EmbeddingSpecId.Trim();

        var updated = existing with
        {
            Name = name,
            Description = desc,
            Enabled = enabled,
            EmbeddingSpecId = specId,
            DesiredConfigRevision = existing.DesiredConfigRevision + 1,
            EffectiveConfigRevision = existing.EffectiveConfigRevision + 1,
            UpdatedAt = DateTime.UtcNow
        };

        await _catalog.SaveKnowledgeBaseAsync(updated, ct);
        _logger?.LogInformation("[KnowledgeBaseService] 知识库已更新: {Id} ({Name})", id, name);
        return updated;
    }

    public async Task<bool> ToggleKnowledgeBaseAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var existing = await _catalog.GetKnowledgeBaseAsync(id, ct);
        if (existing == null) return false;
        var updated = existing with
        {
            Enabled = enabled,
            DesiredConfigRevision = existing.DesiredConfigRevision + 1,
            EffectiveConfigRevision = existing.EffectiveConfigRevision + 1,
            UpdatedAt = DateTime.UtcNow
        };
        await _catalog.SaveKnowledgeBaseAsync(updated, ct);
        _logger?.LogInformation("[KnowledgeBaseService] 知识库启停翻转: {Id} -> {Enabled}", id, enabled);
        return true;
    }

    public async Task<ConnectorDefinition> UpdateConnectorAsync(
        string knowledgeBaseId,
        string connectorId,
        UpdateConnectorRequest request,
        CancellationToken ct = default)
    {
        var connectors = await _catalog.GetConnectorsByKnowledgeBaseAsync(knowledgeBaseId, ct);
        var existing = connectors.FirstOrDefault(c => c.Id == connectorId)
            ?? throw new KeyNotFoundException($"连接器不存在: {connectorId}");

        var targetPath = string.IsNullOrWhiteSpace(request.TargetPath) ? existing.TargetPath : request.TargetPath.Trim();
        var pattern = request.FilePattern != null ? request.FilePattern.Trim() : existing.FilePattern;
        var enabled = request.Enabled ?? existing.Enabled;

        var updated = existing with
        {
            TargetPath = targetPath,
            FilePattern = pattern,
            Enabled = enabled,
            ConfigRevision = existing.ConfigRevision + 1,
            UpdatedAt = DateTime.UtcNow
        };

        await _catalog.SaveConnectorAsync(updated, ct);
        _logger?.LogInformation("[KnowledgeBaseService] 连接器已更新: {Id} (KB: {KbId})", connectorId, knowledgeBaseId);
        return updated;
    }

    public Task<ConnectorDefinition?> GetConnectorAsync(string connectorId, CancellationToken ct = default)
    {
        return _catalog.GetConnectorAsync(connectorId, ct);
    }

    public Task<EmbeddingSpec?> GetEmbeddingSpecAsync(string id, CancellationToken ct = default)
    {
        return _catalog.GetEmbeddingSpecAsync(id, ct);
    }

    public Task SaveEmbeddingSpecAsync(EmbeddingSpec spec, CancellationToken ct = default)
    {
        return _catalog.SaveEmbeddingSpecAsync(spec, ct);
    }

    public Task<bool> DeleteEmbeddingSpecAsync(string specId, CancellationToken ct = default)
    {
        return _catalog.DeleteEmbeddingSpecAsync(specId, ct);
    }

    // ==================== 文档增删改查实现 ====================
    public async Task<(IReadOnlyList<KnowledgeBaseDocumentRecord> Documents, int TotalCount)> GetDocumentsAsync(
        string knowledgeBaseId,
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? connectorId = null,
        CancellationToken ct = default)
    {
        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadOnly, ct);
        return await lease.Store.GetDocumentsAsync(page, pageSize, search, connectorId, ct);
    }

    public async Task<KnowledgeBaseDocumentRecord?> GetDocumentByIdAsync(
        string knowledgeBaseId,
        string documentId,
        CancellationToken ct = default)
    {
        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadOnly, ct);
        return await lease.Store.GetDocumentByIdAsync(documentId, ct);
    }

    public async Task<bool> DeleteDocumentByIdAsync(
        string knowledgeBaseId,
        string documentId,
        CancellationToken ct = default)
    {
        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, ct);
        return await lease.Store.DeleteDocumentByIdAsync(documentId, ct);
    }

    // ==================== 切片增删改查实现 (HITL) ====================
    public async Task<(IReadOnlyList<KnowledgeBaseChunkRecord> Chunks, int TotalCount)> GetChunksAsync(
        string knowledgeBaseId,
        string? documentId = null,
        int page = 1,
        int pageSize = 50,
        string? search = null,
        bool? enabledOnly = null,
        CancellationToken ct = default)
    {
        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadOnly, ct);
        return await lease.Store.GetChunksAsync(documentId, page, pageSize, search, enabledOnly, ct);
    }

    public async Task<KnowledgeBaseChunkRecord?> GetChunkByIdAsync(
        string knowledgeBaseId,
        string chunkId,
        CancellationToken ct = default)
    {
        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadOnly, ct);
        return await lease.Store.GetChunkByIdAsync(chunkId, ct);
    }

    public async Task<KnowledgeBaseChunkRecord> CreateChunkAsync(
        string knowledgeBaseId,
        CreateChunkRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("切片文本不能为空", nameof(request.Text));

        float[]? vector = null;
        if (_embeddingService != null)
        {
            try
            {
                vector = await _embeddingService.EncodeAsync(request.Text.Trim(), EmbeddingMode.Document, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[KnowledgeBaseService] 生成切片嵌入向量失败，切片将以无向量状态保存");
            }
        }

        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, ct);
        return await lease.Store.CreateChunkAsync(request.DocumentId, request.Text.Trim(), request.EmbeddingText, vector, ct);
    }

    public async Task<KnowledgeBaseChunkRecord?> UpdateChunkAsync(
        string knowledgeBaseId,
        string chunkId,
        UpdateChunkRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("切片文本不能为空", nameof(request.Text));

        float[]? vector = null;
        if (_embeddingService != null && request.RecomputeEmbeddingImmediately)
        {
            try
            {
                vector = await _embeddingService.EncodeAsync(request.Text.Trim(), EmbeddingMode.Document, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[KnowledgeBaseService] 生成切片嵌入向量失败，仅更新文本与词法索引");
            }
        }

        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, ct);
        return await lease.Store.UpdateChunkAtomicAsync(chunkId, request.Text.Trim(), request.EmbeddingText, vector, ct);
    }

    public async Task<bool> ToggleChunkAsync(
        string knowledgeBaseId,
        string chunkId,
        bool enabled,
        CancellationToken ct = default)
    {
        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, ct);
        return await lease.Store.ToggleChunkAsync(chunkId, enabled, ct);
    }

    public async Task<bool> DeleteChunkAsync(
        string knowledgeBaseId,
        string chunkId,
        CancellationToken ct = default)
    {
        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, ct);
        return await lease.Store.DeleteChunkByIdAsync(chunkId, ct);
    }
}
