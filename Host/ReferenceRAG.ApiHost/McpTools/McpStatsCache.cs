using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;

namespace ReferenceRAG.Service.McpTools;

/// <summary>
/// 连接器物理统计的短 TTL 缓存（Decorator 模式）。
///
/// 背景（生产实测）：<c>list_sources</c> 逐连接器执行 2 个子查询，首调 2.4 秒；
/// <c>get_index_status</c> 每库 <c>COUNT(*) + AVG(LENGTH(text))</c> 扫 237k 行，稳定 1.2–1.5 秒。
/// 这些都是低频变化的读模型，用 30 秒 TTL + 单飞（同一时刻只允许一次加载）即可把抖动抹平，
/// 且与既有 <c>SourceLinkProjectionCache</c> 的失效语义一致（写路径触发失效标签）。
/// </summary>
public interface IMcpStatsCache
{
    Task<IReadOnlyList<ConnectorStatsSnapshot>> GetConnectorStatsAsync(
        IKnowledgeBaseService kbService, CancellationToken ct = default);

    Task<IReadOnlyList<KnowledgeBaseIndexStats>> GetIndexStatsAsync(
        IKnowledgeBaseService kbService, IKnowledgeBaseStorageManager? storage, CancellationToken ct = default);

    void Invalidate();
}

public sealed record KnowledgeBaseIndexStats(
    string KnowledgeBaseId,
    string Name,
    string EmbeddingSpecId,
    int ChunkCount,
    int DocumentCount,
    int VectorCount,
    int VocabularySize);

public sealed class McpStatsCache : IMcpStatsCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _connectorGate = new(1, 1);
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    private IReadOnlyList<ConnectorStatsSnapshot>? _connectors;
    private DateTime _connectorsAt;
    private IReadOnlyList<KnowledgeBaseIndexStats>? _index;
    private DateTime _indexAt;

    public void Invalidate()
    {
        _connectors = null;
        _index = null;
    }

    public async Task<IReadOnlyList<ConnectorStatsSnapshot>> GetConnectorStatsAsync(
        IKnowledgeBaseService kbService, CancellationToken ct = default)
    {
        var hit = Fresh(_connectors, _connectorsAt);
        if (hit != null) return hit;

        await _connectorGate.WaitAsync(ct);
        try
        {
            hit = Fresh(_connectors, _connectorsAt);
            if (hit != null) return hit;

            try
            {
                _connectors = await kbService.GetConnectorStatsSnapshotAsync(ct);
            }
            catch
            {
                _connectors = Array.Empty<ConnectorStatsSnapshot>();
            }
            _connectorsAt = DateTime.UtcNow;
            return _connectors;
        }
        finally
        {
            _connectorGate.Release();
        }
    }

    public async Task<IReadOnlyList<KnowledgeBaseIndexStats>> GetIndexStatsAsync(
        IKnowledgeBaseService kbService, IKnowledgeBaseStorageManager? storage, CancellationToken ct = default)
    {
        var hit = Fresh(_index, _indexAt);
        if (hit != null) return hit;

        await _indexGate.WaitAsync(ct);
        try
        {
            hit = Fresh(_index, _indexAt);
            if (hit != null) return hit;

            var list = new List<KnowledgeBaseIndexStats>();
            try
            {
                if (storage != null)
                {
                    foreach (var kb in (await kbService.ListKnowledgeBasesAsync(ct)))
                    {
                        if (!kb.Enabled || !await storage.ExistsAsync(kb.Id, ct)) continue;
                        await using var lease = await storage.AcquireLeaseAsync(kb.Id, StorageAccess.ReadOnly, ct);
                        var lexical = await lease.Store.GetLexicalStatsAsync(ct);
                        var manifest = await lease.Store.GetManifestAsync(ct);
                        list.Add(new KnowledgeBaseIndexStats(
                            kb.Id, kb.Name, kb.EmbeddingSpecId,
                            lexical.TotalChunks, lexical.TotalDocuments,
                            manifest.ActiveVectorCount, lexical.TotalVocabularySize));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[McpStatsCache] 索引统计加载失败: " + ex.Message);
            }

            _index = list;
            _indexAt = DateTime.UtcNow;
            return list;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private static T? Fresh<T>(T? value, DateTime loadedAtUtc) where T : class =>
        value is not null && DateTime.UtcNow - loadedAtUtc < Ttl ? value : null;
}
