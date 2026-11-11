using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Business.Features.Search.Adapters;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Business.Features.Search.Cognitive;

/// <summary>
/// Cross-Encoder 重排模型认知证据评估适配器
/// 采用查询增强范式 (Prompt/Context-Augmented Query) 支持先验证据感知，
/// 并内置自适应 Sigmoid 归一化保障异构模型分值对齐。
/// </summary>
public sealed class CrossEncoderEvidenceAdapter : ICognitiveEvidenceEvaluator
{
    private readonly IRerankExecutionAdapter _rerankAdapter;
    private readonly float _threshold;
    private readonly ILogger<CrossEncoderEvidenceAdapter>? _logger;

    public string BackendName => "CrossEncoder";
    public bool IsAvailable => _rerankAdapter.IsAvailable;

    public CognitiveBackendCapabilities Capabilities => new(
        SupportsMultiHopContext: false, // 通过 Prompt-Augmented Query 注入上下文
        SupportsBatching: true,
        MaxContextTokens: 512,
        RequiresGpu: _rerankAdapter.ModelInfo?.IsGpu ?? false,
        RequiresNetwork: false);

    public CrossEncoderEvidenceAdapter(
        IRerankExecutionAdapter rerankAdapter,
        float threshold = 0.50f,
        ILogger<CrossEncoderEvidenceAdapter>? logger = null)
    {
        _rerankAdapter = rerankAdapter ?? throw new ArgumentNullException(nameof(rerankAdapter));
        _threshold = threshold;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, CognitiveScoreResult>> EvaluateBatchAsync(
        string originalQuery,
        IReadOnlyList<CandidateEvaluationItem> candidates,
        IReadOnlyList<CandidateEvaluationItem> previouslyRetrievedEvidence,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, CognitiveScoreResult>(StringComparer.OrdinalIgnoreCase);
        if (!IsAvailable || candidates == null || candidates.Count == 0)
        {
            return results;
        }

        // 构造前序证据增强型 Query (Prompt-Augmented Query)
        var query = originalQuery;
        if (previouslyRetrievedEvidence != null && previouslyRetrievedEvidence.Count > 0)
        {
            var contextSnippets = previouslyRetrievedEvidence
                .Take(2)
                .Select(e => e.Content.Length > 120 ? e.Content[..120] : e.Content);
            var mergedContext = string.Join("; ", contextSnippets);
            query = $"{originalQuery} [参考背景: {mergedContext}]";
        }

        // 准备候选文本（截断避免超出模型注意力上下文）
        var docTexts = candidates
            .Select(c => c.Content.Length > 800 ? c.Content[..800] : c.Content)
            .ToList();

        try
        {
            var rerankResult = await _rerankAdapter.RerankBatchAsync(query, docTexts, ct);

            for (int i = 0; i < candidates.Count; i++)
            {
                var item = candidates[i];
                var docScore = rerankResult.Documents.FirstOrDefault(d => d.Index == i);
                var rawScore = docScore?.RelevanceScore ?? 0.5;

                // Logit 自适应 Sigmoid 归一化
                var normalized = NormalizeScore(rawScore);
                results[item.ChunkId] = new CognitiveScoreResult(
                    item.ChunkId,
                    normalized,
                    normalized >= _threshold,
                    $"Cross-Encoder 打分: {normalized:F3}");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[CrossEncoderEvidenceAdapter] 重排评估异常，安全保底: {Message}", ex.Message);
            foreach (var item in candidates)
            {
                results[item.ChunkId] = new CognitiveScoreResult(
                    item.ChunkId,
                    0.5,
                    true,
                    "重排推断异常保底");
            }
        }

        return results;
    }

    /// <summary>
    /// 自适应 Sigmoid 归一化
    /// </summary>
    public static double NormalizeScore(double score)
    {
        if (double.IsNaN(score) || double.IsInfinity(score)) return 0.5;
        // 若分值已处于 [0.0, 1.0]，直接使用
        if (score >= 0.0 && score <= 1.0) return score;
        // 否则视为 Logit 进行 Sigmoid 变换
        return 1.0 / (1.0 + Math.Exp(-score));
    }
}
