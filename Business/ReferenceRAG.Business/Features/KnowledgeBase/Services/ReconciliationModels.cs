using System.Text.Json.Serialization;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>挂载漂移类型（与 CheckConfigDriftAsync 的「模型/分块配置漂移」是两套语义，勿混用）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceLinkDriftKind
{
    /// <summary>连接器指向的物理目录已不存在，且未找到迁移候选。</summary>
    Orphaned,

    /// <summary>config.Sources 中存在但没有任何连接器指向它的残留投影。</summary>
    Unlinked,

    /// <summary>连接器物理目录不存在，但检测到疑似改名/迁移的候选目录。</summary>
    Drifted
}

public sealed record SourceLinkDriftIssue(
    SourceLinkDriftKind Kind,
    string Path,
    string Message,
    string? SourceName = null,
    string? KnowledgeBaseId = null,
    string? KnowledgeBaseName = null,
    string? ConnectorId = null,
    IReadOnlyList<string>? RebindCandidates = null);

/// <summary>
/// 对账报告。首版为 ReportOnly：只产出问题，不自动删除或改写任何数据。
/// </summary>
public sealed record SourceLinkReconcileReport(
    DateTime GeneratedAtUtc,
    int KnowledgeBaseCount,
    int ConnectorCount,
    int SourceCount,
    IReadOnlyList<SourceLinkDriftIssue> Issues,
    bool ReportOnly = true)
{
    public int OrphanedCount => Issues.Count(i => i.Kind == SourceLinkDriftKind.Orphaned);
    public int UnlinkedCount => Issues.Count(i => i.Kind == SourceLinkDriftKind.Unlinked);
    public int DriftedCount => Issues.Count(i => i.Kind == SourceLinkDriftKind.Drifted);
}

/// <summary>保存最近一次对账结果，供定时作业与手动触发共享。</summary>
public interface IReconciliationState
{
    SourceLinkReconcileReport? Latest { get; }
    void Set(SourceLinkReconcileReport report);
}

public sealed class ReconciliationState : IReconciliationState
{
    private readonly object _gate = new();
    private SourceLinkReconcileReport? _latest;

    public SourceLinkReconcileReport? Latest
    {
        get { lock (_gate) return _latest; }
    }

    public void Set(SourceLinkReconcileReport report)
    {
        lock (_gate) _latest = report;
    }
}
