using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Infrastructure.Features.Cognitive;

namespace ReferenceRAG.Business.Features.Search.Cognitive;

/// <summary>
/// TypeSafe AI Jev (System One) 极速概率决策认知证据评估适配器
/// 原生支持在 state 中传递结构化 previously_retrieved_evidence，
/// 通过 noul 概率头实现 100ms 级事实链必要性判定。
/// </summary>
public sealed class JevSystemOneEvidenceAdapter : ICognitiveEvidenceEvaluator
{
    private readonly ISystemOneClient? _jevClient;
    private readonly float _threshold;
    private readonly ILogger<JevSystemOneEvidenceAdapter>? _logger;
    private readonly SemaphoreSlim _semaphore = new(5, 5);

    public string BackendName => "Jev";
    public bool IsAvailable => _jevClient != null && _jevClient.IsConfigured;

    public CognitiveBackendCapabilities Capabilities => new(
        SupportsMultiHopContext: true,
        SupportsBatching: true,
        MaxContextTokens: 4096,
        RequiresGpu: false,
        RequiresNetwork: true);

    public JevSystemOneEvidenceAdapter(
        ISystemOneClient? jevClient,
        float threshold = 0.50f,
        ILogger<JevSystemOneEvidenceAdapter>? logger = null)
    {
        _jevClient = jevClient;
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
        if (_jevClient == null || !_jevClient.IsConfigured || candidates == null || candidates.Count == 0)
        {
            return results;
        }

        var priorEvidencePayload = previouslyRetrievedEvidence?.Select(e => new
        {
            chunk_id = e.ChunkId,
            title = e.Title,
            snippet = e.Content.Length > 150 ? e.Content[..150] : e.Content
        }).ToList();

        var tasks = candidates.Select(async item =>
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                var state = new
                {
                    query = originalQuery,
                    candidate_title = item.Title,
                    candidate_content = item.Content.Length > 800 ? item.Content[..800] + "..." : item.Content,
                    previously_retrieved_evidence = priorEvidencePayload
                };

                var questions = new Dictionary<string, SystemOneQuestion>
                {
                    ["is_necessary_evidence"] = SystemOneQuestion.Noul(
                        "Does this candidate provide necessary, verifiable facts or context directly relevant to answering the query, considering any previously retrieved evidence?")
                };

                var resp = await _jevClient.EvaluateAsync(state, questions, ct);
                var noul = resp.Answers?.GetValueOrDefault("is_necessary_evidence")?.Noul ?? 0.5;

                return new CognitiveScoreResult(
                    item.ChunkId,
                    noul,
                    noul >= _threshold,
                    $"Jev Noul 判定: {noul:P1}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[JevSystemOneEvidenceAdapter] 候选 {ChunkId} 评估异常: {Message}", item.ChunkId, ex.Message);
                return new CognitiveScoreResult(
                    item.ChunkId,
                    0.50,
                    true,
                    $"Jev 异常保底: {ex.Message}");
            }
            finally
            {
                _semaphore.Release();
            }
        });

        var evaluated = await Task.WhenAll(tasks);
        foreach (var r in evaluated)
        {
            results[r.ChunkId] = r;
        }

        return results;
    }
}
