using System.Collections.Generic;

namespace ReferenceRAG.Business.Features.Search.Benchmark.Contracts;

/// <summary>
/// 评测问题类型
/// </summary>
public enum BenchmarkQuestionType
{
    /// <summary>
    /// 单跳直接事实定位 (Single-hop Fact)
    /// </summary>
    SingleHop = 0,

    /// <summary>
    /// 实体线索桥接多跳 (Bridge Entity Multi-hop)
    /// </summary>
    MultiHopBridge = 1,

    /// <summary>
    /// 属性对比多跳 (Comparison Multi-hop)
    /// </summary>
    MultiHopComparison = 2
}

/// <summary>
/// 标准化通用知识库评测问题契约
/// </summary>
/// <param name="QuestionId">用例唯一标识符</param>
/// <param name="Query">用户自然语言查询</param>
/// <param name="QuestionType">问题推理类型</param>
/// <param name="GoldChunkIds">黄金答案切片 ID 列表</param>
/// <param name="SupportingEvidenceTitles">支撑事实标题/实体列表 (用于多跳证据链完整性度量)</param>
/// <param name="GroundTruthAnswer">标准答案文本 (可选)</param>
/// <param name="ContextCandidates">评测附带的候选段落列表 (可选)</param>
public sealed record BenchmarkQuestionCase(
    string QuestionId,
    string Query,
    BenchmarkQuestionType QuestionType,
    IReadOnlyList<string> GoldChunkIds,
    IReadOnlyList<string> SupportingEvidenceTitles,
    string? GroundTruthAnswer = null,
    IReadOnlyList<BenchmarkContextCandidate>? ContextCandidates = null
);

/// <summary>
/// 评测候选段落
/// </summary>
public sealed record BenchmarkContextCandidate(
    string Title,
    IReadOnlyList<string> Sentences
)
{
    public string FullText => string.Join(" ", Sentences);
}

/// <summary>
/// 通用知识库评测文档
/// </summary>
public sealed record BenchmarkCorpusDocument(
    string DocumentId,
    string Title,
    string Content,
    string? Category = null
);

/// <summary>
/// 学术与工业级综合评测度量结果
/// </summary>
public sealed record BenchmarkEvaluationMetrics(
    int TotalQueries,
    double Top1HitRate,
    double Top3HitRate,
    double Top5HitRate,
    double Top10HitRate,
    double Mrr10,
    double Ndcg10,
    double SupportingFactsRecall,
    double SupportingFactsExactMatchRate,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double LatencyP99Ms,
    double AverageLatencyMs,
    double Qps
);
