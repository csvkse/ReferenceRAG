using System;
using System.Collections.Generic;
using System.Linq;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Infrastructure.Features.Search.Lexical;

/// <summary>
/// 轮询交错合并算法 (参考 SAG-Wiki core/src/retrieval.rs interleave)
/// 保证多路扩展召回候选列表的公平采样，杜绝某一路高频词项垄断整个候选池
/// </summary>
public static class InterleaveAlgorithm
{
    public static IReadOnlyList<RecallItem> Interleave(
        IReadOnlyList<IReadOnlyList<RecallItem>> lists,
        int limit)
    {
        if (lists == null || lists.Count == 0 || limit <= 0)
        {
            return Array.Empty<RecallItem>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RecallItem>(Math.Min(limit, 128));

        int maxDepth = lists.Max(l => l.Count);

        for (int rank = 0; rank < maxDepth; rank++)
        {
            foreach (var list in lists)
            {
                if (rank < list.Count)
                {
                    var item = list[rank];
                    if (seen.Add(item.ChunkId))
                    {
                        result.Add(item);
                        if (result.Count >= limit)
                        {
                            return result;
                        }
                    }
                }
            }
        }

        return result;
    }
}
