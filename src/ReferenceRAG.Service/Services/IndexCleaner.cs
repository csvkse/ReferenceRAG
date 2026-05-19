using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services.Graph;

namespace ReferenceRAG.Service.Services;

/// <summary>
/// 索引联动删除服务 — 统一协调 VectorStore / BM25 / Graph 三个存储的删除操作。
/// 直接调用 IVectorStore.Delete* 只清 SQLite，不清 BM25/Graph，会留下孤儿条目。
/// 所有删除逻辑必须经由本服务执行。
/// </summary>
public class IndexCleaner
{
    private readonly IVectorStore _vectorStore;
    private readonly IBM25Store _bm25Store;
    private readonly GraphIndexingService? _graphIndexing;
    private readonly ILogger<IndexCleaner>? _logger;

    public IndexCleaner(
        IVectorStore vectorStore,
        IBM25Store bm25Store,
        GraphIndexingService? graphIndexing = null,
        ILogger<IndexCleaner>? logger = null)
    {
        _vectorStore = vectorStore;
        _bm25Store = bm25Store;
        _graphIndexing = graphIndexing;
        _logger = logger;
    }

    /// <summary>
    /// 删除单个文件的全部索引数据（VectorStore + BM25 + Graph）。
    /// </summary>
    /// <param name="fileId">文件 ID</param>
    /// <param name="filePath">文件路径，传入时同步清理图节点</param>
    public async Task DeleteFileAsync(string fileId, string? filePath = null, CancellationToken ct = default)
    {
        // 先取 chunk ID 列表再删，BM25 按 chunk ID 删除
        var chunkIds = (await _vectorStore.GetChunksByFileAsync(fileId, ct))
                       .Select(c => c.Id).ToList();

        await _vectorStore.DeleteFileAsync(fileId, ct);

        if (chunkIds.Count > 0)
            await _bm25Store.DeleteDocumentsByIdsAsync(chunkIds);

        if (filePath != null && _graphIndexing != null)
            await _graphIndexing.RemoveAsync(filePath, ct);

        _logger?.LogDebug(
            "IndexCleaner: 删除文件 {FileId}, BM25 {BM25Count} 条, Graph {Graph}",
            fileId, chunkIds.Count, filePath != null ? "已清" : "跳过");
    }

    /// <summary>
    /// 删除某个源的全部索引数据（VectorStore + BM25 + Graph）。
    /// </summary>
    public async Task DeleteBySourceAsync(string source, CancellationToken ct = default)
    {
        // 先收集所有 chunk ID 和文件路径，DeleteBySourceAsync 执行后无法再查
        var files = (await _vectorStore.GetAllFilesAsync(ct))
                    .Where(f => f.Source == source)
                    .ToList();

        var chunkIds = new List<string>();
        foreach (var file in files)
        {
            var chunks = await _vectorStore.GetChunksByFileAsync(file.Id, ct);
            chunkIds.AddRange(chunks.Select(c => c.Id));
        }

        await _vectorStore.DeleteBySourceAsync(source, ct);

        if (chunkIds.Count > 0)
            await _bm25Store.DeleteDocumentsByIdsAsync(chunkIds);

        if (_graphIndexing != null)
            foreach (var file in files)
                await _graphIndexing.RemoveAsync(file.Path, ct);

        _logger?.LogDebug(
            "IndexCleaner: 删除源 {Source}, 文件 {FileCount} 个, BM25 {BM25Count} 条, Graph {Graph}",
            source, files.Count, chunkIds.Count, _graphIndexing != null ? "已清" : "跳过");
    }
}
