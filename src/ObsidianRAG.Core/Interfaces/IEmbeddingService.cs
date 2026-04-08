namespace ObsidianRAG.Core.Interfaces;

/// <summary>
/// 向量编码接口
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// 模型名称
    /// </summary>
    string ModelName { get; }
    
    /// <summary>
    /// 向量维度
    /// </summary>
    int Dimension { get; }
    
    /// <summary>
    /// 编码单个文本
    /// </summary>
    Task<float[]> EncodeAsync(string text, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// 批量编码
    /// </summary>
    Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// L2归一化
    /// </summary>
    float[] Normalize(float[] vector);
    
    /// <summary>
    /// 计算相似度（归一化后用内积）
    /// </summary>
    float Similarity(float[] a, float[] b);
}
