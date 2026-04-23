namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// 向量编码模式
/// </summary>
public enum EmbeddingMode
{
    /// <summary>
    /// 对称编码（查询和文档使用相同编码）
    /// </summary>
    Symmetric,

    /// <summary>
    /// 查询编码（为查询文本添加前缀）
    /// </summary>
    Query,

    /// <summary>
    /// 文档编码（为文档文本添加前缀）
    /// </summary>
    Document
}

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
    /// 是否为模拟模式
    /// </summary>
    bool IsSimulationMode { get; }

    /// <summary>
    /// 是否支持非对称编码
    /// </summary>
    bool SupportsAsymmetricEncoding { get; }

    /// <summary>
    /// 重新加载模型。maxSequenceLength 为 null 时保持原有配置不变。
    /// </summary>
    Task<bool> ReloadModelAsync(string modelPath, string modelName, int? maxSequenceLength = null);

    /// <summary>
    /// 卸载模型（释放 ONNX session）
    /// </summary>
    void UnloadModel();

    /// <summary>
    /// 编码单个文本
    /// </summary>
    Task<float[]> EncodeAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量编码
    /// </summary>
    Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按模式编码单个文本（支持非对称编码）
    /// </summary>
    Task<float[]> EncodeAsync(string text, EmbeddingMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按模式批量编码（支持非对称编码）
    /// </summary>
    Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, EmbeddingMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// L2归一化
    /// </summary>
    float[] Normalize(float[] vector);

    /// <summary>
    /// 计算相似度（归一化后用内积）
    /// </summary>
    float Similarity(float[] a, float[] b);
}
