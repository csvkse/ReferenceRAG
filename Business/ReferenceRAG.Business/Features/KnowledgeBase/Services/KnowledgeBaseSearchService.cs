using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.Resilience;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 跨知识库受控扇出检索与多路融合服务实现
/// </summary>
public sealed class KnowledgeBaseSearchService : IKnowledgeBaseSearchService
{
    private readonly ICatalogStore _catalog;
    private readonly IKnowledgeBaseStorageManager _storageManager;
    private readonly ISearchFusionStrategy _fusionStrategy;
    private readonly ILogger<KnowledgeBaseSearchService>? _logger;
    private readonly ConfigManager? _configManager;
    private readonly IEmbeddingService? _embeddingService;
    private readonly ResilienceCircuitBreaker _circuitBreaker;

    public KnowledgeBaseSearchService(
        ICatalogStore catalog,
        IKnowledgeBaseStorageManager storageManager,
        ISearchFusionStrategy? fusionStrategy = null,
        ILogger<KnowledgeBaseSearchService>? logger = null,
        ConfigManager? configManager = null,
        IEmbeddingService? embeddingService = null,
        ResilienceCircuitBreaker? circuitBreaker = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _fusionStrategy = fusionStrategy ?? new RrfSearchFusionStrategy(k: 60);
        _logger = logger;
        _configManager = configManager;
        _embeddingService = embeddingService;
        _circuitBreaker = circuitBreaker ?? new ResilienceCircuitBreaker(failureThreshold: 3, breakDuration: TimeSpan.FromSeconds(30));
    }

