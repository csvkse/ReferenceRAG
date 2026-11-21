using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 知识库进行中的独占作业信息
/// </summary>
public sealed record ActiveKbJobInfo(string JobId, string Operation, CancellationTokenSource Cts);

/// <summary>
/// 进程级作业协调器（Singleton）。
/// IKnowledgeBaseService 是 Scoped，请求级字典无法跨请求取消/单飞；
/// 本协调器集中持有 CTS 注册表与每知识库长时写作业（sync、rechunk、reindex）单飞互斥占位。
/// </summary>
public sealed class KnowledgeBaseJobCoordinator
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CancellationTokenSource> _jobCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActiveKbJobInfo> _activeJobByKb = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 尝试为知识库占据单飞互斥位（sync、rechunk、reindex 等写操作）；已有进行中作业时返回 false（→ HTTP 409）。
    /// </summary>
    public bool TryBeginJob(
        string knowledgeBaseId,
        string jobId,
        string operation,
        CancellationTokenSource cts,
        out ActiveKbJobInfo? activeConflict)
    {
        lock (_lock)
        {
            if (_activeJobByKb.TryGetValue(knowledgeBaseId, out var existing))
            {
                activeConflict = existing;
                return false;
            }

            var info = new ActiveKbJobInfo(jobId, operation, cts);
            _activeJobByKb[knowledgeBaseId] = info;
            _jobCts[jobId] = cts;
            activeConflict = null;
            return true;
        }
    }

    /// <summary>
    /// 释放知识库单飞互斥位并注销 CTS
    /// </summary>
    public void EndJob(string knowledgeBaseId, string jobId)
    {
        lock (_lock)
        {
            if (_activeJobByKb.TryGetValue(knowledgeBaseId, out var current) &&
                string.Equals(current.JobId, jobId, StringComparison.OrdinalIgnoreCase))
            {
                _activeJobByKb.Remove(knowledgeBaseId);
            }
            _jobCts.Remove(jobId);
        }
    }

    /// <summary>尝试为知识库占据 reindex 单飞位；已有进行中作业时返回 false（→ HTTP 409）。</summary>
    public bool TryBeginReindex(string knowledgeBaseId, string jobId, CancellationTokenSource cts)
    {
        return TryBeginJob(knowledgeBaseId, jobId, JobOperation.Reindex, cts, out _);
    }

    public void EndReindex(string knowledgeBaseId, string jobId)
    {
        EndJob(knowledgeBaseId, jobId);
    }

    /// <summary>注册普通作业（sync、bm25、graph 等）的取消源，供跨请求 CancelJobAsync 使用。</summary>
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
            return _activeJobByKb.TryGetValue(knowledgeBaseId, out var info) &&
                   string.Equals(info.Operation, JobOperation.Reindex, StringComparison.OrdinalIgnoreCase)
                ? info.JobId
                : null;
        }
    }

    public ActiveKbJobInfo? GetActiveJob(string knowledgeBaseId)
    {
        lock (_lock)
        {
            return _activeJobByKb.TryGetValue(knowledgeBaseId, out var info) ? info : null;
        }
    }
}
