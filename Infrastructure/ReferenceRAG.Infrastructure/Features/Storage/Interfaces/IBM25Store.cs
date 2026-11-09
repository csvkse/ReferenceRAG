using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// BM25 存储接口 - 支持单一内容索引与多字段加权 (Title, Heading, Content)
/// </summary>
public interface IBM25Store
{
    // ==================== 索引操作 ====================

    /// <summary>
    /// 索引单个文档
    /// </summary>
    Task IndexDocumentAsync(string chunkId, string content);

    /// <summary>
    /// 索引单个包含标题与章节的多字段文档
    /// </summary>
    Task IndexDocumentAsync(string chunkId, string title, string heading, string content) =>
        IndexDocumentAsync(chunkId, content);

    /// <summary>
    /// 批量索引文档
    /// </summary>
    Task IndexBatchAsync(IEnumerable<(string chunkId, string content)> documents, IProgress<int>? progress = null);

    /// <summary>
    /// 批量索引包含标题与章节的多字段文档
    /// </summary>
    Task IndexBatchAsync(IEnumerable<(string chunkId, string title, string heading, string content)> documents, IProgress<int>? progress = null) =>
        IndexBatchAsync(documents.Select(d => (d.chunkId, d.content)), progress);

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
    /// BM25 搜索 (支持多字段加权: Title 5.0, Heading 2.0, Body 1.0)
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

    /// <summary>Checks both the exact ID set and indexed content for a completed full rebuild.</summary>
    Task<bool> VerifyExactIndexAsync(IReadOnlyDictionary<string, string> expectedDocuments, CancellationToken cancellationToken = default);
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
