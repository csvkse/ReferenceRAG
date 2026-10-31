using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Fusion;

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

    public KnowledgeBaseSearchService(
        ICatalogStore catalog,
        IKnowledgeBaseStorageManager storageManager,
        ISearchFusionStrategy? fusionStrategy = null,
        ILogger<KnowledgeBaseSearchService>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _fusionStrategy = fusionStrategy ?? new RrfSearchFusionStrategy(k: 60);
        _logger = logger;
    }

    public async Task<CrossKnowledgeBaseSearchResponse> SearchAsync(
        CrossKnowledgeBaseSearchRequest request,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (request.KnowledgeBaseIds == null || request.KnowledgeBaseIds.Count == 0)
        {
            return new CrossKnowledgeBaseSearchResponse(
                Query: request.Query,
                Items: Array.Empty<RecallItem>(),
                SuccessfulKnowledgeBaseIds: Array.Empty<string>(),
                FailedKnowledgeBaseIds: Array.Empty<string>(),
                PartialSuccess: false,
                ScoreKind: _fusionStrategy.ScoreKind,
                Elapsed: sw.Elapsed
            );
        }

        // 规范化库数量上限（至多 8 库扇出）
        var targetKbIds = request.KnowledgeBaseIds.Distinct().Take(8).ToList();

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

                await using var lease = await _storageManager.AcquireLeaseAsync(kbId, StorageAccess.ReadOnly, effectiveCt);
                var snapshot = await lease.Store.RecallAsync(new RecallRequest(request.Query, request.TopK, QueryVector: request.QueryVector), effectiveCt);
                return (KbId: kbId, Success: true, Snapshot: (RecallSnapshot?)snapshot);
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
        if (request.FailurePolicy == FailurePolicy.Strict && failedKbs.Count > 0)
        {
            throw new InvalidOperationException($"严格模式下检索失败: 知识库 [{string.Join(", ", failedKbs)}] 执行异常");
        }

        var fusedItems = _fusionStrategy.Fuse(snapshots, request.TopK);
        sw.Stop();

        return new CrossKnowledgeBaseSearchResponse(
            Query: request.Query,
            Items: fusedItems,
            SuccessfulKnowledgeBaseIds: successfulKbs,
            FailedKnowledgeBaseIds: failedKbs,
            PartialSuccess: failedKbs.Count > 0 && successfulKbs.Count > 0,
            ScoreKind: _fusionStrategy.ScoreKind,
            Elapsed: sw.Elapsed
        );
    }
}
