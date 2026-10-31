namespace ReferenceRAG.Core.Features.KnowledgeBase.Domain;

/// <summary>
/// 任务状态（JobStatus，严格与内部执行阶段 OperationPhase 分离）
/// </summary>
public enum JobStatus
{
    Queued,
    Running,
    WaitingForInput,
    Cancelling,
    Succeeded,
    PartiallySucceeded,
    Failed,
    Cancelled
}

/// <summary>
/// 内部操作执行阶段（OperationPhase）
/// </summary>
public enum OperationPhase
{
    None,
    Scanning,
    Probing,
    Parsing,
    Profiling,
    Planning,
    Chunking,
    Rendering,
    Embedding,
    Projecting,
    Validating,
    Publishing,
    Quiescing,
    Invalidating,
    Building
}

/// <summary>
/// 尝试状态（AttemptStatus，用于断点续跑与恢复）
/// </summary>
public enum AttemptStatus
{
    Created,
    Running,
    Succeeded,
    Failed,
    Interrupted,
    Cancelled
}

/// <summary>
/// 知识库生命周期状态（KnowledgeBaseLifecycle）
/// </summary>
public enum KnowledgeBaseLifecycle
{
    Provisioning,
    Active,
    Deleting,
    Deleted,
    Faulted
}

/// <summary>
/// 检索能力状态（CapabilityStatus）
/// </summary>
public enum CapabilityStatus
{
    NotConfigured,
    Building,
    Ready,
    Pending,
    Unavailable,
    NeedsRebuild
}

/// <summary>
/// 模型运行时状态（ModelRuntimeState）
/// </summary>
public enum ModelRuntimeState
{
    Unloaded,
    Loading,
    Ready,
    Inferring,
    Draining,
    Unloading,
    Faulted
}

/// <summary>
/// 连接器状态（ConnectorState）
/// </summary>
public enum ConnectorState
{
    Disabled,
    Idle,
    Scanning,
    Watching,
    Backoff,
    Faulted
}

/// <summary>
/// 存储访问权限模式（StorageAccess）
/// </summary>
public enum StorageAccess
{
    ReadOnly,
    ReadWrite,
    Maintenance
}

/// <summary>
/// 检索容错与故障处理策略（FailurePolicy）
/// </summary>
public enum FailurePolicy
{
    Strict,
    AllowPartial
}

/// <summary>
/// 检索分数类型（ScoreKind）
/// </summary>
public enum ScoreKind
{
    Cosine,
    VectorDistance,
    BM25,
    RRF,
    Rerank
}

/// <summary>
/// 数据源变更类型（ChangeKind）
/// </summary>
public enum ChangeKind
{
    Upsert,
    Delete,
    Rename
}
