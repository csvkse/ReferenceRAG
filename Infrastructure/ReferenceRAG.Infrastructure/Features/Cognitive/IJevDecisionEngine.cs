using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReferenceRAG.Infrastructure.Features.Cognitive;

/// <summary>
/// 意图分类与决策结果
/// </summary>
public sealed record IntentClassificationResult(
    string PrimaryIntent,
    bool RequiresRetrieval,
    double RetrievalProbability,
    double Confidence,
    string Rationale);

/// <summary>
/// 候选文档相关性预筛结果
/// </summary>
public sealed record ChunkRelevanceDecision(
    int Index,
    string ChunkText,
    bool IsRelevant,
    double RelevanceScore);

/// <summary>
/// 事实一致性与忠实度验证结果
/// </summary>
public sealed record FaithfulnessVerificationResult(
    bool IsFaithful,
    double FaithfulnessScore,
    string Assessment);

/// <summary>
/// Jev 认知决策引擎高层接口
/// </summary>
public interface IJevDecisionEngine
{
    bool IsEnabled { get; }

    /// <summary>
    /// 极速判定用户输入的意图与是否需要知识库检索
    /// </summary>
    Task<IntentClassificationResult> ClassifyIntentAsync(
        string userMessage,
        CancellationToken ct = default);

    /// <summary>
    /// 对初筛召回的候选分块执行负样本轻量预淘汰
    /// </summary>
    Task<IReadOnlyList<ChunkRelevanceDecision>> PruneCandidateChunksAsync(
        string query,
        IReadOnlyList<string> candidateChunks,
        CancellationToken ct = default);

    /// <summary>
    /// 验证主生成模型的回答是否忠实于检索到的参考上下文
    /// </summary>
    Task<FaithfulnessVerificationResult> VerifyFaithfulnessAsync(
        string query,
        string referenceContext,
        string generatedAnswer,
        CancellationToken ct = default);
}
