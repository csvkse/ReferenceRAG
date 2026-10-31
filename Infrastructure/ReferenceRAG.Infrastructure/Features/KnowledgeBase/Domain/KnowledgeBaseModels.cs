namespace ReferenceRAG.Core.Features.KnowledgeBase.Domain;

/// <summary>
/// 知识库定义实体（保存在全局 Catalog）
/// </summary>
public sealed record KnowledgeBaseDefinition(
    string Id,
    string Name,
    string? Description,
    bool Enabled,
    long DesiredConfigRevision,
    long EffectiveConfigRevision,
    string EmbeddingSpecId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// 连接器定义实体（保存在全局 Catalog）
/// </summary>
public sealed record ConnectorDefinition(
    string Id,
    string KnowledgeBaseId,
    string Kind,
    string TargetPath,
    string? FilePattern,
    long ConfigRevision,
    bool Enabled,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// 逻辑嵌入模型规范契约（不可变 EmbeddingSpec，支持搜索与向量生成双通道解耦）
/// </summary>
public sealed record EmbeddingSpec(
    string Id,
    string ModelName,
    int Dimension,
    string Metric,
    string? QueryPrefix = null,
    string? DocumentPrefix = null,
    int MaxTokens = 512,
    bool EnableDualChannel = false,
    string? IngestionBackend = null,
    string? IngestionBaseUrl = null,
    string? IngestionApiKey = null,
    string? SearchBackend = null);

/// <summary>
/// 数据源变更项
/// </summary>
public sealed record SourceChange(
    string ConnectorId,
    string SourceKey,
    ChangeKind Kind,
    string? Locator = null,
    string? SourceRevision = null,
    string? PreviousSourceKey = null);

/// <summary>
/// 数据源同步请求
/// </summary>
public sealed record SyncRequest(
    string? ScanId = null,
    bool FullScan = false,
    string? ContinuationToken = null);

/// <summary>
/// 分块持久化数据项
/// </summary>
public sealed record ChunkData(
    string ChunkId,
    int Ordinal,
    string Text,
    int TokenCount,
    string? EmbeddingText = null);

/// <summary>
/// 事务性提交文档批次
/// </summary>
public sealed record IndexBatch(
    string KnowledgeBaseId,
    long ExpectedIndexEpoch,
    long ExpectedDocumentVersion,
    string ConnectorId,
    string SourceKey,
    string? Locator,
    string? SourceRevision,
    string ContentHash,
    IReadOnlyList<ChunkData> Chunks,
    IReadOnlyList<float[]>? Vectors = null,
    string? ProducerCohort = null,
    string? EmbeddingSpecHash = null);

/// <summary>
/// 文档提交结果
/// </summary>
public sealed record CommitResult(
    bool Success,
    long PublishedDocumentVersion,
    long PublishedReadRevision,
    string? ErrorMessage = null);

/// <summary>
/// 知识库检索请求
/// </summary>
public sealed record RecallRequest(
    string Query,
    int TopK = 10,
    IReadOnlyDictionary<string, string>? MetadataFilters = null,
    float[]? QueryVector = null,
    string? ConnectorId = null);

/// <summary>
/// 命中结果项
/// </summary>
public sealed record RecallItem(
    string ChunkId,
    string DocumentId,
    string SourceKey,
    string Text,
    double Score,
    ScoreKind ScoreKind,
    long DocumentVersion,
    string? ConnectorId = null);

/// <summary>
/// 某库检索返回的快照
/// </summary>
public sealed record RecallSnapshot(
    string KnowledgeBaseId,
    long ReadRevision,
    long ContentGeneration,
    long VectorGeneration,
    IReadOnlyList<RecallItem> Items,
    bool Degraded = false,
    string? DegradationReason = null);

/// <summary>
/// 知识库索引 Manifest（保存在 kb.db 内部）
/// </summary>
public sealed record IndexManifest(
    string KnowledgeBaseId,
    long IndexEpoch,
    long ReadRevision,
    long ContentGeneration,
    long VectorGeneration,
    string? ActiveEmbeddingSpecHash,
    string? TargetEmbeddingSpecHash,
    long CorpusCohortSetRevision,
    int ActiveVectorCount,
    DateTime UpdatedAt);

/// <summary>
/// 作业定义（存储于 Catalog）
/// </summary>
public sealed record JobDefinition(
    string Id,
    string Operation,
    string KnowledgeBaseId,
    long ExpectedIndexEpoch,
    JobStatus Status,
    OperationPhase Phase,
    int TotalUnits,
    int ProcessedUnits,
    string? IdempotencyKey,
    string? ErrorMessage,
    DateTime CreatedAt,
    DateTime? FinishedAt);

/// <summary>
/// 作业尝试记录（用于持久化重试与断点恢复）
/// </summary>
public sealed record JobAttempt(
    string Id,
    string JobId,
    int AttemptNumber,
    AttemptStatus Status,
    string? CheckpointData,
    string? ErrorMessage,
    DateTime StartedAt,
    DateTime? CompletedAt);

/// <summary>
/// 模型运行时身份键（RuntimeKey）
/// </summary>
public sealed record RuntimeKey(
    string ModelId,
    string ImplementationRevision,
    string ExecutionProfileRevision,
    string Device,
    string Backend);

/// <summary>
/// 模型选择请求
/// </summary>
public sealed record ModelSelection(
    string ModelId,
    string Purpose,
    string? TargetRevision = null);

/// <summary>
/// 工作负载上下文
/// </summary>
public sealed record WorkloadContext(
    string RequestId,
    int Priority = 0,
    TimeSpan? Timeout = null,
    CancellationToken CancellationToken = default);

/// <summary>
/// 资源预算需求（显式区分加载峰值与稳态）
/// </summary>
public sealed record ResourceDemand(
    long PeakLoadVramBytes,
    long SteadyVramBytes,
    long SystemRamBytes,
    int DeviceId = 0);
