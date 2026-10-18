using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.Adapters;

/// <summary>
/// 嵌入模型画像适配器接口
/// 用于探测并计算不同嵌入模型的物理上下文限制、最佳黄金区间与推荐参数
/// </summary>
public interface IModelProfileAdapter
{
    /// <summary>
    /// 是否支持该模型
    /// </summary>
    bool CanHandle(string? modelName, string? mode);

    /// <summary>
    /// 获取模型的嵌入上下文画像
    /// </summary>
    ModelEmbeddingProfile GetProfile(EmbeddingConfig? config);
}
