using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReferenceRAG.Business.Features.Search.Cognitive;

/// <summary>
/// 认知后端能力特性声明
/// </summary>
public sealed record CognitiveBackendCapabilities(
    bool SupportsMultiHopContext,      // 是否原生支持摄入 priorEvidence 上下文
    bool SupportsBatching,              // 是否支持多候选批量并发打分
    int MaxContextTokens,               // 最大上下文窗口 Token 限制
    bool RequiresGpu,                   // 是否依赖本地 GPU
    bool RequiresNetwork);              // 是否依赖外网 API

/// <summary>
/// 单个候选分块的认知评估结果
/// </summary>
public sealed record CognitiveScoreResult(
    string ChunkId,
    double NormalizedScore,            // 归一化得分 [0.0, 1.0]
    bool IsAccepted,                   // 是否被判定为有效证据 (高于阈值)
    string? Reason = null);            // 判定原因或元数据

/// <summary>
/// 待评估候选分块载荷
/// </summary>
public sealed record CandidateEvaluationItem(
    string ChunkId,
    string Title,
    string Heading,
    string Content,
    double Bm25Score,
    double? PreviousScore = null,
    string? FileId = null,
    string? FilePath = null,
    string? Source = null,
    int StartLine = 0,
    int EndLine = 0,
    string? HeadingPath = null,
    string? Locator = null);

/// <summary>
/// 统一认知证据评估适配器接口
/// </summary>
public interface ICognitiveEvidenceEvaluator
{
    /// <summary>后端名称（Jev, CrossEncoder, Statistical）</summary>
    string BackendName { get; }

    /// <summary>当前后端是否就绪可用</summary>
    bool IsAvailable { get; }

    /// <summary>当前后端的能力声明</summary>
    CognitiveBackendCapabilities Capabilities { get; }

    /// <summary>
    /// 对一批候选分块进行认知判别与打分
    /// </summary>
    /// <param name="originalQuery">用户原始提问</param>
    /// <param name="candidates">当前待评候选分块集</param>
    /// <param name="previouslyRetrievedEvidence">前序 Hop 已锁定的证据集 (作为上下文)</param>
    /// <param name="ct">取消令牌</param>
    Task<IReadOnlyDictionary<string, CognitiveScoreResult>> EvaluateBatchAsync(
        string originalQuery,
        IReadOnlyList<CandidateEvaluationItem> candidates,
        IReadOnlyList<CandidateEvaluationItem> previouslyRetrievedEvidence,
        CancellationToken ct = default);
}