    public async Task<CrossKnowledgeBaseSearchResponse> SearchAsync(
        CrossKnowledgeBaseSearchRequest request,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var embeddingEnabled = _configManager?.Load().Indexing.EmbeddingEnabled ?? true;
        if (request.AuthorizedSourceNames != null && _configManager == null)
            throw new InvalidOperationException("受限知识库检索缺少数据源配置。 ");
        if (!embeddingEnabled && request.QueryVector is { Length: > 0 })
            throw new InvalidOperationException("embedding 已禁用，无法执行向量检索。");
        var useLexical = !embeddingEnabled || request.UseLexicalTokenizer;
        var queryVector = request.QueryVector;
        bool isDegraded = false;

        // 在线向量计算与断路器高可用降级保护
        if (!useLexical && (queryVector == null || queryVector.Length == 0) && _embeddingService != null)
        {
            if (_circuitBreaker.IsOpen)
            {
                _logger?.LogWarning("[KnowledgeBaseSearchService] 向量模型断路器开启，0ms 短路降级为词法检索。");
                useLexical = true;
                isDegraded = true;
            }
            else
            {
                try
                {
                    queryVector = await _embeddingService.EncodeAsync(request.Query, EmbeddingMode.Query, ct);
                    _circuitBreaker.RecordSuccess();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _circuitBreaker.RecordFailure();
                    _logger?.LogWarning(ex, "[KnowledgeBaseSearchService] 向量服务异常，熔断器记录失败并降级为词法检索。");
                    useLexical = true;
                    isDegraded = true;
                }
            }
        }

        var targetKbIds = request.KnowledgeBaseIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? new List<string>();
        if (targetKbIds.Count == 0)
        {
            var allKbs = await _catalog.ListKnowledgeBasesAsync(ct);
            targetKbIds = allKbs.Where(k => k.Enabled).Select(k => k.Id).ToList();
        }

        if (targetKbIds.Count == 0 || request.AuthorizedSourceNames is { Count: 0 })
        {
            return new CrossKnowledgeBaseSearchResponse(
                Query: request.Query,
                Items: Array.Empty<RecallItem>(),
                SuccessfulKnowledgeBaseIds: Array.Empty<string>(),
                FailedKnowledgeBaseIds: Array.Empty<string>(),
                PartialSuccess: false,
                ScoreKind: _fusionStrategy.ScoreKind,
                Elapsed: sw.Elapsed,
                Degraded: isDegraded
            );
        }

        // 规范化库数量上限（至多 8 库扇出）
        targetKbIds = targetKbIds.Take(8).ToList();

        var successfulKbs = new List<string>();
        var failedKbs = new List<string>();
        var snapshots = new List<RecallSnapshot>();

        using var cts = request.Timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (cts != null && request.Timeout.HasValue)
        {
            cts.CancelAfter(request.Timeout.Value);
        }
        var effectiveCt = cts?.Token ?? ct;

        // 并发检索各库（受控并行度）
        var tasks = targetKbIds.Select(async kbId =>
        {
            try
            {
                var kbDef = await _catalog.GetKnowledgeBaseAsync(kbId, effectiveCt);
                if (kbDef == null || !kbDef.Enabled)
                {
                    return (KbId: kbId, Success: false, Snapshot: (RecallSnapshot?)null);
                }

                var isKbLexical = kbDef.IsPureLexical || string.Equals(kbDef.EmbeddingSpecId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase);
                var kbQueryVector = isKbLexical ? null : queryVector;
                var kbUseLexical = useLexical || isKbLexical;

                await using var lease = await _storageManager.AcquireLeaseAsync(kbId, StorageAccess.ReadOnly, effectiveCt);
                KnowledgeBaseFileAuthorization? authorization = null;
                if (request.AuthorizedSourceNames != null)
                    authorization = new KnowledgeBaseFileAuthorization(_configManager!, request.AuthorizedSourceNames,
                        await _catalog.GetConnectorsByKnowledgeBaseAsync(kbId, effectiveCt));
                var snapshot = await lease.Store.RecallAsync(new RecallRequest(request.Query, request.TopK,
                    QueryVector: kbQueryVector, UseLexicalTokenizer: kbUseLexical, AllowDegrade: request.AllowDegrade, Folders: request.Folders)
                    { AuthorizeDocument = authorization == null ? null : authorization.Allows }, effectiveCt);
                if (request.AuthorizedSourceNames != null)
                {
                    var latestAuthorization = new KnowledgeBaseFileAuthorization(_configManager!, request.AuthorizedSourceNames,
                        await _catalog.GetConnectorsByKnowledgeBaseAsync(kbId, effectiveCt));
                    snapshot = snapshot with { Items = snapshot.Items.Where(i => i.ConnectorId != null && latestAuthorization.Allows(i.ConnectorId, i.SourceKey, i.Locator)).ToArray() };
                }
                return (KbId: kbId, Success: true, Snapshot: (RecallSnapshot?)snapshot);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[KnowledgeBaseSearchService] 知识库 {KbId} 召回异常", kbId);
                return (KbId: kbId, Success: false, Snapshot: (RecallSnapshot?)null);
            }
        });

        var results = await Task.WhenAll(tasks);

        foreach (var r in results)
        {
            if (r.Success && r.Snapshot != null)
            {
                successfulKbs.Add(r.KbId);
                snapshots.Add(r.Snapshot);
            }
            else
            {
                failedKbs.Add(r.KbId);
            }
        }

        // 故障策略裁决
        if (failedKbs.Count > 0 && (request.FailurePolicy == FailurePolicy.Strict || successfulKbs.Count == 0))
        {
            throw new InvalidOperationException(request.AuthorizedSourceNames == null
                ? $"知识库检索失败: [{string.Join(", ", failedKbs)}] 无法完成检索。"
                : "部分知识库无法完成检索。");
        }

        var strategy = _fusionStrategy is RrfSearchFusionStrategy
            ? new RrfSearchFusionStrategy(k: 60, query: request.Query)
            : _fusionStrategy;
        var fusedItems = strategy.Fuse(snapshots, request.TopK);
        sw.Stop();

        return new CrossKnowledgeBaseSearchResponse(
            Query: request.Query,
            Items: fusedItems,
            SuccessfulKnowledgeBaseIds: request.AuthorizedSourceNames == null ? successfulKbs :
                results.Where(r => r.Success && r.Snapshot is { Items.Count: > 0 }).Select(r => r.KbId).ToList(),
            FailedKnowledgeBaseIds: request.AuthorizedSourceNames == null ? failedKbs : Array.Empty<string>(),
            PartialSuccess: (failedKbs.Count > 0 && successfulKbs.Count > 0) || isDegraded,
            ScoreKind: _fusionStrategy.ScoreKind,
            Elapsed: sw.Elapsed,
            EarlyPruned: snapshots.Any(s => s.EarlyPruned),
            Degraded: isDegraded
        );
    }

    public async Task<IReadOnlyList<KnowledgeBaseDefinition>> ListAccessibleKnowledgeBasesAsync(
        IReadOnlyList<string> authorizedSourceNames, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorizedSourceNames);
        if (authorizedSourceNames.Count == 0) return Array.Empty<KnowledgeBaseDefinition>();
        if (_configManager == null) throw new InvalidOperationException("受限知识库检索缺少数据源配置。");
        var visible = new List<KnowledgeBaseDefinition>();
        foreach (var kb in await _catalog.ListKnowledgeBasesAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!kb.Enabled) continue;
            var authorization = new KnowledgeBaseFileAuthorization(_configManager, authorizedSourceNames,
                await _catalog.GetConnectorsByKnowledgeBaseAsync(kb.Id, ct));
            await using var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadOnly, ct);
            if (!await lease.Store.HasAuthorizedDocumentsAsync(authorization.Allows, ct)) continue;
            var latest = new KnowledgeBaseFileAuthorization(_configManager, authorizedSourceNames,
                await _catalog.GetConnectorsByKnowledgeBaseAsync(kb.Id, ct));
            if (await lease.Store.HasAuthorizedDocumentsAsync(latest.Allows, ct))
                visible.Add(kb with { Description = null, EmbeddingSpecId = string.Empty });
        }
        return visible;
    }
}
