using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Core.Features.ModelManagement.Adapters;

/// <summary>
/// 文本重排底层引擎适配器统一接口
/// </summary>
public interface IRerankBackendAdapter : IDisposable, IAsyncDisposable
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
    /// 模型是否就绪加载
    /// </summary>
    bool IsLoaded { get; }

    /// <summary>
    /// 异步初始化并加载权重
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 对单对查询与文档进行重排评分
    /// </summary>
    Task<double> RerankAsync(string query, string document, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量重排评分
    /// </summary>
    Task<RerankResult> RerankBatchAsync(string query, IEnumerable<string> documents, CancellationToken cancellationToken = default);
}
