namespace ObsidianRAG.Core.Interfaces;

/// <summary>
/// 重排服务接口
/// </summary>
public interface IRerankService
{
    /// <summary>
    /// 模型名称
    /// </summary>
    string ModelName { get; }
    
    /// <summary>
    /// 模型是否已加载
    /// </summary>
    bool IsLoaded { get; }
    
    /// <summary>
    /// 对单个查询-文档对进行重排评分
    /// </summary>
    Task<double> RerankAsync(string query, string document, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 对多个文档进行批量重排评分
    /// </summary>
    Task<RerankResult> RerankBatchAsync(string query, IEnumerable<string> documents, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 重新加载模型
    /// </summary>
    Task<bool> ReloadModelAsync(string modelPath, string modelName);
}

/// <summary>
/// 重排结果
/// </summary>
public class RerankResult
{
    public string Query { get; set; } = "";
    public List<RerankDocument> Documents { get; set; } = new();
    public long DurationMs { get; set; }
}

/// <summary>
/// 重排文档
/// </summary>
public class RerankDocument
{
    /// <summary>
    /// 文档ID
    /// </summary>
    public string? Id { get; set; }
    
    /// <summary>
    /// 文档索引
    /// </summary>
    public int Index { get; set; }
    
    /// <summary>
    /// 文档内容
    /// </summary>
    public string Document { get; set; } = "";
    
    /// <summary>
    /// 文档内容（别名）
    /// </summary>
    public string Text { get => Document; set => Document = value; }
    
    /// <summary>
    /// 相关性分数
    /// </summary>
    public double RelevanceScore { get; set; }
}
