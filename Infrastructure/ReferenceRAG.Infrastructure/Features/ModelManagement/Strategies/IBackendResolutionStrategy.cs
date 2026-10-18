using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Features.ModelManagement.Strategies;

/// <summary>
/// 模型后端解析与创建策略接口
/// </summary>
public interface IBackendResolutionStrategy
{
    /// <summary>
    /// 后端类型名称（onnx, llama, openai 等）
    /// </summary>
    string StrategyName { get; }

    /// <summary>
    /// 判断此策略是否能处理指定路径与配置
    /// </summary>
    bool CanHandle(string modelPath, string? mode = null);

    /// <summary>
    /// 创建并初始化向量嵌入适配器
    /// </summary>
    Task<IEmbeddingBackendAdapter> CreateEmbeddingAdapterAsync(
        string modelPath,
        string modelName,
        EmbeddingConfig config,
        IGpuMemoryManager? memoryManager = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建并初始化文本重排适配器
    /// </summary>
    Task<IRerankBackendAdapter> CreateRerankAdapterAsync(
        string modelPath,
        string modelName,
        RerankConfig config,
        IGpuMemoryManager? memoryManager = null,
        CancellationToken cancellationToken = default);
}
