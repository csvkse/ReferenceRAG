using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Infrastructure.Features.Cognitive;

/// <summary>
/// TypeSafe AI Jev 认知决策引擎实现
/// </summary>
public sealed class JevDecisionEngine : IJevDecisionEngine
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("JevDecisionEngine");
    private readonly ISystemOneClient _client;
    private readonly float _retrievalThreshold;
    private readonly bool _enabled;

    public bool IsEnabled => _enabled && _client.IsConfigured;

    public JevDecisionEngine(
        ISystemOneClient client,
        bool enabled = true,
        float retrievalThreshold = 0.65f)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _enabled = enabled;
        _retrievalThreshold = retrievalThreshold;
    }

    public async Task<IntentClassificationResult> ClassifyIntentAsync(
        string userMessage,
        CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(userMessage))
        {
            return new IntentClassificationResult(
                PrimaryIntent: "unhandled",
                RequiresRetrieval: true,
                RetrievalProbability: 1.0,
                Confidence: 0.0,
                Rationale: "Jev 决策引擎未启用或输入文本为空");
        }

        var questions = new Dictionary<string, SystemOneQuestion>
        {
            ["intent"] = SystemOneQuestion.Choice(
                "What is the primary intent of this user query?",
                new Dictionary<string, string>
                {
                    ["rag_search"] = "Searching knowledge base, repository documentation, architecture or specific internal facts",
                    ["chit_chat"] = "Casual conversational greetings, small talk, pleasantries or non-retrieval chat",
                    ["code_task"] = "Writing, explaining, analyzing or refactoring programming code",
                    ["general_qa"] = "Common world knowledge questions not requiring private repository context"
                }),
            ["requires_retrieval"] = SystemOneQuestion.Noul(
                "Does answering this request require retrieving internal documents, notes, code, or domain specifications?")
        };

        try
        {
            var response = await _client.EvaluateAsync(userMessage, questions, ct);

            var intentAnswer = response.Answers.GetValueOrDefault("intent");
            var retrievalAnswer = response.Answers.GetValueOrDefault("requires_retrieval");

            var primaryIntent = intentAnswer?.Choice ?? "rag_search";
            var confidence = intentAnswer?.Confidence ?? 1.0;
            var prob = retrievalAnswer?.Noul ?? 0.8;

            var requiresRetrieval = prob >= _retrievalThreshold;

            return new IntentClassificationResult(
                PrimaryIntent: primaryIntent,
                RequiresRetrieval: requiresRetrieval,
                RetrievalProbability: prob,
                Confidence: confidence,
                Rationale: $"Jev 判定意图: {primaryIntent} (置信度: {confidence:P0}), 检索需求概率: {prob:P0}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JevDecisionEngine] 意图分类异常，安全兜底回退为需要检索: {Message}", ex.Message);
            return new IntentClassificationResult(
                PrimaryIntent: "rag_search",
                RequiresRetrieval: true,
                RetrievalProbability: 1.0,
                Confidence: 0.5,
                Rationale: $"决策异常兜底: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<ChunkRelevanceDecision>> PruneCandidateChunksAsync(
        string query,
        IReadOnlyList<string> candidateChunks,
        CancellationToken ct = default)
    {
        if (!IsEnabled || candidateChunks == null || candidateChunks.Count == 0)
        {
            if (candidateChunks == null || candidateChunks.Count == 0)
                return Array.Empty<ChunkRelevanceDecision>();

            return candidateChunks.Select((c, i) => new ChunkRelevanceDecision(i, c, true, 1.0)).ToList();
        }

        // 限制并发度批量评估
        var results = new ChunkRelevanceDecision[candidateChunks.Count];
        var tasks = candidateChunks.Select(async (chunk, index) =>
        {
            // 单分块状态构造
            var state = new
            {
                query,
                document_snippet = chunk.Length > 800 ? chunk.Substring(0, 800) + "..." : chunk
            };

            var questions = new Dictionary<string, SystemOneQuestion>
            {
                ["is_relevant"] = SystemOneQuestion.Noul(
                    "Does this document snippet contain information relevant or helpful to answering the query?"),
                ["relevance_level"] = SystemOneQuestion.Score(
                    "Rate the relevance of this document snippet to the query.",
                    new[] { "Irrelevant noise", "Partially relevant context", "Highly relevant core answer" })
            };

            try
            {
                var resp = await _client.EvaluateAsync(state, questions, ct);
                var noul = resp.Answers.GetValueOrDefault("is_relevant")?.Noul ?? 0.5;
                var score = resp.Answers.GetValueOrDefault("relevance_level")?.Score ?? noul;

                results[index] = new ChunkRelevanceDecision(
                    Index: index,
                    ChunkText: chunk,
                    IsRelevant: noul >= 0.4,
                    RelevanceScore: score);
            }
            catch
            {
                // 异常时保留候选块，避免误杀
                results[index] = new ChunkRelevanceDecision(index, chunk, true, 0.5);
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    public async Task<FaithfulnessVerificationResult> VerifyFaithfulnessAsync(
        string query,
        string referenceContext,
        string generatedAnswer,
        CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(generatedAnswer) || string.IsNullOrWhiteSpace(referenceContext))
        {
            return new FaithfulnessVerificationResult(true, 1.0, "门禁未启用或缺少上下文");
        }

        var state = new
        {
            user_question = query,
            reference_context = referenceContext.Length > 2000 ? referenceContext.Substring(0, 2000) + "..." : referenceContext,
            generated_answer = generatedAnswer
        };

        var questions = new Dictionary<string, SystemOneQuestion>
        {
            ["is_faithful"] = SystemOneQuestion.Noul(
                "Is the generated answer strictly faithful to and supported by the reference context without fabricating unverified facts?"),
            ["hallucination_risk"] = SystemOneQuestion.Score(
                "Evaluate the risk of hallucination or fabricated statements in the generated answer.",
                new[] { "No hallucination / strictly grounded", "Minor extrapolation / unverified nuance", "Severe fabrication / hallucinated content" })
        };

        try
        {
            var resp = await _client.EvaluateAsync(state, questions, ct);
            var faithfulNoul = resp.Answers.GetValueOrDefault("is_faithful")?.Noul ?? 1.0;
            var riskScore = resp.Answers.GetValueOrDefault("hallucination_risk")?.Score ?? 0.0;

            var isFaithful = faithfulNoul >= 0.6 && riskScore <= 1.2;

            return new FaithfulnessVerificationResult(
                IsFaithful: isFaithful,
                FaithfulnessScore: faithfulNoul,
                Assessment: $"Jev 忠实度判定: {faithfulNoul:P0}, 幻觉风险评分: {riskScore:F2}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[JevDecisionEngine] 事实一致性检验异常: {Message}", ex.Message);
            return new FaithfulnessVerificationResult(true, 1.0, $"检验异常兜底: {ex.Message}");
        }
    }
}
