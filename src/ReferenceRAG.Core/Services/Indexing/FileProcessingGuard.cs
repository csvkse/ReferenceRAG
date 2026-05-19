using System.Collections.Concurrent;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 跨服务文件级互斥锁。IndexJobService 和 AutoIndexService 共享同一实例，
/// 防止同一文件被并发索引（向量重复、BM25重复、图谱错乱）。
/// </summary>
public sealed class FileProcessingGuard
{
    private readonly ConcurrentDictionary<string, byte> _inProgress =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryAcquire(string filePath) => _inProgress.TryAdd(filePath, 0);
    public void Release(string filePath)    => _inProgress.TryRemove(filePath, out _);
}
