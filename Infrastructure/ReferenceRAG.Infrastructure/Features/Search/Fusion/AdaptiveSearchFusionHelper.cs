namespace ReferenceRAG.Core.Features.Search.Fusion;

/// <summary>
/// 意图感知自适应多路检索动态加权核算子 (Adaptive Dynamic Fusion Helper)
/// 摒弃传统僵化 1:1 无参融合，根据问句特征（实体密度、引号标记、长句场景、口语错字）动态计算 BM25 与 Vector 召回权重
/// </summary>
public static class AdaptiveSearchFusionHelper
{
    /// <summary>
    /// 根据查询特征动态计算 (BM25权重, 向量权重)
    /// </summary>
    public static (double Bm25Weight, double VectorWeight) CalculateWeights(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return (0.5, 0.5);

        // 1. 强实体定界符（书名号《...》、双引号“...”）或精确专名检索
        bool hasDelimiters = query.Contains('《') || query.Contains('》') || query.Contains('"') || query.Contains('“');
        bool isColloquial = query.Contains("为什么") || query.Contains("怎么") || query.Contains("如何")
                         || query.Contains("什么") || query.Contains("故事") || query.Contains("哪只")
                         || query.Contains("里头") || query.Contains("那个") || query.Contains("谁")
                         || query.Contains("经历") || query.Contains("到底") || query.Contains("在何处")
                         || query.Contains("在哪") || query.Contains("哪个地方") || query.Contains("篇章")
                         || query.Contains("有没有") || query.Contains("讲一个");

        // 短专有名词/定界符：实体精确度绝对优先，BM25 权重占优 (0.75 / 0.25)
        if (hasDelimiters || (!isColloquial && query.Length <= 8))
        {
            return (0.75, 0.25);
        }

        // 长句场景、口语现象或模糊描述：语义向量深层意图理解占优 (0.25 / 0.75)
        if (query.Length > 15 || isColloquial)
        {
            return (0.25, 0.75);
        }

        // 默认均衡
        return (0.5, 0.5);
    }
}
