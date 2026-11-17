namespace ReferenceRAG.Core.Interfaces;

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

    /// <summary>
    /// 卸载模型（释放 ONNX session）
    /// </summary>
    void UnloadModel();
}

/// <summary>重排分数的来源：Measured=模型真实打分；Degraded=模型不可用时的保底占位（不得作为相关性对外发布）。</summary>
public enum RerankScoreKind
{
    Measured = 0,
    Degraded = 1
}

/// <summary>
/// 重排结果
/// </summary>
public class RerankResult
{
    public string Query { get; set; } = "";
    public List<RerankDocument> Documents { get; set; } = new();
    public long DurationMs { get; set; }

    /// <summary>整批结果是否来自降级保底（模型缺 Rank 头 / 熔断 / 远端不可用 / 模拟模式）。</summary>
    public bool Degraded { get; set; }

    /// <summary>降级原因（Degraded=true 时给出可诊断的具体原因）。</summary>
    public string? DegradationReason { get; set; }

    public RerankScoreKind ScoreKind => Degraded ? RerankScoreKind.Degraded : RerankScoreKind.Measured;
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
    /// 相关性分数。降级路径必须留 null —— 把保底占位（如 0.85 - i*step）当测量值发布，
    /// 会让下游把「名次」误读成「相关性」。
    /// </summary>
    public double? RelevanceScore { get; set; }

    /// <summary>本条的分数来源。</summary>
    public RerankScoreKind ScoreKind { get; set; } = RerankScoreKind.Measured;
}
