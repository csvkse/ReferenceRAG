using ReferenceRAG.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Connectors;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Jobs;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Runtime;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Models;

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
    private readonly IMarkdownChunker? _markdownChunker;
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
        IEmbeddingService? embeddingService = null,
        IMarkdownChunker? markdownChunker = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _modelRouter = modelRouter;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _configManager = configManager;
        _modelManager = modelManager;
        _embeddingService = embeddingService;
        _markdownChunker = markdownChunker;
    }

    public async Task<KnowledgeBaseDefinition> CreateKnowledgeBaseAsync(
        string name,
        string? description,
        string? embeddingSpecId = null,
        EmbeddingSpec? customSpec = null,
        string? chunkingSpecId = null,
        ChunkingSpec? customChunkingSpec = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("知识库名称不能为空", nameof(name));

        var id = Guid.NewGuid().ToString("N")[..16];
        var now = DateTime.UtcNow;

        // 1. 处理 EmbeddingSpec 固化快照
        string finalEmbeddingSpecId;
        if (customSpec != null)
        {
            await _catalog.SaveEmbeddingSpecAsync(customSpec, ct);
            finalEmbeddingSpecId = customSpec.Id;
        }
        else if (string.Equals(embeddingSpecId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(embeddingSpecId, "lexical", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(embeddingSpecId, "none", StringComparison.OrdinalIgnoreCase))
        {
            finalEmbeddingSpecId = "spec_lexical_only";
        }
        else if (!string.IsNullOrWhiteSpace(embeddingSpecId) && embeddingSpecId != "default-spec")
        {
            finalEmbeddingSpecId = embeddingSpecId;
            var existingSpec = await _catalog.GetEmbeddingSpecAsync(finalEmbeddingSpecId, ct);
            if (existingSpec == null)
            {
                var allSpecs = await ListEmbeddingSpecsAsync(ct);
                var matched = allSpecs.FirstOrDefault(s => s.Id == finalEmbeddingSpecId);
                if (matched != null)
                {
                    await _catalog.SaveEmbeddingSpecAsync(matched, ct);
                }
            }
        }
        else
        {
            // 原型快照：从全局当前模型配置复制并固化；若全局未配置模型则优雅回退至纯词法
            var globalCfg = _configManager?.Load()?.Embedding;
            if (string.IsNullOrWhiteSpace(globalCfg?.ModelName) || globalCfg.ModelName.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                finalEmbeddingSpecId = "spec_lexical_only";
            }
            else
            {
                var snapshotSpecId = $"spec_emb_{id}";
                var snapshotSpec = new EmbeddingSpec(
                    Id: snapshotSpecId,
                    ModelName: globalCfg?.ModelName ?? "default-model",
                    Dimension: _embeddingService?.Dimension > 0 ? _embeddingService.Dimension : 1024,
                    Metric: "cosine",
                    MaxTokens: 512,
                    IngestionBackend: globalCfg?.ActiveBackend ?? "local",
                    SearchBackend: globalCfg?.ActiveBackend ?? "local"
                );
                await _catalog.SaveEmbeddingSpecAsync(snapshotSpec, ct);
                finalEmbeddingSpecId = snapshotSpecId;
            }
        }

        // 2. 处理 ChunkingSpec 固化快照
        string finalChunkingSpecId;
        if (customChunkingSpec != null)
        {
            await _catalog.SaveChunkingSpecAsync(customChunkingSpec, ct);
            finalChunkingSpecId = customChunkingSpec.Id;
        }
        else if (string.Equals(finalEmbeddingSpecId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase) &&
                 (string.IsNullOrWhiteSpace(chunkingSpecId) || chunkingSpecId == "spec_chunk_default"))
        {
            finalChunkingSpecId = "spec_chunk_lexical_paragraph";
        }
        else if (!string.IsNullOrWhiteSpace(chunkingSpecId) && chunkingSpecId != "spec_chunk_default")
        {
            finalChunkingSpecId = chunkingSpecId;
        }
        else
        {
            // 原型快照：从全局当前自适应分块配置固化
            var globalChunk = _configManager?.Load()?.Chunking;
            var snapshotChunkId = $"spec_chunk_{id}";
            var snapshotChunkSpec = new ChunkingSpec(
                Id: snapshotChunkId,
                Mode: globalChunk?.Mode ?? "Adaptive",
                StrategyType: globalChunk?.AdaptiveStrategyType ?? "Balanced",
                AdaptiveLevel: globalChunk?.AdaptiveLevel ?? "Level2",
                MaxTokens: globalChunk?.MaxTokens > 0 ? globalChunk.MaxTokens : 512,
                MinTokens: globalChunk?.MinTokens > 0 ? globalChunk.MinTokens : 50,
                OverlapTokens: globalChunk?.OverlapTokens > 0 ? globalChunk.OverlapTokens : 50,
                TargetWindowUtilizationPercent: globalChunk?.TargetWindowUtilizationPercent > 0 ? globalChunk.TargetWindowUtilizationPercent : 70,
                PreserveCodeBlocks: globalChunk?.PreserveCodeBlocks ?? true,
                PreserveTables: true,
                PreserveHeadings: globalChunk?.PreserveHeadings ?? true
            );
            await _catalog.SaveChunkingSpecAsync(snapshotChunkSpec, ct);
            finalChunkingSpecId = snapshotChunkId;
        }

        // 校验 ChunkingSpec 与 EmbeddingSpec 的相容性（模型刚性物理上限守卫）
        var chunkSpecToValidate = customChunkingSpec
            ?? (finalChunkingSpecId.StartsWith("spec_chunk_") ? await _catalog.GetChunkingSpecAsync(finalChunkingSpecId, ct) : null)
            ?? await _catalog.GetChunkingSpecAsync(finalChunkingSpecId, ct);
        if (chunkSpecToValidate != null)
        {
            await ValidateChunkingSpecAgainstModelAsync(chunkSpecToValidate, finalEmbeddingSpecId, ct);
        }

        var kb = new KnowledgeBaseDefinition(
            Id: id,
            Name: name.Trim(),
            Description: description?.Trim(),
            Enabled: true,
            DesiredConfigRevision: 1,
            EffectiveConfigRevision: 1,
            EmbeddingSpecId: finalEmbeddingSpecId,
            CreatedAt: now,
            UpdatedAt: now,
            ChunkingSpecId: finalChunkingSpecId
        );

        // 初始化底层存储
        await using var lease = await _storageManager.AcquireLeaseAsync(id, StorageAccess.ReadWrite, ct);
        await lease.Store.InitializeAsync(ct);

        await _catalog.SaveKnowledgeBaseAsync(kb, ct);
        _logger?.LogInformation("[KnowledgeBaseService] 创建知识库成功: {Id} ({Name}), EmbeddingSpec: {EmbSpec}, ChunkingSpec: {ChunkSpec}",
            id, name, finalEmbeddingSpecId, finalChunkingSpecId);
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

    public async Task<(ConnectorDefinition Connector, bool Created)> EnsureConnectorAsync(
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

        var key = NormalizePathKey(targetPath);
        var existing = (await _catalog.GetConnectorsByKnowledgeBaseAsync(knowledgeBaseId, ct))
            .FirstOrDefault(c => NormalizePathKey(c.TargetPath) == key);

        if (existing != null)
        {
            // 已有连接器：仅在原规则为空时补齐，不覆盖用户配置
            if (!string.IsNullOrWhiteSpace(filePattern) && string.IsNullOrWhiteSpace(existing.FilePattern))
            {
                var patched = existing with { FilePattern = filePattern, UpdatedAt = DateTime.UtcNow };
                await _catalog.SaveConnectorAsync(patched, ct);
                return (patched, false);
            }
            return (existing, false);
        }

        try
        {
            var created = await AddConnectorAsync(knowledgeBaseId, kind, targetPath, filePattern, ct);
            return (created, true);
        }
        catch (Exception)
        {
            // 并发插入触发唯一索引冲突时回查已存在记录，保持幂等
            var raced = (await _catalog.GetConnectorsByKnowledgeBaseAsync(knowledgeBaseId, ct))
                .FirstOrDefault(c => NormalizePathKey(c.TargetPath) == key);
            if (raced != null) return (raced, false);
            throw;
        }
    }

    private static string NormalizePathKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }

    public async Task<IReadOnlyList<ConnectorDetailDto>> GetConnectorsAsync(string knowledgeBaseId, CancellationToken ct = default)
    {
        var rawConnectors = await _catalog.GetConnectorsByKnowledgeBaseAsync(knowledgeBaseId, ct);
        if (rawConnectors.Count == 0) return Array.Empty<ConnectorDetailDto>();

        var result = new List<ConnectorDetailDto>(rawConnectors.Count);
        try
        {
            await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadOnly, ct);
            foreach (var c in rawConnectors)
            {
                var (state, isReachable, diagMsg) = ProbeDirectory(c.TargetPath, c.Kind);
                var (docCount, chunkCount) = await lease.Store.GetConnectorStatsAsync(c.Id, ct);
                result.Add(new ConnectorDetailDto(
                    Id: c.Id,
                    KnowledgeBaseId: c.KnowledgeBaseId,
                    Kind: c.Kind,
                    TargetPath: c.TargetPath,
                    FilePattern: c.FilePattern,
                    ConfigRevision: c.ConfigRevision,
                    Enabled: c.Enabled,
                    CreatedAt: c.CreatedAt,
                    UpdatedAt: c.UpdatedAt,
                    DocumentCount: docCount,
                    ChunkCount: chunkCount,
                    IsReachable: isReachable,
                    Reachability: state,
                    DiagnosticMessage: diagMsg
                ));
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[KnowledgeBaseService] 获取连接器聚合统计降级: {Message}", ex.Message);
            result.Clear();
            foreach (var c in rawConnectors)
            {
                var (state, isReachable, diagMsg) = ProbeDirectory(c.TargetPath, c.Kind);
                result.Add(new ConnectorDetailDto(
                    Id: c.Id,
                    KnowledgeBaseId: c.KnowledgeBaseId,
                    Kind: c.Kind,
                    TargetPath: c.TargetPath,
                    FilePattern: c.FilePattern,
                    ConfigRevision: c.ConfigRevision,
                    Enabled: c.Enabled,
                    CreatedAt: c.CreatedAt,
                    UpdatedAt: c.UpdatedAt,
                    DocumentCount: 0,
                    ChunkCount: 0,
                    IsReachable: isReachable,
                    Reachability: state,
                    DiagnosticMessage: diagMsg ?? $"统计暂不可用: {ex.Message}"
                ));
            }
            return result;
        }
    }

    private static (ConnectorReachabilityState State, bool IsReachable, string? Message) ProbeDirectory(string targetPath, string kind)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return (ConnectorReachabilityState.Unreachable, false, "路径为空");

        var cleanPath = targetPath.Trim();
        try
        {
            if (string.Equals(kind, "singlefile", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "file", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(cleanPath))
                    return (ConnectorReachabilityState.Reachable, true, null);
                return (ConnectorReachabilityState.Unreachable, false, "文件不存在");
            }

            if (Directory.Exists(cleanPath))
            {
                try
                {
                    _ = Directory.EnumerateFileSystemEntries(cleanPath).Take(1).ToList();
                    return (ConnectorReachabilityState.Reachable, true, null);
                }
                catch (UnauthorizedAccessException)
                {
                    return (ConnectorReachabilityState.AccessDenied, false, "目录存在但当前进程无访问/遍历权限");
                }
            }

            return (ConnectorReachabilityState.Unreachable, false, "目录不存在");
        }
        catch (Exception ex)
        {
            return (ConnectorReachabilityState.Unknown, false, $"探测发生异常: {ex.Message}");
        }
    }

    public async Task<bool> DeleteConnectorAsync(string knowledgeBaseId, string connectorId, bool cascade = true, CancellationToken ct = default)
    {
        if (cascade)
        {
            try
            {
                await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, ct);
                await lease.Store.PurgeConnectorDataAsync(connectorId, ct);
                _logger?.LogInformation("[KnowledgeBaseService] 已物理级联清空连接器 {ConnectorId} 对应全部数据", connectorId);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[KnowledgeBaseService] 级联清除连接器 {ConnectorId} 物理数据异常: {Message}", connectorId, ex.Message);
            }
        }
        return await _catalog.DeleteConnectorAsync(connectorId, ct);
    }

    public async Task<PathsResponse> GetPathsAsync(IReadOnlyList<string> knowledgeBaseIds, CancellationToken ct = default)
    {
        var response = new PathsResponse();
        var targetIds = knowledgeBaseIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? new List<string>();

        if (targetIds.Count == 0)
        {
            var allKbs = await _catalog.ListKnowledgeBasesAsync(ct);
            targetIds = allKbs.Where(k => k.Enabled).Select(k => k.Id).ToList();
        }

        var isMultiKb = targetIds.Count > 1;
        foreach (var kbId in targetIds)
        {
            try
            {
                var kb = await _catalog.GetKnowledgeBaseAsync(kbId, ct);
                if (kb == null || !kb.Enabled) continue;

                var connectors = await _catalog.GetConnectorsByKnowledgeBaseAsync(kbId, ct);
                if (connectors.Count == 0) continue;

                await using var lease = await _storageManager.AcquireLeaseAsync(kbId, StorageAccess.ReadOnly, ct);
                var paths = await lease.Store.GetPathsAsync(connectors, ct);

                foreach (var p in paths)
                {
                    if (isMultiKb)
                    {
                        p.Name = $"{kb.Name} - {p.Name}";
                    }
                    response.Sources.Add(p);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[KnowledgeBaseService] 获取知识库 {KbId} 路径失败", kbId);
            }
        }

        return response;
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
                var worker = new KnowledgeBaseSyncWorker(_catalog, _storageManager, _modelRouter, workerLogger, _loggerFactory, enableEmbedding, _markdownChunker);
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

    public Task<bool> DeleteJobAsync(string jobId, CancellationToken ct = default)
    {
        return _catalog.DeleteJobAsync(jobId, ct);
    }

    public Task<int> PruneJobsAsync(string? knowledgeBaseId = null, TimeSpan? olderThan = null, CancellationToken ct = default)
    {
        return _catalog.PruneJobsAsync(knowledgeBaseId, olderThan, ct);
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
        var chunkSpecId = string.IsNullOrWhiteSpace(request.ChunkingSpecId) ? existing.ChunkingSpecId : request.ChunkingSpecId.Trim();

        // 校验变更的分段规格与当前模型的相容性
        if (!string.IsNullOrWhiteSpace(request.ChunkingSpecId) && !string.Equals(request.ChunkingSpecId.Trim(), existing.ChunkingSpecId, StringComparison.OrdinalIgnoreCase))
        {
            var targetChunkSpec = await _catalog.GetChunkingSpecAsync(request.ChunkingSpecId.Trim(), ct)
                ?? (await ListChunkingSpecsAsync(ct)).FirstOrDefault(s => s.Id.Equals(request.ChunkingSpecId.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"分段配置未找到: {request.ChunkingSpecId}");
            await ValidateChunkingSpecAgainstModelAsync(targetChunkSpec, specId, ct);
        }

        var updated = existing with
        {
            Name = name,
            Description = desc,
            Enabled = enabled,
            EmbeddingSpecId = specId,
            ChunkingSpecId = chunkSpecId,
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

    public async Task<KnowledgeBaseDocumentRecord> CreateDocumentAsync(
        string knowledgeBaseId,
        string? connectorId,
        string? title,
        string content,
        string? sourceKey = null,
        CancellationToken ct = default)
    {
        var targetConnectorId = !string.IsNullOrWhiteSpace(connectorId) ? connectorId : "manual";
        var finalSourceKey = !string.IsNullOrWhiteSpace(sourceKey) ? sourceKey : (string.IsNullOrWhiteSpace(title) ? $"doc_{Guid.NewGuid():N}"[..12] + ".md" : title);

        var kb = await _catalog.GetKnowledgeBaseAsync(knowledgeBaseId, ct);
        ChunkingSpec? spec = null;
        if (kb != null && !string.IsNullOrWhiteSpace(kb.ChunkingSpecId))
        {
            spec = await _catalog.GetChunkingSpecAsync(kb.ChunkingSpecId, ct);
        }
        spec ??= await _catalog.GetChunkingSpecAsync("spec_chunk_default", ct);

        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, ct);
        return await lease.Store.CreateDocumentAsync(targetConnectorId, finalSourceKey, title, content, spec, ct);
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

    // ==================== ChunkingSpec 规格管理 ====================
    public Task<IReadOnlyList<ChunkingSpec>> ListChunkingSpecsAsync(CancellationToken ct = default)
    {
        return _catalog.ListChunkingSpecsAsync(ct);
    }

    public Task<ChunkingSpec?> GetChunkingSpecAsync(string id, CancellationToken ct = default)
    {
        return _catalog.GetChunkingSpecAsync(id, ct);
    }

    public Task SaveChunkingSpecAsync(ChunkingSpec spec, CancellationToken ct = default)
    {
        if (spec.MaxTokens <= 0)
            throw new ArgumentException("分段最大Token数必须大于0", nameof(spec.MaxTokens));
        if (spec.MinTokens < 0 || spec.MinTokens >= spec.MaxTokens)
            throw new ArgumentException("分段最小Token数不能小于0且必须小于最大Token数", nameof(spec.MinTokens));
        return _catalog.SaveChunkingSpecAsync(spec, ct);
    }

    public Task<bool> DeleteChunkingSpecAsync(string specId, CancellationToken ct = default)
    {
        return _catalog.DeleteChunkingSpecAsync(specId, ct);
    }

    // ==================== 配置漂移与升级诊断 ====================
    public async Task<ConfigDriftResult> CheckConfigDriftAsync(string knowledgeBaseId, CancellationToken ct = default)
    {
        var kb = await _catalog.GetKnowledgeBaseAsync(knowledgeBaseId, ct);
        if (kb == null)
            throw new KeyNotFoundException($"知识库不存在: {knowledgeBaseId}");

        var currentSpec = await _catalog.GetEmbeddingSpecAsync(kb.EmbeddingSpecId, ct);
        var currentChunkSpec = await _catalog.GetChunkingSpecAsync(kb.ChunkingSpecId, ct);

        var globalConfig = _configManager?.Load();
        var globalModel = globalConfig?.Embedding?.ModelName ?? "default-model";
        var globalChunkMode = globalConfig?.Chunking?.AdaptiveStrategyType ?? "Balanced";

        var currentModel = currentSpec?.ModelName ?? "unknown";
        var currentChunk = currentChunkSpec?.StrategyType ?? "Balanced";

        // 纯词法知识库判定：免模型是主动设定，状态为 Aligned，提示可随时绑定模型
        if (string.Equals(kb.EmbeddingSpecId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase))
        {
            return new ConfigDriftResult(
                KnowledgeBaseId: knowledgeBaseId,
                Status: DriftStatus.Aligned,
                CurrentEmbeddingModel: "Pure Lexical (FTS5 BM25)",
                GlobalDefaultEmbeddingModel: globalModel,
                CurrentChunkingStrategy: currentChunk,
                GlobalChunkingStrategy: globalChunkMode,
                CanUpgrade: true,
                UpgradeRecommendation: "当前知识库为纯词法模式（零显存消耗）。可随时绑定目标向量模型平滑升级为混合检索。"
            );
        }

        bool modelMatches = string.Equals(currentModel, globalModel, StringComparison.OrdinalIgnoreCase);
        bool chunkMatches = string.Equals(currentChunk, globalChunkMode, StringComparison.OrdinalIgnoreCase);

        DriftStatus status;
        string? recommendation;
        bool canUpgrade = false;

        if (modelMatches && chunkMatches)
        {
            status = DriftStatus.Aligned;
            recommendation = "知识库规格与全局配置完全对齐，无需更新。";
        }
        else if (!modelMatches)
        {
            status = DriftStatus.Drifted;
            canUpgrade = true;
            recommendation = $"全局默认模型已演进至 [{globalModel}]，当前知识库仍固化在历史版本 [{currentModel}]。建议执行蓝绿影子重构索引进行升级。";
        }
        else
        {
            status = DriftStatus.Customized;
            recommendation = $"知识库使用了独立自定义的分块策略 [{currentChunk}]。";
        }

        return new ConfigDriftResult(
            KnowledgeBaseId: knowledgeBaseId,
            Status: status,
            CurrentEmbeddingModel: currentModel,
            GlobalDefaultEmbeddingModel: globalModel,
            CurrentChunkingStrategy: currentChunk,
            GlobalChunkingStrategy: globalChunkMode,
            CanUpgrade: canUpgrade,
            UpgradeRecommendation: recommendation
        );
    }

    // ==================== 蓝绿影子重构索引 ====================
    public async Task<ModelReindexResult> TriggerModelReindexAsync(
        string knowledgeBaseId,
        string? targetSpecId = null,
        CancellationToken ct = default)
    {
        var kb = await _catalog.GetKnowledgeBaseAsync(knowledgeBaseId, ct);
        if (kb == null)
            throw new KeyNotFoundException($"知识库不存在: {knowledgeBaseId}");

        EmbeddingSpec? targetSpec = null;
        if (!string.IsNullOrWhiteSpace(targetSpecId))
        {
            targetSpec = await _catalog.GetEmbeddingSpecAsync(targetSpecId, ct);
        }
        if (targetSpec == null)
        {
            var allSpecs = await ListEmbeddingSpecsAsync(ct);
            targetSpec = allSpecs.FirstOrDefault(s => s.Id == targetSpecId) ?? allSpecs.FirstOrDefault();
        }
        if (targetSpec == null)
        {
            throw new InvalidOperationException("未找到可用的目标向量模型规格。");
        }

        // 校验目标模型上下文长度是否兼容当前知识库的分段 Token 上限（反向相容性防御门禁）
        var currentChunkSpec = await _catalog.GetChunkingSpecAsync(kb.ChunkingSpecId, ct)
            ?? await _catalog.GetChunkingSpecAsync("spec_chunk_default", ct);
        if (currentChunkSpec != null)
        {
            int targetLimit = targetSpec.MaxTokens > 0 ? targetSpec.MaxTokens : 512;
            var targetName = (targetSpec.ModelName ?? "").ToLowerInvariant();
            if (targetName.Contains("bge-m3") || targetName.Contains("jina") || targetName.Contains("text-embedding-3"))
            {
                targetLimit = Math.Max(targetLimit, 8192);
            }
            else if (targetName.Contains("small") || targetName.Contains("minilm") || targetName.Contains("text2vec"))
            {
                targetLimit = Math.Min(targetLimit, 512);
            }

            if (currentChunkSpec.MaxTokens > targetLimit)
            {
                return new ModelReindexResult(
                    KnowledgeBaseId: knowledgeBaseId,
                    TargetEmbeddingSpecId: targetSpec.Id,
                    ProcessedChunks: 0,
                    Success: false,
                    Message: $"目标模型 '{targetSpec.ModelName}' 最大上下文上限 ({targetLimit} tokens) 小于当前知识库分段规格 ({currentChunkSpec.MaxTokens} tokens)。切换后将引发严重截断或向量生成失败，请先调整知识库分段规格并重新分段。"
                );
            }
        }

        await _catalog.SaveEmbeddingSpecAsync(targetSpec, ct);

        Func<IReadOnlyList<string>, Task<IReadOnlyList<float[]>>> vectorProvider = async texts =>
        {
            if (_modelRouter != null)
            {
                try
                {
                    await using var modelLease = await _modelRouter.AcquireLeaseAsync<IEmbeddingBackendAdapter>(
                        new ModelSelection(targetSpec.Id, "embedding"),
                        new WorkloadContext($"reindex_{knowledgeBaseId}", Priority: -1),
                        ct);
                    if (modelLease?.Client != null)
                    {
                        var rawVectors = await modelLease.Client.EncodeBatchAsync(texts, null, ct);
                        return rawVectors;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "通过 ModelRouter 获取目标模型适配器编码失败，尝试回退全局嵌入服务");
                }
            }

            if (_embeddingService != null)
            {
                var res = await _embeddingService.EncodeBatchAsync(texts, EmbeddingMode.Document, ct);
                return res;
            }
            var dim = targetSpec.Dimension > 0 ? targetSpec.Dimension : 1024;
            return texts.Select(_ => new float[dim]).ToList();
        };

        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, ct);
        var processed = await lease.Store.ExecuteShadowModelReindexAsync(
            vectorProvider,
            targetSpec.Id,
            targetSpec.Dimension,
            ct);

        var updatedKb = kb with
        {
            EmbeddingSpecId = targetSpec.Id,
            DesiredConfigRevision = kb.DesiredConfigRevision + 1,
            EffectiveConfigRevision = kb.EffectiveConfigRevision + 1,
            UpdatedAt = DateTime.UtcNow
        };
        await _catalog.SaveKnowledgeBaseAsync(updatedKb, ct);

        _logger?.LogInformation("[KnowledgeBaseService] 知识库 {KbId} 蓝绿影子重构索引完成，处理 {Count} 个切片至规格 {Spec}",
            knowledgeBaseId, processed, targetSpec.Id);

        return new ModelReindexResult(
            KnowledgeBaseId: knowledgeBaseId,
            TargetEmbeddingSpecId: targetSpec.Id,
            ProcessedChunks: processed,
            Success: true,
            Message: $"成功完成蓝绿影子重构索引，已处理并迁移 {processed} 个切片向量至规格 [{targetSpec.Id}]。"
        );
    }

    // ==================== 全库重新分段与重索引 ====================
    public async Task<RechunkResult> TriggerRechunkAsync(
        string knowledgeBaseId,
        string? targetChunkingSpecId = null,
        CancellationToken ct = default)
    {
        var kb = await _catalog.GetKnowledgeBaseAsync(knowledgeBaseId, ct)
            ?? throw new KeyNotFoundException($"知识库未找到: {knowledgeBaseId}");

        ChunkingSpec? targetChunkSpec = null;
        if (!string.IsNullOrWhiteSpace(targetChunkingSpecId))
        {
            targetChunkSpec = await _catalog.GetChunkingSpecAsync(targetChunkingSpecId.Trim(), ct);
            if (targetChunkSpec == null)
            {
                var allSpecs = await ListChunkingSpecsAsync(ct);
                targetChunkSpec = allSpecs.FirstOrDefault(s => s.Id.Equals(targetChunkingSpecId.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (targetChunkSpec == null)
                throw new KeyNotFoundException($"分段配置未找到: {targetChunkingSpecId}");
        }
        else
        {
            targetChunkSpec = await _catalog.GetChunkingSpecAsync(kb.ChunkingSpecId, ct)
                ?? await _catalog.GetChunkingSpecAsync("spec_chunk_default", ct);
        }

        if (targetChunkSpec == null)
        {
            throw new InvalidOperationException("未找到可用的分段规格。");
        }

        // 校验目标分段规格与当前嵌入模型的相容性
        await ValidateChunkingSpecAgainstModelAsync(targetChunkSpec, kb.EmbeddingSpecId, ct);

        // 更新知识库分段规格绑定
        var updatedKb = kb with
        {
            ChunkingSpecId = targetChunkSpec.Id,
            DesiredConfigRevision = kb.DesiredConfigRevision + 1,
            EffectiveConfigRevision = kb.EffectiveConfigRevision + 1,
            UpdatedAt = DateTime.UtcNow
        };
        await _catalog.SaveKnowledgeBaseAsync(updatedKb, ct);

        // 触发连接器全量重新同步与切分
        var connectors = await _catalog.GetConnectorsByKnowledgeBaseAsync(knowledgeBaseId, ct);
        int processedDocs = 0;

        foreach (var connDef in connectors)
        {
            if (!connDef.Enabled) continue;
            await TriggerSyncJobAsync(knowledgeBaseId, connDef.Id, ct);
            processedDocs++;
        }

        int processedChunks = 0;
        try
        {
            await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadOnly, ct);
            var (_, totalCount) = await lease.Store.GetChunksAsync(pageSize: 1, ct: ct);
            processedChunks = totalCount;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "查询知识库切片数统计异常: {Msg}", ex.Message);
        }

        _logger?.LogInformation("[KnowledgeBaseService] 触发全库重新分段完成: KB {Id}, Spec {SpecId}", knowledgeBaseId, targetChunkSpec.Id);
        return new RechunkResult(
            KnowledgeBaseId: knowledgeBaseId,
            TargetChunkingSpecId: targetChunkSpec.Id,
            ProcessedDocuments: processedDocs,
            ProcessedChunks: processedChunks,
            Success: true,
            Message: $"已成功应用分段规格 [{targetChunkSpec.Id}] 并启动全库重新切分与向量索引任务。"
        );
    }

    public async Task<KnowledgeBaseDefinition> UnbindModelAsync(string knowledgeBaseId, CancellationToken ct = default)
    {
        var kb = await _catalog.GetKnowledgeBaseAsync(knowledgeBaseId, ct)
            ?? throw new KeyNotFoundException($"知识库不存在: {knowledgeBaseId}");

        var updatedKb = kb with
        {
            EmbeddingSpecId = "spec_lexical_only",
            DesiredConfigRevision = kb.DesiredConfigRevision + 1,
            EffectiveConfigRevision = kb.EffectiveConfigRevision + 1,
            UpdatedAt = DateTime.UtcNow
        };

        await _catalog.SaveKnowledgeBaseAsync(updatedKb, ct);
        _logger?.LogInformation("[KnowledgeBaseService] 知识库 {Id} 已解绑模型，成功切换为纯词法模式 (spec_lexical_only)", knowledgeBaseId);
        return updatedKb;
    }

    private async Task ValidateChunkingSpecAgainstModelAsync(
        ChunkingSpec chunkSpec,
        string embeddingSpecId,
        CancellationToken ct = default)
    {
        if (chunkSpec == null) return;

        // 1. 纯词法知识库特殊放行与安全边界守卫
        if (string.Equals(embeddingSpecId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase))
        {
            if (chunkSpec.MaxTokens > 2048)
            {
                throw new InvalidOperationException(
                    $"纯词法分段配置不合法: 分段最大Token数 ({chunkSpec.MaxTokens}) 超过了认知检索与下游 Agent 上下文建议安全上限 (2048)。过大切片会导致 BM25 长度惩罚失效与召回噪声稀释，请调整至 2048 以内（推荐 400~800 Tokens）。");
            }
            if (chunkSpec.MaxTokens < 100)
            {
                throw new InvalidOperationException($"纯词法分段配置不合法: 分段最大Token数 ({chunkSpec.MaxTokens}) 过小，会导致词汇共现密度不足，必须 >= 100 Tokens。");
            }
            return;
        }

        var embSpec = await _catalog.GetEmbeddingSpecAsync(embeddingSpecId, ct);
        if (embSpec == null)
        {
            var allSpecs = await ListEmbeddingSpecsAsync(ct);
            embSpec = allSpecs.FirstOrDefault(s => s.Id.Equals(embeddingSpecId, StringComparison.OrdinalIgnoreCase));
        }

        if (string.Equals(embSpec?.Id, "spec_lexical_only", StringComparison.OrdinalIgnoreCase) || embSpec?.Dimension == 0)
        {
            if (chunkSpec.MaxTokens > 2048)
            {
                throw new InvalidOperationException(
                    $"纯词法分段配置不合法: 分段最大Token数 ({chunkSpec.MaxTokens}) 超过了认知检索与下游 Agent 上下文建议安全上限 (2048)。过大切片会导致 BM25 长度惩罚失效与召回噪声稀释，请调整至 2048 以内（推荐 400~800 Tokens）。");
            }
            if (chunkSpec.MaxTokens < 100)
            {
                throw new InvalidOperationException($"纯词法分段配置不合法: 分段最大Token数 ({chunkSpec.MaxTokens}) 过小，会导致词汇共现密度不足，必须 >= 100 Tokens。");
            }
            return;
        }

        int modelLimit = embSpec?.MaxTokens > 0 ? embSpec.MaxTokens : 512;
        var modelName = embSpec?.ModelName ?? embeddingSpecId;

        var lowerModelName = modelName.ToLowerInvariant();
        if (lowerModelName.Contains("bge-m3") || lowerModelName.Contains("jina") || lowerModelName.Contains("text-embedding-3"))
        {
            modelLimit = Math.Max(modelLimit, 8192);
        }
        else if (lowerModelName.Contains("small") || lowerModelName.Contains("minilm") || lowerModelName.Contains("text2vec"))
        {
            modelLimit = Math.Min(modelLimit, 512);
        }

        if (chunkSpec.MaxTokens > modelLimit)
        {
            throw new InvalidOperationException(
                $"分段配置不合法: 分段最大Token数 ({chunkSpec.MaxTokens}) 超过了当前绑定模型 '{modelName}' 的物理上下文上限 ({modelLimit})。必须先配置有效兼容的长上下文模型，或将分段大小调小至 {modelLimit} 以内。");
        }
    }
}
