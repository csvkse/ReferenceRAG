namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// BM25 存储接口 - 单一索引模式
/// </summary>
public interface IBM25Store
{
    // ==================== 索引操作 ====================

    /// <summary>
    /// 索引单个文档
    /// </summary>
    Task IndexDocumentAsync(string chunkId, string content);

    /// <summary>
    /// 批量索引文档
    /// </summary>
    Task IndexBatchAsync(IEnumerable<(string chunkId, string content)> documents, IProgress<int>? progress = null);

    /// <summary>
    /// 清空所有索引
    /// </summary>
    Task ClearIndexAsync();

    /// <summary>
    /// 删除指定 chunk ID 的文档索引
    /// </summary>
    Task DeleteDocumentsByIdsAsync(IEnumerable<string> chunkIds);

    // ==================== 搜索操作 ====================

    /// <summary>
    /// BM25 搜索
    /// </summary>
    Task<List<BM25SearchResult>> SearchAsync(string query, int topK = 10, float k1 = 1.5f, float b = 0.75f);

    // ==================== 状态查询 ====================

    /// <summary>
    /// 获取索引统计信息
    /// </summary>
    Task<BM25IndexStats> GetStatsAsync();

    /// <summary>
    /// 删除 BM25 索引中不在 validChunkIds 集合里的孤儿记录。
    /// 调用方从 IVectorStore.GetAllChunkIdsAsync() 获取有效 ID 集合后传入。
    /// </summary>
    Task<int> CleanupOrphanDocumentsAsync(IEnumerable<string> validChunkIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// BM25 索引统计信息
/// </summary>
public class BM25IndexStats
{
    public int TotalDocuments { get; set; }
    public double AverageDocLength { get; set; }
    public int VocabularySize { get; set; }
}

/// <summary>
/// BM25 搜索结果
/// </summary>
public class BM25SearchResult
{
    public string ChunkId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public double Score { get; set; }
    public int Rank { get; set; }
}
