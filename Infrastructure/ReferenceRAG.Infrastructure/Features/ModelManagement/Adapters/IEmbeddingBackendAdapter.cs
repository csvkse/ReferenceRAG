namespace ReferenceRAG.Core.Features.ModelManagement.Adapters;

/// <summary>
/// 向量嵌入底层引擎适配器统一接口
/// </summary>
public interface IEmbeddingBackendAdapter : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// 后端引擎标识（onnx, llama, openai 等）
    /// </summary>
    string BackendType { get; }

    /// <summary>
    /// 模型文件路径或标识
    /// </summary>
    string ModelPath { get; }

    /// <summary>
    /// 模型名称
    /// </summary>
    string ModelName { get; }

    /// <summary>
    /// 向量维度
    /// </summary>
    int Dimension { get; }

    /// <summary>
    /// 最大上下文序列长度
    /// </summary>
    int MaxSequenceLength { get; }

    /// <summary>
    /// 是否支持非对称检索前缀（Query/Document Prefix）
    /// </summary>
    bool SupportsAsymmetricEncoding { get; }

    /// <summary>
    /// 是否运行在模拟降级模式
    /// </summary>
    bool IsSimulationMode { get; }

    /// <summary>
    /// 异步初始化并加载权重
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 单文本向量提取
    /// </summary>
    Task<float[]> EncodeAsync(string text, string? prefix = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量文本向量提取
    /// </summary>
    Task<float[][]> EncodeBatchAsync(IReadOnlyList<string> texts, string? prefix = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 计算输入文本的 Token 数量
    /// </summary>
    int CountTokens(string text);
}
