using System.Collections.Concurrent;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 简单 Token 计数器（估算）- 线程安全，支持并行，低内存分配。
/// 统一委托 TokenEstimator，保证与分块/搜索侧口径一致。
/// </summary>
public class SimpleTokenizer : ITokenizer
{
    /// <summary>
    /// 计算 token 数量
    /// </summary>
    public int CountTokens(string text)
    {
        return TokenEstimator.EstimateTokens(text);
    }

    /// <summary>
    /// 批量计算 token 数量（并行优化）
    /// </summary>
    public Dictionary<string, int> CountTokensBatch(IEnumerable<string> texts)
    {
        var textList = texts.ToList();
        if (textList.Count == 0) return new Dictionary<string, int>();

        // 小批量直接顺序处理，避免并行开销
        if (textList.Count < 10)
        {
            var result = new Dictionary<string, int>(textList.Count);
            foreach (var text in textList)
            {
                result[text] = CountTokens(text);
            }
            return result;
        }

        // 大批量并行处理
        var concurrentResult = new ConcurrentDictionary<string, int>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8)
        };

        Parallel.ForEach(textList, options, text =>
        {
            concurrentResult[text] = CountTokens(text);
        });

        return new Dictionary<string, int>(concurrentResult);
    }
}