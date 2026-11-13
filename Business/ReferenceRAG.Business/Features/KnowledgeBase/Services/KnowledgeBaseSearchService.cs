using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Tracing;
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
        // 与 SearchService 同一套模板方法：耗时链在这里开启/收口，
        // 若上游（如 SearchService）已开启则复用其链，只补充自己的 Span。
        var ownsTimeline = !SearchTimeline.IsActive;
        var timeline = SearchTimeline.BeginRequest(out var timelineScope);

        var root = TimingScope.Begin("request.kb-search", SpanCategory.Request, "跨库检索请求");
        root.SetDetail(request.Query);

        CrossKnowledgeBaseSearchResponse response;
        try
        {
            response = await SearchCoreAsync(request, ct);
        }
        catch (Exception ex)
        {
            root.Fail(ex);
            root.Dispose();
            if (ownsTimeline) timelineScope.Dispose();
            throw;
        }

        root.Dispose();

        if (ownsTimeline)
        {
            var chain = timeline.Complete((long)response.Elapsed.TotalMilliseconds);
            response = response with { TimingChain = chain };
            timelineScope.Dispose();
        }

        return response;
    }

    private async Task<CrossKnowledgeBaseSearchResponse> SearchCoreAsync(
        CrossKnowledgeBaseSearchRequest request,
        CancellationToken ct)
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
        string? degradationReason = null;

        // 在线向量计算与断路器高可用降级保护
        if (!useLexical && (queryVector == null || queryVector.Length == 0) && _embeddingService != null)
        {
            if (_circuitBreaker.IsOpen)
            {
                _logger?.LogWarning("[KnowledgeBaseSearchService] 向量模型断路器开启，0ms 短路降级为词法检索。");
                useLexical = true;
                isDegraded = true;
                degradationReason = "向量模型断路器开启，已短路降级为词法检索";
            }
            else
            {
                try
                {
                    using var embedSpan = TimingScope.Begin("embed.infer", SpanCategory.ModelLoad, "查询向量推理");
                    queryVector = await _embeddingService.EncodeAsync(request.Query, EmbeddingMode.Query, ct);
                    embedSpan.SetOutput(queryVector.Length);
                    _circuitBreaker.RecordSuccess();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _circuitBreaker.RecordFailure();
                    _logger?.LogWarning(ex, "[KnowledgeBaseSearchService] 向量服务异常，熔断器记录失败并降级为词法检索。");
                    useLexical = true;
                    isDegraded = true;
                    degradationReason = $"向量服务异常，已降级为词法检索 ({ex.Message})";
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
                Degraded: isDegraded,
                DegradationReason: degradationReason
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
        var fanoutScope = TimingScope.Begin("kb.fanout", SpanCategory.Phase, $"跨库扇出检索（{targetKbIds.Count} 库）");
        fanoutScope.SetInput(targetKbIds.Count);
        var tasks = targetKbIds.Select(async kbId =>
        {
            var recallSpan = TimingScope.Begin("kb.recall", SpanCategory.Io, "知识库召回");
            recallSpan.AddAttribute("kbId", kbId);
            try
            {
                var kbDef = await _catalog.GetKnowledgeBaseAsync(kbId, effectiveCt);
                if (kbDef == null || !kbDef.Enabled)
                {
                    recallSpan.SetDetail("知识库不存在或未启用");
                    return (KbId: kbId, Success: false, Snapshot: (RecallSnapshot?)null);
                }

                var isKbLexical = kbDef.IsPureLexical || string.Equals(kbDef.EmbeddingSpecId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase);
                var kbQueryVector = isKbLexical ? null : queryVector;
                var kbUseLexical = useLexical || isKbLexical;
                recallSpan.AddAttribute("lexical", kbUseLexical.ToString());

                RecallSnapshot snapshot;
                // 租约 Span 只覆盖"获取"本身：若把整个 recall 包进来，租约耗时会吞掉真正的检索耗时，
                // 归因就会把「冷启动打开数据库」和「FTS 匹配慢」混为一谈。
                IStorageLease? lease = null;
                var leaseSpan = TimingScope.Begin("kb.lease", SpanCategory.LockWait, "知识库存储租约获取");
                var leaseSw = Stopwatch.StartNew();
                try
                {
                    lease = await _storageManager.AcquireLeaseAsync(kbId, StorageAccess.ReadOnly, effectiveCt);
                }
                finally
                {
                    leaseSw.Stop();
                    leaseSpan.SetWait(leaseSw.ElapsedMilliseconds);
                    leaseSpan.SetDetail($"租约获取 {leaseSw.ElapsedMilliseconds}ms");
                    leaseSpan.Dispose();
                }

                await using (lease)
                {
                    KnowledgeBaseFileAuthorization? authorization = null;
                    if (request.AuthorizedSourceNames != null)
                        authorization = new KnowledgeBaseFileAuthorization(_configManager!, request.AuthorizedSourceNames,
                            await _catalog.GetConnectorsByKnowledgeBaseAsync(kbId, effectiveCt));
                    snapshot = await lease.Store.RecallAsync(new RecallRequest(request.Query, request.TopK,
                        QueryVector: kbQueryVector, UseLexicalTokenizer: kbUseLexical, AllowDegrade: request.AllowDegrade, Folders: request.Folders)
                        { AuthorizeDocument = authorization == null ? null : authorization.Allows }, effectiveCt);
                }

                if (request.AuthorizedSourceNames != null)
                {
                    var latestAuthorization = new KnowledgeBaseFileAuthorization(_configManager!, request.AuthorizedSourceNames,
                        await _catalog.GetConnectorsByKnowledgeBaseAsync(kbId, effectiveCt));
                    snapshot = snapshot with { Items = snapshot.Items.Where(i => i.ConnectorId != null && latestAuthorization.Allows(i.ConnectorId, i.SourceKey, i.Locator)).ToArray() };
                }
                recallSpan.SetOutput(snapshot.Items.Count);
                if (snapshot.Degraded) recallSpan.SetDetail(snapshot.DegradationReason ?? "降级");
                return (KbId: kbId, Success: true, Snapshot: (RecallSnapshot?)snapshot);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                recallSpan.SetDetail("请求取消");
                throw;
            }
            catch (Exception ex)
            {
                recallSpan.Fail(ex);
                _logger?.LogWarning(ex, "[KnowledgeBaseSearchService] 知识库 {KbId} 召回异常", kbId);
                return (KbId: kbId, Success: false, Snapshot: (RecallSnapshot?)null);
            }
            finally
            {
                recallSpan.Dispose();
            }
        });

        (string KbId, bool Success, RecallSnapshot? Snapshot)[] results;
        try
        {
            results = await Task.WhenAll(tasks);
            fanoutScope.SetOutput(results.Count(r => r.Success));
        }
        finally
        {
            fanoutScope.Dispose();
        }

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

        // 汇总各库降级原因：
        //  - Degraded 保持"模型/服务级降级"语义（断路器、向量推理失败），不因索引状态变化而翻转，
        //    避免把「词法索引正在后台补齐」误判成模型故障；
        //  - 索引级降级（词法索引未就绪 / 向量覆盖不全）单独走 IndexDegraded，原因仍透出给前端。
        bool indexDegraded = snapshots.Any(s => s.Degraded);
        if (indexDegraded && string.IsNullOrWhiteSpace(degradationReason))
        {
            degradationReason = snapshots
                .FirstOrDefault(s => s.Degraded && !string.IsNullOrWhiteSpace(s.DegradationReason))?.DegradationReason;
        }

        var strategy = _fusionStrategy is RrfSearchFusionStrategy
            ? new RrfSearchFusionStrategy(k: 60, query: request.Query)
            : _fusionStrategy;
        IReadOnlyList<RecallItem> fusedItems;
        var fusionSpan = TimingScope.Begin("fusion.rrf", SpanCategory.Cpu, "RRF 多路融合");
        try
        {
            fusionSpan.SetInput(snapshots.Sum(s => s.Items.Count));
            fusedItems = strategy.Fuse(snapshots, request.TopK);
            fusionSpan.SetOutput(fusedItems.Count);
        }
        finally
        {
            fusionSpan.Dispose();
        }
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
            Degraded: isDegraded,
            DegradationReason: degradationReason,
            IndexDegraded: indexDegraded
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
