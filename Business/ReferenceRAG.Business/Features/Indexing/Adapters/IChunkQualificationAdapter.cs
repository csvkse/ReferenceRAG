using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Indexing.Adapters;

/// <summary>
/// 噪声分块类别
/// </summary>
public enum NoiseCategory
{
    None,
    SeparatorLine,      // 分隔线如 ---, ===
    PaginationMarker,   // 页码标记如 === (43-48) ===
    PunctuationOnly,    // 纯标点或空白
    LowDensityFragment, // 低信息密度碎片 (< 6 有效字符)
    TruncationRemnant   // 截断遗留符
}

/// <summary>
/// 入库处置建议动作
/// </summary>
public enum IngestionActionType
{
    Accept,             // 合格准入
    AbsorbToAdjacent,   // 吸收合并至相邻块 (保留行号跨度)
    Discard             // 彻底丢弃 (纯噪声阻断)
}

/// <summary>
/// 分块准入评估结果
/// </summary>
public sealed record ChunkQualificationResult
{
    public bool IsQualified { get; init; }
    public string Reason { get; init; } = string.Empty;
    public NoiseCategory Category { get; init; } = NoiseCategory.None;
    public IngestionActionType RecommendedAction { get; init; } = IngestionActionType.Accept;

    public static ChunkQualificationResult Qualified() =>
        new() { IsQualified = true, RecommendedAction = IngestionActionType.Accept };

    public static ChunkQualificationResult Unqualified(
        string reason,
        NoiseCategory category,
        IngestionActionType recommendedAction) =>
        new()
        {
            IsQualified = false,
            Reason = reason,
            Category = category,
            RecommendedAction = recommendedAction
        };
}

/// <summary>
/// 分块准入评估上下文
/// </summary>
public sealed class ChunkEvaluationContext
{
    public int ChunkIndex { get; init; }
    public int TotalChunks { get; init; }
    public string? FilePath { get; init; }
    public bool HasPreviousChunk { get; init; }
    public bool HasNextChunk { get; init; }
}

/// <summary>
/// 分块准入适配器接口
/// </summary>
public interface IChunkQualificationAdapter
{
    string AdapterName { get; }
    int Priority { get; }
    ChunkQualificationResult Evaluate(ChunkRecord chunk, ChunkEvaluationContext context);
}
