using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// 向量存储接口
/// </summary>
public interface IVectorStore
{
    // ==================== 文件操作 ====================
    
    /// <summary>
    /// 插入或更新文件记录
    /// </summary>
    Task UpsertFileAsync(FileRecord file, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 获取文件记录
    /// </summary>
    Task<FileRecord?> GetFileAsync(string id, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 根据路径获取文件
    /// </summary>
    Task<FileRecord?> GetFileByPathAsync(string path, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 根据内容哈希获取文件
    /// </summary>
    Task<FileRecord?> GetFileByHashAsync(string contentHash, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 删除文件及其关联数据
    /// </summary>
    Task DeleteFileAsync(string id, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 获取所有文件
    /// </summary>
    Task<IEnumerable<FileRecord>> GetAllFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 流式获取所有文件记录（用于替代全量加载）
    /// </summary>
    Task<IAsyncEnumerable<FileRecord>> StreamAllFilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量按路径查找文件（IN 查询，替代 N 次 GetFileByPathAsync）
    /// </summary>
    Task<IEnumerable<FileRecord>> GetFilesByPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量按 fileId 获取所有 chunks（IN 查询，替代 N 次 GetChunksByFileAsync）
    /// </summary>
    Task<IEnumerable<ChunkRecord>> GetChunksByFileIdsAsync(IEnumerable<string> fileIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取文件总数（COUNT(*)，替代全表加载计数）
    /// </summary>
    Task<int> GetFileCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取每个源的聚合统计（SQL GROUP BY，替代全表加载后内存聚合）
    /// </summary>
    Task<IEnumerable<SourceFileStat>> GetSourceStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 分页获取指定源的文件列表
    /// </summary>
    Task<IEnumerable<FileRecord>> GetFilesBySourceAsync(string source, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新文件索引状态（'pending' / 'complete'）
    /// </summary>
    Task MarkFileStatusAsync(string fileId, string status, CancellationToken cancellationToken = default);
    
    // ==================== 分段操作 ====================
    
    /// <summary>
    /// 插入或更新分段记录
    /// </summary>
    Task UpsertChunkAsync(ChunkRecord chunk, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 批量插入分段
    /// </summary>
    Task UpsertChunksAsync(IEnumerable<ChunkRecord> chunks, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 获取分段记录
    /// </summary>
    Task<ChunkRecord?> GetChunkAsync(string id, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 获取文件的所有分段
    /// </summary>
    Task<IEnumerable<ChunkRecord>> GetChunksByFileAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取指定分段前后窗口范围内的分段
    /// </summary>
    Task<IEnumerable<ChunkRecord>> GetAdjacentChunksByFileAsync(
        string fileId,
        string chunkId,
        int windowSize,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 删除文件的所有分段
    /// </summary>
    Task DeleteChunksByFileAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除单个分段
    /// </summary>
    Task DeleteChunkAsync(string id, CancellationToken cancellationToken = default);
    
    // ==================== 向量操作 ====================
    
    /// <summary>
    /// 插入或更新向量记录
    /// </summary>
    Task UpsertVectorAsync(VectorRecord vector, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 批量插入向量
    /// </summary>
    Task UpsertVectorsAsync(IEnumerable<VectorRecord> vectors, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 获取向量记录
    /// </summary>
    Task<VectorRecord?> GetVectorAsync(string id, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 根据分段ID获取向量
    /// </summary>
    Task<VectorRecord?> GetVectorByChunkIdAsync(string chunkId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量根据分段ID获取向量
    /// </summary>
    Task<IReadOnlyDictionary<string, VectorRecord>> GetVectorsByChunkIdsAsync(
        IEnumerable<string> chunkIds,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 删除向量
    /// </summary>
    Task DeleteVectorAsync(string id, CancellationToken cancellationToken = default);
    
    // ==================== 检索操作 ====================

    /// <summary>
    /// 向量相似检索（使用第一个可用模型）
    /// </summary>
    Task<IEnumerable<SearchResult>> SearchAsync(
        float[] queryVector,
        int topK,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指定模型的向量相似检索
    /// </summary>
    Task<IEnumerable<SearchResult>> SearchAsync(
        float[] queryVector,
        string modelName,
        int topK,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 按聚合类型检索
    /// </summary>
    Task<IEnumerable<SearchResult>> SearchByAggregateTypeAsync(
        float[] queryVector,
        AggregateType aggregateType,
        int topK,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 在指定ID范围内检索
    /// </summary>
    Task<IEnumerable<SearchResult>> SearchInIdsAsync(
        float[] queryVector,
        IEnumerable<string> ids,
        int topK,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按源删除所有数据
    /// </summary>
    Task DeleteBySourceAsync(string source, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量存储向量记录
    /// </summary>
    Task StoreBatchAsync(IEnumerable<VectorRecord> records, CancellationToken cancellationToken = default);

    // ==================== 统计与管理操作 ====================

    /// <summary>
    /// 获取所有模型的向量统计信息
    /// </summary>
    Task<List<VectorStats>> GetVectorStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除指定模型的向量
    /// </summary>
    Task<int> DeleteVectorsByModelAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 仅删除指定文件的向量行，不碰 chunks 表（VectorOnly 重建用）
    /// </summary>
    Task DeleteVectorsByFileAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除无关联模型的向量（孤立项清理）
    /// </summary>
    Task<int> DeleteOrphanedVectorsAsync(IEnumerable<string> existingModelNames, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除向量表中 chunk_id 不在 chunks 表的孤儿向量
    /// </summary>
    Task<int> CleanupOrphanChunkVectorsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 返回 chunks 表中所有 chunk id（供跨 DB 孤儿清理使用）
    /// </summary>
    Task<IEnumerable<string>> GetAllChunkIdsAsync(CancellationToken cancellationToken = default);

}

/// <summary>
/// 按源聚合的文件统计
/// </summary>
public class SourceFileStat
{
    public string Source { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public int ChunkCount { get; set; }
    public DateTime? LastIndexed { get; set; }
}

/// <summary>
/// 检索结果
/// </summary>
public class SearchResult
{
    public string ChunkId { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? Source { get; set; }
    public string? Title { get; set; }
    public string Content { get; set; } = string.Empty;
    public float Score { get; set; }
    public float BM25Score { get; set; }
    public float EmbeddingScore { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? HeadingPath { get; set; }
    public int Level { get; set; }
    public int ChildChunkCount { get; set; }
    public AggregateType AggregateType { get; set; }

    /// <summary>
    /// 重排分数（启用重排时有效）
    /// </summary>
    public double? RerankScore { get; set; }
}
