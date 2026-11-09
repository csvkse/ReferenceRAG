using System;
using System.Collections.Generic;
using System.Linq;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Business.Features.Search.Services;

/// <summary>
/// 基于行列式点过程 (DPP) 与 Cholesky 投影分解的免向量多样性去重选择器
/// (借鉴 SAG-Wiki/SAG2 v10 核多样性算法，彻底摆脱 Dense 向量与 GPU 依赖)
/// </summary>
public static class DppKernelDiversitySelector
{
    /// <summary>
    /// 对候选集合执行基于纯文本稀疏核的 DPP 多样性去重重排
    /// </summary>
    /// <typeparam name="T">候选对象类型</typeparam>
    /// <param name="items">原始候选列表（按相关度分数降序排列）</param>
    /// <param name="textSelector">提取分块正文的委托</param>
    /// <param name="scoreSelector">提取分块打分（需 > 0）的委托</param>
    /// <param name="targetK">目标输出数量</param>
    /// <returns>兼顾高相关度与语义互斥性的多样化分块集合</returns>
    public static List<T> Diversify<T>(
        IReadOnlyList<T> items,
        Func<T, string> textSelector,
        Func<T, double> scoreSelector,
        int targetK = 10)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(textSelector);
        ArgumentNullException.ThrowIfNull(scoreSelector);

        if (items.Count <= 1 || targetK <= 1)
        {
            return items.Take(Math.Max(targetK, 0)).ToList();
        }

        // 为保证响应在 <1ms 内完成，截取前 N 个候选进行核投影计算 (通常取前 15~20 条)
        int n = Math.Min(items.Count, 20);
        var subList = items.Take(n).ToList();

        // 1. 构建每个候选分块的稀疏 TF 向量并 L2 归一化
        var sparseVectors = new Dictionary<string, double>[n];
        for (int i = 0; i < n; i++)
        {
            sparseVectors[i] = BuildNormalizedTfVector(textSelector(subList[i]));
        }

        // 2. 准备打分与核矩阵 Kernel[i, j]
        var scores = new double[n];
        var maxScore = 0.0;
        for (int i = 0; i < n; i++)
        {
            var raw = Math.Abs(scoreSelector(subList[i]));
            scores[i] = raw <= 1e-6 ? 0.01 : raw;
            if (scores[i] > maxScore) maxScore = scores[i];
        }

        // 归一化打分至 [0.1, 1.0] 防止由于尺度过小导致下溢
        if (maxScore > 0)
        {
            for (int i = 0; i < n; i++)
            {
                scores[i] = 0.1 + 0.9 * (scores[i] / maxScore);
            }
        }

        var kernel = new double[n, n];
        var residual = new double[n];

        for (int i = 0; i < n; i++)
        {
            residual[i] = scores[i] * scores[i];
            for (int j = 0; j < n; j++)
            {
                if (i == j)
                {
                    kernel[i, j] = residual[i];
                }
                else
                {
                    var sim = SparseCosine(sparseVectors[i], sparseVectors[j]);
                    kernel[i, j] = scores[i] * scores[j] * sim;
                }
            }
        }

        // 3. 贪心 Cholesky 投影分解选择
        var order = new List<int>(targetK);
        var remaining = new HashSet<int>(Enumerable.Range(0, n));
        var coefficients = new List<double>[n];
        for (int i = 0; i < n; i++)
        {
            coefficients[i] = new List<double>(targetK);
        }

        while (remaining.Count > 0 && order.Count < targetK)
        {
            // 选出当前残差方差最大的候选项作为本次赢家 (Winner)
            int winner = -1;
            double maxResidual = -1.0;

            foreach (var idx in remaining)
            {
                if (residual[idx] > maxResidual)
                {
                    maxResidual = residual[idx];
                    winner = idx;
                }
            }

            if (winner < 0 || maxResidual <= 1e-9)
            {
                // 剩余分块与已选分块高度冗余或已无方差，直接依次补充剩余项
                foreach (var rem in remaining.OrderByDescending(r => scores[r]))
                {
                    if (order.Count >= targetK) break;
                    order.Add(rem);
                }
                break;
            }

            order.Add(winner);
            remaining.Remove(winner);

            double denom = Math.Sqrt(residual[winner]);

            // 对剩余候选项进行正交投影消除方差
            foreach (var rem in remaining)
            {
                double dot = 0.0;
                for (int k = 0; k < coefficients[winner].Count; k++)
                {
                    dot += coefficients[winner][k] * coefficients[rem][k];
                }

                double projection = (kernel[winner, rem] - dot) / denom;
                coefficients[rem].Add(projection);
                residual[rem] = Math.Max(0.0, residual[rem] - projection * projection);
            }
        }

        // 4. 按选出的 DPP 序列重组最终返回列表
        var result = new List<T>(order.Count);
        foreach (var idx in order)
        {
            result.Add(subList[idx]);
        }

        // 如果原始请求需要更多数量（targetK > n），补充后续未参与核计算的元素
        if (result.Count < targetK && items.Count > n)
        {
            foreach (var trailing in items.Skip(n).Take(targetK - result.Count))
            {
                result.Add(trailing);
            }
        }

        return result;
    }

    private static Dictionary<string, double> BuildNormalizedTfVector(string text)
    {
        var tokens = TokenizationCacheProxy.Default.Tokenize(text);
        if (tokens.Count == 0) return new Dictionary<string, double>(0);

        var counts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens)
        {
            if (counts.TryGetValue(t, out var val))
            {
                counts[t] = val + 1.0;
            }
            else
            {
                counts[t] = 1.0;
            }
        }

        // 亚线性词频 log(1 + tf)
        double sumSq = 0.0;
        var keys = counts.Keys.ToList();
        foreach (var k in keys)
        {
            var weight = 1.0 + Math.Log(counts[k]);
            counts[k] = weight;
            sumSq += weight * weight;
        }

        double norm = Math.Sqrt(sumSq);
        if (norm > 0)
        {
            foreach (var k in keys)
            {
                counts[k] /= norm;
            }
        }

        return counts;
    }

    private static double SparseCosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;

        // 遍历较小的字典进行点乘
        var small = a.Count <= b.Count ? a : b;
        var large = a.Count <= b.Count ? b : a;

        double dot = 0.0;
        foreach (var kvp in small)
        {
            if (large.TryGetValue(kvp.Key, out var bVal))
            {
                dot += kvp.Value * bVal;
            }
        }

        return Math.Clamp(dot, 0.0, 1.0);
    }
}
