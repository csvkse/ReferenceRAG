using System;
using System.Collections.Generic;
using System.Linq;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Rerank;

/// <summary>
/// 重排批次分片
/// </summary>
public sealed record RerankBatchPartition(
    int BatchIndex,
    int StartOffset,
    IReadOnlyList<string> Documents,
    IReadOnlyList<int> OriginalIndices);

/// <summary>
/// 重排 Token 预算管理与分批调度器
/// 防止大召回量场景下单次 HTTP POST 超过云端网关载荷上限 (HTTP 413 / 400)
/// </summary>
public sealed class RerankTokenBudgeter
{
    public int MaxDocChars { get; }
    public int MaxBatchSize { get; }

    public static RerankTokenBudgeter Default { get; } = new(maxDocChars: 3500, maxBatchSize: 32);

    public RerankTokenBudgeter(int maxDocChars = 3500, int maxBatchSize = 32)
    {
        MaxDocChars = Math.Max(10, maxDocChars);
        MaxBatchSize = Math.Max(1, maxBatchSize);
    }

    /// <summary>
    /// 对文档列表执行长度截断与批次切分
    /// </summary>
    public IReadOnlyList<RerankBatchPartition> Partition(IEnumerable<string> rawDocuments)
    {
        var docs = rawDocuments.ToList();
        if (docs.Count == 0) return Array.Empty<RerankBatchPartition>();

        var partitions = new List<RerankBatchPartition>();
        for (int i = 0; i < docs.Count; i += MaxBatchSize)
        {
            var count = Math.Min(MaxBatchSize, docs.Count - i);
            var batchDocs = new List<string>(count);
            var batchIndices = new List<int>(count);

            for (int j = 0; j < count; j++)
            {
                var originalIdx = i + j;
                var text = docs[originalIdx] ?? string.Empty;

                // 单文档预算截断，保护请求体
                var budgetedText = text.Length > MaxDocChars
                    ? text.Substring(0, MaxDocChars) + "..."
                    : text;

                batchDocs.Add(budgetedText);
                batchIndices.Add(originalIdx);
            }

            partitions.Add(new RerankBatchPartition(
                BatchIndex: partitions.Count,
                StartOffset: i,
                Documents: batchDocs,
                OriginalIndices: batchIndices));
        }

        return partitions;
    }
}
