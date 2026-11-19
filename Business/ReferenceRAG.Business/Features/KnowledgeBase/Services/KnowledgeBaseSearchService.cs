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
/// 跨知识库受控扇出检索与单轨统一服务实现
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

    /// <summary>
    /// 统一知识库单轨检索入口
    /// </summary>
    public async Task<KnowledgeBaseSearchResponse> SearchAsync(
        KnowledgeBaseSearchRequest request,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traceId = Guid.NewGuid().ToString("n");
        var warnings = new List<string>();

        var ownsTimeline = !SearchTimeline.IsActive;
        var timeline = SearchTimeline.BeginRequest(out var timelineScope);

        var root = TimingScope.Begin("request.kb-search", SpanCategory.Request, "知识库单轨检索请求");
        root.SetDetail(request.Query);

        KnowledgeBaseSearchResponse response;
        try
        {
            response = await SearchUnifiedCoreAsync(request, traceId, sw, warnings, ct);
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
            var chain = timeline.Complete((long)response.ElapsedMs);
            response = response with { TimingChain = chain };
            timelineScope.Dispose();
        }

        return response;
    }

    /// <summary>
    /// 兼容旧版跨知识库检索入口（内部直接复用单轨检索能力）
    /// </summary>
    public async Task<CrossKnowledgeBaseSearchResponse> SearchAsync(
        CrossKnowledgeBaseSearchRequest request,
        CancellationToken ct = default)
    {
        var unifiedRequest = (KnowledgeBaseSearchRequest)request;
        var unifiedResponse = await SearchAsync(unifiedRequest, ct);
        return (CrossKnowledgeBaseSearchResponse)unifiedResponse;
    }

    private async Task<KnowledgeBaseSearchResponse> SearchUnifiedCoreAsync(
        KnowledgeBaseSearchRequest request,
        string traceId,
        Stopwatch sw,
        List<string> warnings,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new KnowledgeBaseSearchResponse(
                Outcome: "no_match",
                Completeness: "complete",
                RequestedMode: request.Mode ?? "auto",
                Hits: Array.Empty<KnowledgeEvidenceHit>(),
                Executions: Array.Empty<KnowledgeBaseExecutionRecord>(),
                TraceId: traceId,
                ElapsedMs: sw.ElapsedMilliseconds,
                Warnings: warnings
            ) { Query = request.Query ?? "" };
        }

        var mode = (request.Mode ?? "auto").Trim().ToLowerInvariant();
        if (mode is not ("auto" or "lexical" or "semantic" or "hybrid"))
        {
            throw new ArgumentException($"不支持的检索模式: '{request.Mode}'。支持模式: auto, lexical, semantic, hybrid。");
        }

        // 显式空数组返回 400
        if (request.KnowledgeBaseIds != null && request.KnowledgeBaseIds.Count == 0)
        {
            throw new ArgumentException("knowledgeBaseIds 不能为空数组。如需检索全部可用知识库，请省略该参数或传 null。");
        }

        // 规范化库数量上限（至多 8 库扇出）
        if (request.KnowledgeBaseIds != null && request.KnowledgeBaseIds.Count > 8)
        {
            throw new ArgumentException("超出服务端知识库数量上限（单次最多支持 8 个知识库）。");
        }

        // 约束 connectorId / pathPrefix 规则
        if (!string.IsNullOrWhiteSpace(request.ConnectorId) || !string.IsNullOrWhiteSpace(request.PathPrefix))
        {
            if (request.KnowledgeBaseIds == null || request.KnowledgeBaseIds.Count != 1)
            {
                throw new ArgumentException("指定 connectorId 或 pathPrefix 时，必须且只能指定一个确切的 knowledgeBaseId。");
            }
            if (!string.IsNullOrWhiteSpace(request.PathPrefix) && request.PathPrefix.Contains(".."))
            {
                throw new ArgumentException("pathPrefix 必须是规范化的相对路径，不能包含 '..'。");
            }
        }

        var caller = request.Caller;
        if (caller == null && request.AuthorizedSourceNames != null)
        {
            caller = new SearchCaller("legacy", IsAdmin: false, AuthorizedSourceNames: request.AuthorizedSourceNames);
        }

        List<string> targetKbIds;
        if (request.KnowledgeBaseIds != null)
        {
            // 显式请求采用全部目标可用才执行的约定，无权访问则直接抛出 UnauthorizedAccessException
            if (caller != null && !caller.IsAdmin && caller.AuthorizedKnowledgeBaseIds != null)
            {
                var allowedSet = new HashSet<string>(caller.AuthorizedKnowledgeBaseIds, StringComparer.OrdinalIgnoreCase);
                foreach (var id in request.KnowledgeBaseIds)
                {
                    if (!allowedSet.Contains(id))
                    {
                        throw new UnauthorizedAccessException($"无权访问知识库 '{id}' 或该知识库不可见。");
                    }
                }
            }
            targetKbIds = request.KnowledgeBaseIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            var allKbs = await _catalog.ListKnowledgeBasesAsync(ct);
            var enabledKbs = allKbs.Where(k => k.Enabled).ToList();
            if (caller != null && !caller.IsAdmin && caller.AuthorizedKnowledgeBaseIds != null)
            {
                var allowedSet = new HashSet<string>(caller.AuthorizedKnowledgeBaseIds, StringComparer.OrdinalIgnoreCase);
                targetKbIds = enabledKbs.Where(k => allowedSet.Contains(k.Id)).Select(k => k.Id).ToList();
            }
            else if (caller?.AuthorizedSourceNames != null)
            {
                var accessible = await ListAccessibleKnowledgeBasesAsync(caller.AuthorizedSourceNames, ct);
                targetKbIds = accessible.Select(k => k.Id).ToList();
            }
            else
            {
                targetKbIds = enabledKbs.Select(k => k.Id).ToList();
            }
        }

        if (targetKbIds.Count == 0 || (caller != null && !caller.IsAdmin && caller.AuthorizedSourceNames is { Count: 0 }) || request.AuthorizedSourceNames is { Count: 0 })
        {
            return new KnowledgeBaseSearchResponse(
                Outcome: "insufficient_evidence",
                Completeness: "complete",
                RequestedMode: mode,
                Hits: Array.Empty<KnowledgeEvidenceHit>(),
                Executions: Array.Empty<KnowledgeBaseExecutionRecord>(),
                TraceId: traceId,
                ElapsedMs: sw.ElapsedMilliseconds,
                Warnings: new[] { "当前无可用或有权的知识库 (scope_empty)" }
            ) { Query = request.Query };
        }

        var kbDefs = new Dictionary<string, KnowledgeBaseDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var kbId in targetKbIds)
        {
            var kbDef = await _catalog.GetKnowledgeBaseAsync(kbId, ct);
            if (kbDef == null || !kbDef.Enabled)
            {
                if (request.KnowledgeBaseIds != null)
                {
                    throw new UnauthorizedAccessException($"知识库 '{kbId}' 不存在或不可用。");
                }
                continue;
            }
            kbDefs[kbId] = kbDef;
        }

        if (kbDefs.Count == 0)
        {
            return new KnowledgeBaseSearchResponse(
                Outcome: "insufficient_evidence",
                Completeness: "complete",
                RequestedMode: mode,
                Hits: Array.Empty<KnowledgeEvidenceHit>(),
                Executions: Array.Empty<KnowledgeBaseExecutionRecord>(),
                TraceId: traceId,
                ElapsedMs: sw.ElapsedMilliseconds,
                Warnings: new[] { "当前无可用或有权的知识库 (scope_empty)" }
            ) { Query = request.Query };
        }

        var embeddingEnabled = _configManager?.Load().Indexing.EmbeddingEnabled ?? true;
        float[]? queryVector = request.QueryVector;
        bool isDegraded = false;
        string? degradationReason = null;

        // 纯词法检索严格跳过 Embedding 推理（零模型调用）
        bool needEmbedding;
        if (mode == "lexical" || request.UseLexicalTokenizer)
        {
            needEmbedding = false;
        }
        else if (mode == "auto")
        {
            var allPureLexical = kbDefs.Values.All(k => k.IsPureLexical || string.Equals(k.EmbeddingSpecId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase));
            needEmbedding = !allPureLexical;
        }
        else
        {
            needEmbedding = true;
        }

        if (needEmbedding && (queryVector == null || queryVector.Length == 0))
        {
            if (!embeddingEnabled || _embeddingService == null)
            {
                if (mode == "semantic" && !request.AllowDegrade)
                {
                    throw new InvalidOperationException("Embedding 服务未启用，无法执行语义检索。");
                }
                isDegraded = true;
                degradationReason = "Embedding 服务未启用，已降级为词法检索";
            }
            else if (_circuitBreaker.IsOpen)
            {
                if (!request.AllowDegrade)
                {
                    throw new InvalidOperationException("向量模型服务暂时不可用（断路器开启）。");
                }
                isDegraded = true;
                degradationReason = "向量模型断路器开启，已降级为词法检索";
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
                    if (!request.AllowDegrade)
                    {
                        throw;
                    }
                    isDegraded = true;
                    degradationReason = $"向量模型异常，已降级为词法检索 ({ex.Message})";
                }
            }
        }

        var snapshots = new List<RecallSnapshot>();
        var executions = new List<KnowledgeBaseExecutionRecord>();
        var effectiveKbIds = kbDefs.Keys.ToList();

        var fanoutScope = TimingScope.Begin("kb.fanout", SpanCategory.Phase, $"跨库扇出检索（{effectiveKbIds.Count} 库）");
        fanoutScope.SetInput(effectiveKbIds.Count);

        var tasks = effectiveKbIds.Select(async kbId =>
        {
            var recallSpan = TimingScope.Begin("kb.recall", SpanCategory.Io, "知识库召回");
            recallSpan.AddAttribute("kbId", kbId);
            try
            {
                var kbDef = kbDefs[kbId];
                var isKbPureLexical = kbDef.IsPureLexical || string.Equals(kbDef.EmbeddingSpecId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase);

                var plannedChannels = new List<string>();
                if (isKbPureLexical)
                {
                    plannedChannels.Add("lexical");
                }
                else if (mode == "lexical" || request.UseLexicalTokenizer || (isDegraded && request.AllowDegrade))
                {
                    plannedChannels.Add("lexical");
                }
                else if (mode == "semantic")
                {
                    plannedChannels.Add("vector");
                }
                else // hybrid or auto
                {
                    plannedChannels.Add("lexical");
                    if (queryVector != null && queryVector.Length > 0)
                    {
                        plannedChannels.Add("vector");
                    }
                }

                if (isKbPureLexical && mode == "semantic" && !request.AllowDegrade)
                {
                    recallSpan.SetDetail("纯词法库不支持语义检索");
                    return (KbId: kbId, Success: false, Snapshot: (RecallSnapshot?)null, PlannedChannels: (IReadOnlyList<string>)plannedChannels, CompletedChannels: (IReadOnlyList<string>)Array.Empty<string>(), ReasonCode: "mode_unavailable");
                }

                IStorageLease? lease = null;
                var leaseSpan = TimingScope.Begin("kb.lease", SpanCategory.LockWait, "知识库存储租约获取");
                var leaseSw = Stopwatch.StartNew();
                try
                {
                    lease = await _storageManager.AcquireLeaseAsync(kbId, StorageAccess.ReadOnly, ct);
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
                    KnowledgeBaseFileAuthorization? auth = null;
                    if (caller?.AuthorizedSourceNames != null && _configManager != null)
                    {
                        var connectors = await _catalog.GetConnectorsByKnowledgeBaseAsync(kbId, ct);
                        auth = new KnowledgeBaseFileAuthorization(_configManager, caller.AuthorizedSourceNames, connectors);
                    }

                    var effectiveFolders = request.Folders?.ToList() ?? new List<string>();
                    if (!string.IsNullOrWhiteSpace(request.PathPrefix))
                    {
                        effectiveFolders.Add(request.PathPrefix);
                    }
                    var metaFilters = new Dictionary<string, string>();
                    if (!string.IsNullOrWhiteSpace(request.ConnectorId))
                    {
                        metaFilters["connectorId"] = request.ConnectorId;
                    }

                    var topKCandidate = Math.Min(80, Math.Max(20, 3 * request.TopK));
                    var kbUseLexical = plannedChannels.Contains("lexical");
                    var kbQueryVector = plannedChannels.Contains("vector") ? queryVector : null;

                    var snapshot = await lease.Store.RecallAsync(new RecallRequest(
                        Query: request.Query,
                        TopK: topKCandidate,
                        QueryVector: kbQueryVector,
                        UseLexicalTokenizer: kbUseLexical,
                        AllowDegrade: request.AllowDegrade,
                        Folders: effectiveFolders.Count > 0 ? effectiveFolders : null,
                        MetadataFilters: metaFilters.Count > 0 ? metaFilters : null
                    ) { AuthorizeDocument = auth == null ? null : auth.Allows }, ct);

                    if (auth != null)
                    {
                        snapshot = snapshot with
                        {
                            Items = snapshot.Items.Where(i => i.ConnectorId != null && auth.Allows(i.ConnectorId, i.SourceKey, i.Locator)).ToArray()
                        };
                    }

                    var completedChannels = new List<string>();
                    if (snapshot.Items.Any(i => i.Channels.HasFlag(RecallChannel.Lexical))) completedChannels.Add("lexical");
                    if (snapshot.Items.Any(i => i.Channels.HasFlag(RecallChannel.Vector))) completedChannels.Add("vector");
                    if (completedChannels.Count == 0)
                    {
                        completedChannels.AddRange(plannedChannels);
                    }

                    recallSpan.SetOutput(snapshot.Items.Count);
                    if (snapshot.Degraded) recallSpan.SetDetail(snapshot.DegradationReason ?? "降级");
                    return (KbId: kbId, Success: true, Snapshot: (RecallSnapshot?)snapshot, PlannedChannels: (IReadOnlyList<string>)plannedChannels, CompletedChannels: (IReadOnlyList<string>)completedChannels, ReasonCode: (string?)null);
                }
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
                return (KbId: kbId, Success: false, Snapshot: (RecallSnapshot?)null, PlannedChannels: (IReadOnlyList<string>)new[] { "lexical" }, CompletedChannels: (IReadOnlyList<string>)Array.Empty<string>(), ReasonCode: ex.Message);
            }
            finally
            {
                recallSpan.Dispose();
            }
        });

        (string KbId, bool Success, RecallSnapshot? Snapshot, IReadOnlyList<string> PlannedChannels, IReadOnlyList<string> CompletedChannels, string? ReasonCode)[] results;
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
                snapshots.Add(r.Snapshot);
                var status = r.CompletedChannels.Count >= r.PlannedChannels.Count ? "success" : "partial";
                executions.Add(new KnowledgeBaseExecutionRecord(r.KbId, r.PlannedChannels, r.CompletedChannels, status, r.ReasonCode));
            }
            else
            {
                executions.Add(new KnowledgeBaseExecutionRecord(r.KbId, r.PlannedChannels, r.CompletedChannels, "failed", r.ReasonCode));
            }
        }

        if (executions.All(e => e.Status == "failed") && !request.AllowDegrade)
        {
            throw new InvalidOperationException("知识库检索执行失败，所有知识库均无法完成检索。");
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

        var hits = fusedItems.Select(item =>
        {
            var kbId = item.KnowledgeBaseId ?? effectiveKbIds.FirstOrDefault() ?? "";
            var digest = CitationRef.ComputeDigest(item.ChunkId, item.Text, item.StartLine, item.EndLine);
            var citation = new CitationRef(
                KnowledgeBaseId: kbId,
                DocumentId: item.DocumentId,
                ChunkId: item.ChunkId,
                DocumentVersion: item.DocumentVersion,
                Digest: digest
            );
            var channels = new List<string>();
            if (item.Channels.HasFlag(RecallChannel.Lexical)) channels.Add("lexical");
            if (item.Channels.HasFlag(RecallChannel.Vector)) channels.Add("vector");
            if (channels.Count == 0) channels.Add("lexical");

            var title = !string.IsNullOrWhiteSpace(item.SourceKey)
                ? Path.GetFileName(item.SourceKey)
                : item.ChunkId;

            return new KnowledgeEvidenceHit(
                CitationRef: citation,
                Title: title,
                SourceKey: item.SourceKey,
                StartLine: item.StartLine,
                EndLine: item.EndLine,
                Excerpt: item.Text,
                Score: item.Score,
                Channels: channels,
                KnowledgeBaseId: kbId,
                ScoreKind: item.ScoreKind,
                IsFloor: item.IsFloor,
                Coverage: item.Coverage,
                HeadingPath: item.HeadingPath
            );
        }).ToList();

        var (noMatch, noMatchReason) = QueryMatchSignals.Evaluate(request.Query, fusedItems, snapshots.Any(s => s.UsedRecallFloor));

        string outcome;
        if (hits.Count > 0 && !noMatch)
        {
            outcome = "matched";
        }
        else if (executions.All(e => e.Status == "success") && (hits.Count == 0 || noMatch))
        {
            outcome = "no_match";
        }
        else
        {
            outcome = "insufficient_evidence";
        }

        var completeness = executions.All(e => e.Status == "success") && !isDegraded ? "complete" : "partial";
        bool indexDegraded = snapshots.Any(s => s.Degraded);
        if (indexDegraded && string.IsNullOrWhiteSpace(degradationReason))
        {
            degradationReason = snapshots.FirstOrDefault(s => s.Degraded && !string.IsNullOrWhiteSpace(s.DegradationReason))?.DegradationReason;
        }

        sw.Stop();
        var totalCandidates = snapshots.Sum(s => s.Items.Count);

        return new KnowledgeBaseSearchResponse(
            Outcome: outcome,
            Completeness: completeness,
            RequestedMode: mode,
            Hits: hits,
            Executions: executions,
            TraceId: traceId,
            ElapsedMs: sw.ElapsedMilliseconds,
            Warnings: warnings.Count > 0 ? warnings : null,
            Truncated: totalCandidates > hits.Count
        )
        {
            Query = request.Query,
            EarlyPruned = snapshots.Any(s => s.EarlyPruned),
            Degraded = isDegraded,
            DegradationReason = degradationReason,
            IndexDegraded = indexDegraded
        };
    }

    /// <summary>
    /// 结构化版本化证据批量回读
    /// </summary>
    public async Task<EvidenceReadResponse> ReadEvidenceAsync(
        EvidenceReadRequest request,
        SearchCaller? caller = null,
        CancellationToken ct = default)
    {
        if (request.Citations == null || request.Citations.Count == 0)
        {
            return new EvidenceReadResponse(Array.Empty<VerifiedEvidenceItem>());
        }

        var allowedKbSet = caller?.AuthorizedKnowledgeBaseIds != null && !caller.IsAdmin
            ? new HashSet<string>(caller.AuthorizedKnowledgeBaseIds, StringComparer.OrdinalIgnoreCase)
            : null;

        var grouped = request.Citations
            .Select((cit, index) => (cit, index))
            .GroupBy(x => x.cit.KnowledgeBaseId);

        var results = new VerifiedEvidenceItem[request.Citations.Count];

        foreach (var group in grouped)
        {
            var kbId = group.Key;
            if (allowedKbSet != null && !allowedKbSet.Contains(kbId))
            {
                foreach (var (cit, idx) in group)
                {
                    results[idx] = new VerifiedEvidenceItem(cit, Status: "forbidden");
                }
                continue;
            }

            var kbDef = await _catalog.GetKnowledgeBaseAsync(kbId, ct);
            if (kbDef == null || !kbDef.Enabled)
            {
                foreach (var (cit, idx) in group)
                {
                    results[idx] = new VerifiedEvidenceItem(cit, Status: "not_found");
                }
                continue;
            }

            IStorageLease? lease = null;
            try
            {
                lease = await _storageManager.AcquireLeaseAsync(kbId, StorageAccess.ReadOnly, ct);
            }
            catch
            {
                foreach (var (cit, idx) in group)
                {
                    results[idx] = new VerifiedEvidenceItem(cit, Status: "not_found");
                }
                continue;
            }

            await using (lease)
            {
                KnowledgeBaseFileAuthorization? auth = null;
                if (caller?.AuthorizedSourceNames != null && _configManager != null)
                {
                    var connectors = await _catalog.GetConnectorsByKnowledgeBaseAsync(kbId, ct);
                    auth = new KnowledgeBaseFileAuthorization(_configManager, caller.AuthorizedSourceNames, connectors);
                }

                foreach (var (cit, idx) in group)
                {
                    var doc = await lease.Store.GetDocumentByIdAsync(cit.DocumentId, ct);
                    if (doc == null)
                    {
                        results[idx] = new VerifiedEvidenceItem(cit, Status: "not_found");
                        continue;
                    }

                    if (doc.DocumentVersion != cit.DocumentVersion)
                    {
                        results[idx] = new VerifiedEvidenceItem(cit, Status: "stale_reference");
                        continue;
                    }

                    if (auth != null && !auth.Allows(doc.ConnectorId, doc.SourceKey, doc.Locator))
                    {
                        results[idx] = new VerifiedEvidenceItem(cit, Status: "forbidden");
                        continue;
                    }

                    var chunk = await lease.Store.GetChunkByIdAsync(cit.ChunkId, ct);
                    if (chunk == null || chunk.DocumentId != cit.DocumentId)
                    {
                        results[idx] = new VerifiedEvidenceItem(cit, Status: "not_found");
                        continue;
                    }

                    var expectedDigest = CitationRef.ComputeDigest(chunk.Id, chunk.Text, chunk.StartLine, chunk.EndLine);
                    if (!string.Equals(expectedDigest, cit.Digest, StringComparison.OrdinalIgnoreCase))
                    {
                        results[idx] = new VerifiedEvidenceItem(cit, Status: "stale_reference");
                        continue;
                    }

                    results[idx] = new VerifiedEvidenceItem(
                        Citation: cit,
                        Status: "ok",
                        Text: chunk.Text,
                        StartLine: chunk.StartLine,
                        EndLine: chunk.EndLine,
                        HeadingPath: chunk.HeadingPath,
                        SourceKey: doc.SourceKey ?? chunk.DocumentSourceKey
                    );
                }
            }
        }

        return new EvidenceReadResponse(results);
    }

    /// <summary>
    /// 获取当前可信身份下的知识库检索状态摘要
    /// </summary>
    public async Task<KnowledgeBaseSearchStatusResponse> GetStatusAsync(
        SearchCaller? caller = null,
        CancellationToken ct = default)
    {
        var allKbs = await _catalog.ListKnowledgeBasesAsync(ct);
        var allowedKbSet = caller?.AuthorizedKnowledgeBaseIds != null && !caller.IsAdmin
            ? new HashSet<string>(caller.AuthorizedKnowledgeBaseIds, StringComparer.OrdinalIgnoreCase)
            : null;

        var accessibleKbs = new List<KnowledgeBaseDefinition>();
        if (allowedKbSet != null)
        {
            accessibleKbs = allKbs.Where(k => allowedKbSet.Contains(k.Id)).ToList();
        }
        else if (caller?.AuthorizedSourceNames != null)
        {
            accessibleKbs = (await ListAccessibleKnowledgeBasesAsync(caller.AuthorizedSourceNames, ct)).ToList();
        }
        else
        {
            accessibleKbs = allKbs.ToList();
        }

        var summaries = new List<KnowledgeBaseStatusSummary>();
        foreach (var kb in accessibleKbs)
        {
            try
            {
                await using var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadOnly, ct);
                var lexicalStatus = await lease.Store.GetLexicalIndexStatusAsync(ct);
                var stats = await lease.Store.GetLexicalStatsAsync(ct);
                summaries.Add(new KnowledgeBaseStatusSummary(
                    Id: kb.Id,
                    Name: kb.Name,
                    Enabled: kb.Enabled,
                    IsPureLexical: kb.IsPureLexical || string.Equals(kb.EmbeddingSpecId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase),
                    EmbeddingSpecId: kb.EmbeddingSpecId,
                    DocumentCount: stats.TotalDocuments,
                    ChunkCount: stats.TotalChunks,
                    LexicalReady: lexicalStatus.Status is LexicalIndexStatus.Ready or LexicalIndexStatus.Empty
                ));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[KnowledgeBaseSearchService] 获取知识库 {KbId} 状态失败", kb.Id);
                summaries.Add(new KnowledgeBaseStatusSummary(
                    Id: kb.Id,
                    Name: kb.Name,
                    Enabled: kb.Enabled,
                    IsPureLexical: kb.IsPureLexical || string.Equals(kb.EmbeddingSpecId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase),
                    EmbeddingSpecId: kb.EmbeddingSpecId,
                    DocumentCount: 0,
                    ChunkCount: 0,
                    LexicalReady: false
                ));
            }
        }

        IReadOnlyList<JobDefinition> recentJobs = Array.Empty<JobDefinition>();
        if (caller == null || caller.IsAdmin)
        {
            try
            {
                var jobs = await _catalog.ListJobsAsync(ct: ct);
                recentJobs = jobs.Take(20).ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[KnowledgeBaseSearchService] 获取最近作业失败");
            }
        }

        return new KnowledgeBaseSearchStatusResponse(summaries, recentJobs, DateTime.UtcNow);
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
