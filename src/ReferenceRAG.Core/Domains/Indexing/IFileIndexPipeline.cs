using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// 单文件索引流水线 — 所有涉及「给一个文件建/删索引」的操作都经此接口。
/// 消除 IndexJobService / AutoIndexService 之间的逻辑重复。
/// </summary>
public interface IFileIndexPipeline
{
    /// <summary>
    /// Phase 1: hash检测、分块、清旧向量/BM25。
    /// 返回 null 表示内容未变化且非强制，无需重建。
    /// </summary>
    Task<FileProcessContext?> PrepareAsync(
        string filePath,
        IReadOnlyList<SourceFolder> sources,
        bool force,
        CancellationToken ct = default);

    /// <summary>
    /// Phase 1 (VectorOnly 模式): 读已有 chunks，删旧向量，不重新分块。
    /// </summary>
    Task<FileProcessContext?> PrepareVectorOnlyAsync(
        string filePath,
        CancellationToken ct = default);

    /// <summary>
    /// Phase 3: BM25索引 + 图谱更新（GPU推理已在外部完成）。
    /// updateGraph=false 时仅更新 BM25，跳过图谱（VectorOnly 场景）。
    /// </summary>
    Task FinalizeAsync(
        FileProcessContext ctx,
        IReadOnlyDictionary<string, string> filenameMap,
        CancellationToken ct = default,
        bool updateGraph = true);

    /// <summary>
    /// 单文件全流程 (Phase1 + GPU + Phase3)。AutoIndexService 使用。
    /// oldFilePath 非空时先清理旧路径（重命名场景）。
    /// </summary>
    Task<bool> IndexSingleAsync(
        string filePath,
        IReadOnlyList<SourceFolder> sources,
        string? oldFilePath = null,
        CancellationToken ct = default);

    /// <summary>
    /// 删除单文件的全部索引（向量 + BM25 + 图谱含heading节点）。
    /// </summary>
    Task DeleteFileAsync(
        string fileId,
        string? filePath = null,
        CancellationToken ct = default);

    /// <summary>
    /// 按路径查找文件并删除其全部索引。文件不存在时静默返回 false。
    /// </summary>
    Task<bool> DeleteFileByPathAsync(
        string filePath,
        CancellationToken ct = default);

    /// <summary>
    /// 删除某源的全部索引（含该源下所有文件的向量 + BM25 + 图谱）。
    /// </summary>
    Task DeleteSourceAsync(
        string sourceName,
        CancellationToken ct = default);
}

/// <summary>
/// Phase 1 → Phase 2 → Phase 3 之间传递的文件上下文。
/// </summary>
public class FileProcessContext
{
    public FileRecord FileRecord { get; set; } = null!;
    public string Content { get; set; } = "";
    public List<ChunkRecord> Chunks { get; set; } = new();
    public List<string> OldChunkIds { get; set; } = new();
}
