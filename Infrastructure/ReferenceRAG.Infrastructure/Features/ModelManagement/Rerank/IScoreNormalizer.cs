using System;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Rerank;

/// <summary>
/// 重排打分体系归一化接口
/// 抹平 Logits (原始对数几率) 与 Sigmoid (0.0~1.0 概率) 的分值差异，使 ScoreThreshold 行为一致。
/// </summary>
public interface IScoreNormalizer
{
    /// <summary>
    /// 将原始分值转换为 [0.0, 1.0] 范围内的标准置信度分数
    /// </summary>
    double Normalize(double rawScore, string? modelName = null);
}

/// <summary>
/// 默认重排分数归一化器
/// </summary>
public sealed class ScoreNormalizer : IScoreNormalizer
{
    public static ScoreNormalizer Instance { get; } = new();

    public double Normalize(double rawScore, string? modelName = null)
    {
        if (double.IsNaN(rawScore) || double.IsInfinity(rawScore))
        {
            return 0.0;
        }

        // 1. 已处于 [0.0, 1.0] 闭区间内（如 BGE-Reranker-v2-m3 等原生 Sigmoid 概率）
        if (rawScore >= 0.0 && rawScore <= 1.0)
        {
            return rawScore;
        }

        // 2. 原始对数几率 Logits（如 bge-reranker-base、bge-reranker-large 等），应用 Sigmoid 函数变换
        // S(x) = 1 / (1 + exp(-x))
        return 1.0 / (1.0 + Math.Exp(-rawScore));
    }
}
