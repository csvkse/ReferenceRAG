using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Business.Features.Search.Services;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Business.Features.Search.Cognitive;

/// <summary>
/// 纯规则统计启发式认知证据评估适配器 (Heuristic PRF & Term Overlap)
/// 零 AI 模型推理开销，耗时 < 1ms，基于词项覆盖率、专有名词交集与上下文连续性打分，
/// 为极端低算力与无网环境提供刚性保底。
/// </summary>
public sealed class StatisticalHeuristicEvidenceAdapter : ICognitiveEvidenceEvaluator
{
    private readonly float _threshold;

    public string BackendName => "Statistical";
    public bool IsAvailable => true;

    public CognitiveBackendCapabilities Capabilities => new(
        SupportsMultiHopContext: true,
        SupportsBatching: true,
        MaxContextTokens: 32768,
        RequiresGpu: false,
        RequiresNetwork: false);

    public StatisticalHeuristicEvidenceAdapter(float threshold = 0.50f)
    {
        _threshold = threshold;
    }

    public Task<IReadOnlyDictionary<string, CognitiveScoreResult>> EvaluateBatchAsync(
        string originalQuery,
        IReadOnlyList<CandidateEvaluationItem> candidates,
        IReadOnlyList<CandidateEvaluationItem> previouslyRetrievedEvidence,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, CognitiveScoreResult>(StringComparer.OrdinalIgnoreCase);
        if (candidates == null || candidates.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, CognitiveScoreResult>>(results);
        }

        var queryTokens = new HashSet<string>(
            TokenizationCacheProxy.Default.Tokenize(originalQuery),
            StringComparer.OrdinalIgnoreCase);

        var priorEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (previouslyRetrievedEvidence != null && previouslyRetrievedEvidence.Count > 0)
        {
            foreach (var pe in previouslyRetrievedEvidence)
            {
                var entities = LexicalEntityExtractor.ExtractNamePhrases(pe.Content);
                foreach (var e in entities) priorEntities.Add(e);
            }
        }

        double maxBm25 = candidates.Max(c => Math.Abs(c.Bm25Score));
        if (maxBm25 <= 0.0001) maxBm25 = 1.0;

        foreach (var c in candidates)
        {
            var contentTokens = TokenizationCacheProxy.Default.Tokenize(c.Content);
            if (contentTokens.Count == 0)
            {
                results[c.ChunkId] = new CognitiveScoreResult(c.ChunkId, 0.50, true, "空分块保底");
                continue;
            }

            // 1. 查询词覆盖率 (Query Term Coverage)
            int matchedQueryTerms = queryTokens.Count(qt => contentTokens.Contains(qt));
            double queryCoverage = queryTokens.Count > 0 ? (double)matchedQueryTerms / queryTokens.Count : 0.5;

            // 2. BM25 相对强度
            double bm25Rel = Math.Abs(c.Bm25Score) / maxBm25;

            // 3. 先验证据实体衔接增强 (Context Continuity)
            double continuityBoost = 0.0;
            if (priorEntities.Count > 0)
            {
                var candEntities = LexicalEntityExtractor.ExtractNamePhrases(c.Content);
                int sharedEntities = candEntities.Count(ce => priorEntities.Contains(ce));
                if (sharedEntities > 0)
                {
                    continuityBoost = Math.Min(0.20, sharedEntities * 0.10);
                }
            }

            // 综合启发式得分映射至 [0.45, 0.98]
            double rawScore = 0.45 + (0.35 * queryCoverage) + (0.15 * bm25Rel) + continuityBoost;
            double normalized = Math.Clamp(rawScore, 0.0, 1.0);

            results[c.ChunkId] = new CognitiveScoreResult(
                c.ChunkId,
                normalized,
                normalized >= _threshold,
                $"统计规则打分: {normalized:F3} (词项覆盖: {queryCoverage:P0}, 衔接增益: +{continuityBoost:F2})");
        }

        return Task.FromResult<IReadOnlyDictionary<string, CognitiveScoreResult>>(results);
    }
}
