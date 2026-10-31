using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.Search.Fusion;

/// <summary>
/// 基于名次倒数融合算法（Reciprocal Rank Fusion - RRF）的跨库候选融合策略
/// 规则：不比较各库异构余弦原始分数，统一按名次 1 / (60 + rank) 计算融合权重
/// </summary>
public sealed class RrfSearchFusionStrategy : ISearchFusionStrategy
{
    private readonly int _k;

    public ScoreKind ScoreKind => ScoreKind.RRF;

    public RrfSearchFusionStrategy(int k = 60)
    {
        _k = k > 0 ? k : 60;
    }

    public IReadOnlyList<RecallItem> Fuse(IReadOnlyList<RecallSnapshot> snapshots, int topK)
    {
        if (snapshots == null || snapshots.Count == 0 || topK <= 0)
        {
            return Array.Empty<RecallItem>();
        }

        // Map: chunkKey -> (Accumulated RRF Score, RecallItem)
        var merged = new Dictionary<string, (double TotalScore, RecallItem Item)>();

        foreach (var snapshot in snapshots)
        {
            if (snapshot.Items == null || snapshot.Items.Count == 0) continue;

            for (int rank = 0; rank < snapshot.Items.Count; rank++)
            {
                var item = snapshot.Items[rank];
                var rrfScore = 1.0 / (_k + rank + 1);

                var key = $"{snapshot.KnowledgeBaseId}::{item.ChunkId}";
                if (merged.TryGetValue(key, out var existing))
                {
                    merged[key] = (existing.TotalScore + rrfScore, existing.Item);
                }
                else
                {
                    merged[key] = (rrfScore, item);
                }
            }
        }

        return merged.Values
            .OrderByDescending(v => v.TotalScore)
            .ThenBy(v => v.Item.ChunkId)
            .Take(topK)
            .Select(v => v.Item with
            {
                Score = v.TotalScore,
                ScoreKind = ScoreKind.RRF
            })
            .ToList();
    }
}

/// <summary>
/// 基于加权线性归一化的跨库候选融合策略
/// </summary>
public sealed class WeightedScoreFusionStrategy : ISearchFusionStrategy
{
    private readonly IReadOnlyDictionary<string, double>? _kbWeights;

    public ScoreKind ScoreKind => ScoreKind.Cosine;

    public WeightedScoreFusionStrategy(IReadOnlyDictionary<string, double>? kbWeights = null)
    {
        _kbWeights = kbWeights;
    }

    public IReadOnlyList<RecallItem> Fuse(IReadOnlyList<RecallSnapshot> snapshots, int topK)
    {
        if (snapshots == null || snapshots.Count == 0 || topK <= 0)
        {
            return Array.Empty<RecallItem>();
        }

        var list = new List<RecallItem>();

        foreach (var snapshot in snapshots)
        {
            if (snapshot.Items == null || snapshot.Items.Count == 0) continue;

            double weight = 1.0;
            if (_kbWeights != null && _kbWeights.TryGetValue(snapshot.KnowledgeBaseId, out var w))
            {
                weight = w;
            }

            foreach (var item in snapshot.Items)
            {
                list.Add(item with
                {
                    Score = item.Score * weight
                });
            }
        }

        return list
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.ChunkId)
            .Take(topK)
            .ToList();
    }
}
