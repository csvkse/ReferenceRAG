namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 进程级作业协调器（Singleton）。
/// IKnowledgeBaseService 是 Scoped，请求级字典无法跨请求取消/单飞；
/// 本协调器集中持有 CTS 注册表与每知识库 reindex 单飞占位。
/// </summary>
public sealed class KnowledgeBaseJobCoordinator
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _jobCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _activeReindexByKb = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>尝试为知识库占据 reindex 单飞位；已有进行中作业时返回 false（→ HTTP 409）。</summary>
    public bool TryBeginReindex(string knowledgeBaseId, string jobId, CancellationTokenSource cts)
    {
        lock (_lock)
        {
            if (_activeReindexByKb.ContainsKey(knowledgeBaseId)) return false;
            _activeReindexByKb[knowledgeBaseId] = jobId;
            _jobCts[jobId] = cts;
            return true;
        }
    }

    public void EndReindex(string knowledgeBaseId, string jobId)
    {
        lock (_lock)
        {
            if (_activeReindexByKb.TryGetValue(knowledgeBaseId, out var current) &&
                string.Equals(current, jobId, StringComparison.OrdinalIgnoreCase))
            {
                _activeReindexByKb.Remove(knowledgeBaseId);
            }
            _jobCts.Remove(jobId);
        }
    }

    /// <summary>注册普通作业（sync 等）的取消源，供跨请求 CancelJobAsync 使用。</summary>
    public void Register(string jobId, CancellationTokenSource cts)
    {
        lock (_lock)
        {
            _jobCts[jobId] = cts;
        }
    }

    public void Unregister(string jobId)
    {
        lock (_lock)
        {
            _jobCts.Remove(jobId);
        }
    }

    public bool TryGetCts(string jobId, out CancellationTokenSource? cts)
    {
        lock (_lock)
        {
            return _jobCts.TryGetValue(jobId, out cts);
        }
    }

    public string? GetActiveReindexJobId(string knowledgeBaseId)
    {
        lock (_lock)
        {
            return _activeReindexByKb.TryGetValue(knowledgeBaseId, out var jobId) ? jobId : null;
        }
    }
}