namespace ReferenceRAG.Core.Models;

/// <summary>
/// 嵌入模型上下文画像
/// 记录嵌入模型的物理上下文限制、注意力黄金区间、推荐重叠比例与特征
/// </summary>
public class ModelEmbeddingProfile
{
    /// <summary>模型名称</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>模型物理输入上限（如 512 或 8192）</summary>
    public int PhysicalMaxTokens { get; set; } = 512;

    /// <summary>黄金注意力区间下限（低于该值特征稀疏）</summary>
    public int SweetSpotMinTokens { get; set; } = 50;

    /// <summary>黄金注意力最佳目标值（兼顾信息密度与聚焦度）</summary>
    public int SweetSpotOptimalTokens { get; set; } = 360;

    /// <summary>黄金注意力区间上限（高于该值注意力分散）</summary>
    public int SweetSpotMaxTokens { get; set; } = 420;

    /// <summary>推荐重叠率（0.05 ~ 0.15）</summary>
    public float DefaultOverlapRatio { get; set; } = 0.10f;

    /// <summary>是否为 4k/8k 长文本大模型</summary>
    public bool SupportsMultiPageContext { get; set; } = false;

    /// <summary>向量维度</summary>
    public int VectorDimension { get; set; } = 512;
}
