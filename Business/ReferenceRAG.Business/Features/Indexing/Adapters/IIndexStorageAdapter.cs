using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Indexing.Adapters;

/// <summary>
/// 索引存储适配器接口：隔离底库细节，提供外键防冲、路径归一化与原子安全写入保障。
/// </summary>
public interface IIndexStorageAdapter
{
    /// <summary>
    /// 标准化路径（统一盘符、斜杠规范化、去除尾部斜杠，解决大小写与正反斜杠比对不一致问题）
    /// </summary>
    string NormalizePath(string path);

    /// <summary>
    /// 确保文件记录已在 files 表持久化（若不存在则自动插入，防止级联外键冲突）
    /// </summary>
    Task<bool> EnsureFileRecordExistsAsync(FileRecord fileRecord, CancellationToken ct = default);

    /// <summary>
    /// 外键安全的分块批量写入：在写入 chunks 前校验父文件存在性，彻底消除 SQLite Error 19
    /// </summary>
    Task SafeUpsertChunksAsync(IEnumerable<ChunkRecord> chunks, CancellationToken ct = default);

    /// <summary>
    /// 安全写入向量批次
    /// </summary>
    Task SafeUpsertVectorsAsync(IEnumerable<VectorRecord> vectors, CancellationToken ct = default);

    /// <summary>
    /// 磁盘已删文件安全清理：严格防止删除正在索引的活跃文件、存在于磁盘的文件或互斥锁占用的文件
    /// </summary>
    Task<int> CleanDeletedFilesSafeAsync(
        IEnumerable<string> sourceNames,
        ISet<string> diskFilePaths,
        ISet<string> activeFilePaths,
        CancellationToken ct = default);
}
