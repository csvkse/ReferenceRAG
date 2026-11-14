using ReferenceRAG.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Connectors;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Jobs;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Runtime;
using ReferenceRAG.Core.Features.ModelManagement.Services;
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
    private readonly KnowledgeBaseJobCoordinator _coordinator;
    private readonly IEmbeddingAdapterResolver? _adapterResolver;

    public KnowledgeBaseService(
        ICatalogStore catalog,
        IKnowledgeBaseStorageManager storageManager,
        IModelRouter? modelRouter = null,
        ILogger<KnowledgeBaseService>? logger = null,
        ILoggerFactory? loggerFactory = null,
        ConfigManager? configManager = null,
        IModelManager? modelManager = null,
        IEmbeddingService? embeddingService = null,
        IMarkdownChunker? markdownChunker = null,
        KnowledgeBaseJobCoordinator? jobCoordinator = null,
        IEmbeddingAdapterResolver? adapterResolver = null)
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
        _coordinator = jobCoordinator ?? new KnowledgeBaseJobCoordinator();
        _adapterResolver = adapterResolver;
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

    // ==================== P2：事件驱动与漂移治理（remaining-plan §1） ====================

    /// <summary>
    /// 只读对账：connectors（事实） vs config.Sources（投影）。
    /// 产出 Orphaned（物理目录消失且无候选）/ Drifted（消失但有疑似改名候选）/ Unlinked（残留投影）。
    /// 首版只报告，不删除连接器、不清理语料。
    /// </summary>
    public async Task<SourceLinkReconcileReport> ReconcileAsync(CancellationToken ct = default)
    {
        var kbs = await _catalog.ListKnowledgeBasesAsync(ct);
        var connectors = new List<(KnowledgeBaseDefinition Kb, ConnectorDefinition Connector)>();
        foreach (var kb in kbs)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var conn in await _catalog.GetConnectorsByKnowledgeBaseAsync(kb.Id, ct))
                connectors.Add((kb, conn));
        }

        var configSources = _configManager?.Load()?.Sources?.ToList() ?? new List<SourceFolder>();
        var linkedKeys = new HashSet<string>(
            connectors.Select(x => NormalizePathKey(x.Connector.TargetPath)),
            StringComparer.OrdinalIgnoreCase);
        var issues = new List<SourceLinkDriftIssue>();

        // 1) 事实侧：连接器指向的物理目录是否还在
        foreach (var group in connectors.GroupBy(x => NormalizePathKey(x.Connector.TargetPath)))
        {
            ct.ThrowIfCancellationRequested();
            var path = group.First().Connector.TargetPath;
            if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path)) continue;

            var candidates = FindRebindCandidates(path, linkedKeys);
            foreach (var (kb, connector) in group)
            {
                if (candidates.Count > 0)
                {
                    issues.Add(new SourceLinkDriftIssue(
                        SourceLinkDriftKind.Drifted,
                        path,
                        $"物理目录不存在，检测到 {candidates.Count} 个疑似改名/迁移的候选目录；确认后可重绑（保留原连接器与索引数据）。",
                        KnowledgeBaseId: kb.Id,
                        KnowledgeBaseName: kb.Name,
                        ConnectorId: connector.Id,
                        RebindCandidates: candidates));
                }
                else
                {
                    issues.Add(new SourceLinkDriftIssue(
                        SourceLinkDriftKind.Orphaned,
                        path,
                        "连接器指向的物理目录已不存在（孤儿连接器），可在确认后一键清理。",
                        KnowledgeBaseId: kb.Id,
                        KnowledgeBaseName: kb.Name,
                        ConnectorId: connector.Id));
                }
            }
        }

        // 2) 投影侧：config.Sources 中是否存在没有任何连接器指向的残留行
        foreach (var source in configSources)
        {
            if (linkedKeys.Contains(NormalizePathKey(source.Path))) continue;
            issues.Add(new SourceLinkDriftIssue(
                SourceLinkDriftKind.Unlinked,
                source.Path,
                "源仍在配置中，但已未挂载到任何知识库（解绑后的残留投影）。",
                SourceName: source.Name));
        }

        var report = new SourceLinkReconcileReport(
            DateTime.UtcNow, kbs.Count, connectors.Count, configSources.Count, issues);
        _logger?.LogInformation(
            "[KnowledgeBaseService] 对账完成: KB={KbCount} Connector={ConnCount} Source={SourceCount} Issues={IssueCount}",
            kbs.Count, connectors.Count, configSources.Count, issues.Count);
        return report;
    }

    /// <summary>
    /// 在缺失目录的同级下寻找「名称相似」的候选目录（模拟用户把 foo 改成 bar 的场景）。
    /// 只做名称相似度（Dice 字符二元组），不递归扫描、不比较体积，避免对账变成重 IO。
    /// </summary>
    private static IReadOnlyList<string> FindRebindCandidates(string missingPath, ISet<string> linkedKeys)
    {
        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(missingPath);
            var parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return Array.Empty<string>();
            var leaf = Path.GetFileName(trimmed);

            return Directory.EnumerateDirectories(parent)
                .Take(256)
                .Where(d => !linkedKeys.Contains(NormalizePathKey(d)))
                .Select(d => (Path: d, Score: NameSimilarity(leaf, Path.GetFileName(Path.TrimEndingDirectorySeparator(d)))))
                .Where(x => x.Score >= 0.6)
                .OrderByDescending(x => x.Score)
                .Take(3)
                .Select(x => x.Path)
                .ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static double NameSimilarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 0.8;

        var bigrams = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < a.Length - 1; i++)
        {
            var g = a.Substring(i, 2);
            bigrams[g] = bigrams.GetValueOrDefault(g) + 1;
        }
        var matches = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var g = b.Substring(i, 2);
            if (bigrams.TryGetValue(g, out var remaining) && remaining > 0)
            {
                matches++;
                bigrams[g] = remaining - 1;
            }
        }
        var total = Math.Max(1, (a.Length - 1) + (b.Length - 1));
        return 2.0 * matches / total;
    }

    /// <summary>
    /// 确认重绑：原地更新 target_path（CatalogStore.SaveConnectorAsync 的 ON CONFLICT(id) DO UPDATE
    /// 会同步刷新 path_key），保留 connectorId 与全部已索引语料。
    /// 若目标路径已被同 KB 的另一连接器占用，则拒绝，避免触发 ux_connectors_kb_path 唯一约束。
    /// </summary>
    public async Task<ConnectorDefinition> RebindConnectorAsync(
        string knowledgeBaseId,
        string connectorId,
        string newTargetPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newTargetPath))
            throw new ArgumentException("新路径不能为空", nameof(newTargetPath));
        if (!Directory.Exists(newTargetPath))
            throw new InvalidOperationException($"目标目录不存在或不可访问: {newTargetPath}");

        var connector = await _catalog.GetConnectorAsync(connectorId, ct);
        if (connector == null || !string.Equals(connector.KnowledgeBaseId, knowledgeBaseId, StringComparison.OrdinalIgnoreCase))
            throw new KeyNotFoundException($"连接器未找到: {connectorId}");

        var key = NormalizePathKey(newTargetPath);
        var conflict = (await _catalog.GetConnectorsByKnowledgeBaseAsync(knowledgeBaseId, ct))
            .FirstOrDefault(c => c.Id != connectorId && NormalizePathKey(c.TargetPath) == key);
        if (conflict != null)
            throw new InvalidOperationException($"知识库内已存在指向该路径的连接器: {conflict.Id}");

        var rebound = connector with { TargetPath = newTargetPath, UpdatedAt = DateTime.UtcNow };
        await _catalog.SaveConnectorAsync(rebound, ct);
        _logger?.LogInformation(
            "[KnowledgeBaseService] 连接器 {Id} 已重绑: {Old} -> {New}（保留索引数据）",
            connectorId, connector.TargetPath, newTargetPath);
        return rebound;
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

        // 注册取消源并启动后台工作流（协调器为 Singleton，跨请求可取消）
        var cts = new CancellationTokenSource();
        _coordinator.Register(jobId, cts);

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
                _coordinator.Unregister(jobId);
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
        if (_coordinator.TryGetCts(jobId, out var cts) && cts != null)
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

    // ==================== 蓝绿影子重构索引（作业化） ====================
    public async Task<JobDefinition> TriggerModelReindexJobAsync(
        string knowledgeBaseId,
        string? targetSpecId = null,
        CancellationToken ct = default)
    {
        var kb = await _catalog.GetKnowledgeBaseAsync(knowledgeBaseId, ct);
        if (kb == null)
            throw new KeyNotFoundException($"知识库不存在: {knowledgeBaseId}");

        if (string.IsNullOrWhiteSpace(targetSpecId))
            throw new InvalidOperationException("必须显式指定目标向量模型规格 (targetEmbeddingSpecId)，禁止静默回退全局模型。");

        var targetSpec = await _catalog.GetEmbeddingSpecAsync(targetSpecId, ct);
        if (targetSpec == null)
        {
            var allSpecs = await ListEmbeddingSpecsAsync(ct);
            targetSpec = allSpecs.FirstOrDefault(s => string.Equals(s.Id, targetSpecId, StringComparison.OrdinalIgnoreCase));
        }
        if (targetSpec == null)
            throw new InvalidOperationException($"目标向量模型规格未找到: {targetSpecId}");
        if (string.Equals(targetSpec.Id, "spec_lexical_only", StringComparison.OrdinalIgnoreCase) || targetSpec.Dimension == 0)
            throw new InvalidOperationException("目标规格为纯词法（无向量），请使用解绑模型接口而非重构索引。");

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
                throw new InvalidOperationException(
                    $"目标模型 '{targetSpec.ModelName}' 最大上下文上限 ({targetLimit} tokens) 小于当前知识库分段规格 ({currentChunkSpec.MaxTokens} tokens)。切换后将引发严重截断或向量生成失败，请先调整知识库分段规格并重新分段。");
            }
        }

        // 单飞占位：同知识库已有进行中 reindex → 409 冲突
        var jobId = Guid.NewGuid().ToString("N")[..16];
        var cts = new CancellationTokenSource(); // 独立于 HTTP 请求生命周期
        if (!_coordinator.TryBeginReindex(knowledgeBaseId, jobId, cts))
        {
            cts.Dispose();
            var activeId = _coordinator.GetActiveReindexJobId(knowledgeBaseId);
            throw new KnowledgeBaseJobConflictException($"知识库已有进行中的重构索引作业 ({activeId})，请等待完成或先取消。");
        }

        var job = new JobDefinition(
            Id: jobId,
            Operation: JobOperation.Reindex,
            KnowledgeBaseId: knowledgeBaseId,
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Queued,
            Phase: OperationPhase.Planning,
            TotalUnits: 0,
            ProcessedUnits: 0,
            IdempotencyKey: $"{knowledgeBaseId}:reindex:{targetSpec.Id}",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow,
            FinishedAt: null
        );

        try
        {
            await _catalog.SaveJobAsync(job, ct);
            await _catalog.SaveEmbeddingSpecAsync(targetSpec, ct);
        }
        catch
        {
            _coordinator.EndReindex(knowledgeBaseId, jobId);
            cts.Dispose();
            throw;
        }

        _ = Task.Run(() => RunReindexWorkerAsync(job, targetSpec, cts));

        _logger?.LogInformation("[KnowledgeBaseService] 知识库 {KbId} 重构索引作业已入队: {JobId} → {SpecId}",
            knowledgeBaseId, jobId, targetSpec.Id);
        return job;
    }

    private async Task RunReindexWorkerAsync(JobDefinition job, EmbeddingSpec targetSpec, CancellationTokenSource cts)
    {
        var knowledgeBaseId = job.KnowledgeBaseId;
        var sm = new JobStateMachine(job, _catalog, _loggerFactory?.CreateLogger<JobStateMachine>());
        try
        {
            await sm.TransitionToAsync(OperationPhase.Planning, JobStatus.Running, ct: cts.Token);

            if (_adapterResolver == null)
                throw new InvalidOperationException("嵌入适配器解析器未注册，无法按目标规格创建真实编码器。");

            await using var adapter = await _adapterResolver.CreateForSpecAsync(targetSpec, cts.Token);
            await sm.TransitionToAsync(OperationPhase.Embedding, ct: cts.Token);

            Func<IReadOnlyList<string>, Task<IReadOnlyList<float[]>>> vectorProvider = async texts =>
                await adapter.EncodeBatchAsync(texts, targetSpec.DocumentPrefix, cts.Token);

            var progress = new Progress<ShadowReindexProgress>(p => _ = ReportReindexProgressAsync(sm, p));

            await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, cts.Token);
            var result = await lease.Store.ExecuteShadowModelReindexAsync(
                vectorProvider,
                targetSpec.Id,
                targetSpec.Dimension,
                progress,
                cts.Token);

            await sm.TransitionToAsync(OperationPhase.Publishing, ct: cts.Token);
            var freshKb = await _catalog.GetKnowledgeBaseAsync(knowledgeBaseId, cts.Token)
                ?? throw new InvalidOperationException($"知识库不存在: {knowledgeBaseId}");
            var updatedKb = freshKb with
            {
                EmbeddingSpecId = targetSpec.Id,
                DesiredConfigRevision = freshKb.DesiredConfigRevision + 1,
                EffectiveConfigRevision = freshKb.EffectiveConfigRevision + 1,
                UpdatedAt = DateTime.UtcNow
            };
            await _catalog.SaveKnowledgeBaseAsync(updatedKb, cts.Token);

            await sm.UpdateProgressAsync(result.TotalChunks, result.TotalChunks, cts.Token);
            await sm.TransitionToAsync(OperationPhase.None, JobStatus.Succeeded, ct: CancellationToken.None);

            _logger?.LogInformation(
                "[KnowledgeBaseService] 知识库 {KbId} 蓝绿影子重构索引完成: 共 {Total} 切片（续跑 {Resumed}），新代次 {Gen}，规格 {Spec}",
                knowledgeBaseId, result.TotalChunks, result.ResumedChunks, result.NewVectorGeneration, targetSpec.Id);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            await TryDropShadowAsync(knowledgeBaseId);
            await sm.TransitionToAsync(OperationPhase.None, JobStatus.Cancelled, "用户取消了重构索引作业", CancellationToken.None);
            _logger?.LogInformation("[KnowledgeBaseService] 知识库 {KbId} 重构索引作业 {JobId} 已取消", knowledgeBaseId, job.Id);
        }
        catch (Exception ex)
        {
            await TryDropShadowAsync(knowledgeBaseId);
            await sm.TransitionToAsync(OperationPhase.None, JobStatus.Failed, ex.Message, CancellationToken.None);
            _logger?.LogError(ex, "[KnowledgeBaseService] 知识库 {KbId} 重构索引作业 {JobId} 失败", knowledgeBaseId, job.Id);
        }
        finally
        {
            _coordinator.EndReindex(knowledgeBaseId, job.Id);
            cts.Dispose();
        }
    }

    private static async Task ReportReindexProgressAsync(JobStateMachine sm, ShadowReindexProgress p)
    {
        try
        {
            await sm.UpdateProgressAsync(p.ProcessedChunks, p.TotalChunks, CancellationToken.None);
        }
        catch
        {
            // 进度写入失败不影响重构主流程
        }
    }

    private async Task TryDropShadowAsync(string knowledgeBaseId)
    {
        try
        {
            await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, CancellationToken.None);
            await lease.Store.DropShadowVectorTableAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[KnowledgeBaseService] 清理知识库 {KbId} 影子表失败（下次触发将自动重建）", knowledgeBaseId);
        }
    }

    public async Task<KnowledgeBaseDefinition> RollbackModelReindexAsync(string knowledgeBaseId, CancellationToken ct = default)
    {
        var kb = await _catalog.GetKnowledgeBaseAsync(knowledgeBaseId, ct)
            ?? throw new KeyNotFoundException($"知识库不存在: {knowledgeBaseId}");

        if (_coordinator.GetActiveReindexJobId(knowledgeBaseId) != null)
            throw new KnowledgeBaseJobConflictException("重构索引作业进行中，禁止回滚向量代次。");

        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadWrite, ct);
        var previousSpecId = await lease.Store.RollbackVectorGenerationAsync(ct);
        if (previousSpecId == null)
            throw new InvalidOperationException("没有可回滚的上一代向量索引（chunk_vectors_old 不存在）。");

        var updatedKb = kb with
        {
            EmbeddingSpecId = previousSpecId,
            DesiredConfigRevision = kb.DesiredConfigRevision + 1,
            EffectiveConfigRevision = kb.EffectiveConfigRevision + 1,
            UpdatedAt = DateTime.UtcNow
        };
        await _catalog.SaveKnowledgeBaseAsync(updatedKb, ct);

        _logger?.LogInformation("[KnowledgeBaseService] 知识库 {KbId} 向量代次已回滚至规格 {Spec}", knowledgeBaseId, previousSpecId);
        return updatedKb;
    }

    public async Task<ReindexJobStatus> GetReindexStatusAsync(string knowledgeBaseId, CancellationToken ct = default)
    {
        var kb = await _catalog.GetKnowledgeBaseAsync(knowledgeBaseId, ct)
            ?? throw new KeyNotFoundException($"知识库不存在: {knowledgeBaseId}");

        await using var lease = await _storageManager.AcquireLeaseAsync(knowledgeBaseId, StorageAccess.ReadOnly, ct);
        var generation = await lease.Store.GetVectorGenerationInfoAsync(ct);

        var jobs = await _catalog.ListJobsAsync(knowledgeBaseId, ct);
        var active = jobs
            .Where(j => j.Operation == JobOperation.Reindex &&
                        j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.Cancelling)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefault();

        if (active != null && _coordinator.GetActiveReindexJobId(knowledgeBaseId) == null)
        {
            // 进程重启后的孤儿作业：标记中断；影子表保留，重新触发可断点续跑
            active = active with
            {
                Status = JobStatus.Failed,
                ErrorMessage = "进程重启导致作业中断；影子表已保留，重新触发可断点续跑。",
                FinishedAt = DateTime.UtcNow
            };
            await _catalog.SaveJobAsync(active, ct);
            active = null;
        }

        return new ReindexJobStatus(active, generation);
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
