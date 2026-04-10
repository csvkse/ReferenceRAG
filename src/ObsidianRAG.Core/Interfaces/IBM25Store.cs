namespace ObsidianRAG.Core.Interfaces;

/// <summary>
/// BM25 存储接口 - 支持多模型管理的倒排索引存储
/// </summary>
public interface IBM25Store
{
    // ==================== 模型管理 ====================

    /// <summary>
    /// 创建 BM25 模型
    /// </summary>
    Task<BM25ModelInfo> CreateModelAsync(string name, float k1 = 1.5f, float b = 0.75f);

    /// <summary>
    /// 获取所有模型
    /// </summary>
    Task<List<BM25ModelInfo>> GetAllModelsAsync();

    /// <summary>
    /// 获取模型信息
    /// </summary>
    Task<BM25ModelInfo?> GetModelInfoAsync(string name);

    /// <summary>
    /// 删除模型
    /// </summary>
    Task DeleteModelAsync(string name);

    /// <summary>
    /// 启用模型
    /// </summary>
    Task EnableModelAsync(string name);

    /// <summary>
    /// 禁用模型
    /// </summary>
    Task DisableModelAsync(string name);

    // ==================== 索引操作 ====================

    /// <summary>
    /// 索引单个文档
    /// </summary>
    Task IndexDocumentAsync(string modelName, string chunkId, string content);

    /// <summary>
    /// 批量索引文档
    /// </summary>
    Task IndexBatchAsync(string modelName, IEnumerable<(string chunkId, string content)> documents, IProgress<int>? progress = null);

    /// <summary>
    /// 重建完整索引
    /// </summary>
    Task RebuildFullIndexAsync(string modelName, IProgress<int>? progress = null);

    /// <summary>
    /// 清空模型的所有索引
    /// </summary>
    Task ClearModelAsync(string modelName);

    // ==================== 搜索操作 ====================

    /// <summary>
    /// BM25 搜索
    /// </summary>
    Task<List<BM25SearchResult>> SearchAsync(string modelName, string query, int topK = 10, float k1 = 1.5f, float b = 0.75f);

    // ==================== 状态查询 ====================

    /// <summary>
    /// 检查模型是否启用
    /// </summary>
    bool IsModelEnabled(string name);

    /// <summary>
    /// 检查模型是否存在
    /// </summary>
    bool ModelExists(string name);
}

/// <summary>
/// BM25 模型信息
/// </summary>
public class BM25ModelInfo
{
    public string Name { get; set; } = string.Empty;
    public double AverageDocLength { get; set; }
    public int TotalDocuments { get; set; }
    public int VocabularySize { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
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
