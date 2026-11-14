using System.Text.Json.Serialization;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Domain;

/// <summary>
/// 知识库运行模式分类
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KnowledgeBaseMode
{
    /// <summary>纯词法知识库：未绑定向量模型，0 显存占用，全量 SQLite FTS5 BM25F 倒排索引</summary>
    PureLexical,

    /// <summary>混合知识库：绑定了向量模型，具备向量余弦检索与 FTS5 倒排混合检索及 Rerank</summary>
    Hybrid,

    /// <summary>纯向量知识库（预留扩展）</summary>
    VectorOnly
}

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
    DateTime UpdatedAt,
    string ChunkingSpecId = "spec_chunk_default")
{
    [JsonIgnore]
    public bool IsPureLexical => string.Equals(EmbeddingSpecId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(EmbeddingSpecId);

    public KnowledgeBaseMode Mode => IsPureLexical ? KnowledgeBaseMode.PureLexical : KnowledgeBaseMode.Hybrid;
}

/// <summary>
/// 分段配置规范契约（不可变 ChunkingSpec，支持按知识库独立配置与自适应策略）
/// </summary>
public sealed record ChunkingSpec(
    string Id,
    string Mode = "Adaptive",
    string StrategyType = "Balanced",
    string AdaptiveLevel = "Level2",
    int MaxTokens = 512,
    int MinTokens = 50,
    int OverlapTokens = 50,
    int TargetWindowUtilizationPercent = 70,
    bool PreserveCodeBlocks = true,
    bool PreserveTables = true,
    bool PreserveHeadings = true);

/// <summary>
/// 知识库配置偏离状态
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftStatus
{
    Aligned,
    Drifted,
    Customized
}

/// <summary>
/// 知识库配置偏离与升级诊断报告
/// </summary>
public sealed record ConfigDriftResult(
    string KnowledgeBaseId,
    DriftStatus Status,
    string? CurrentEmbeddingModel,
    string? GlobalDefaultEmbeddingModel,
    string? CurrentChunkingStrategy,
    string? GlobalChunkingStrategy,
    bool CanUpgrade,
    string? UpgradeRecommendation = null);

/// <summary>
/// 连接器目标物理路径探测状态
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectorReachabilityState
{
    Reachable,
    Unreachable,
    AccessDenied,
    Unknown
}

/// <summary>
/// 连接器定义实体（保存在全局 Catalog）
/// </summary>
[JsonDerivedType(typeof(ConnectorDefinition))]
[JsonDerivedType(typeof(ConnectorDetailDto))]
public record ConnectorDefinition(
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
/// 连接器综合详情（含物理切片统计与可达性及诊断）
/// </summary>
public sealed record ConnectorDetailDto(
    string Id,
    string KnowledgeBaseId,
    string Kind,
    string TargetPath,
    string? FilePattern,
    long ConfigRevision,
    bool Enabled,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int DocumentCount,
    int ChunkCount,
    bool IsReachable,
    ConnectorReachabilityState Reachability = ConnectorReachabilityState.Reachable,
    string? DiagnosticMessage = null) : ConnectorDefinition(Id, KnowledgeBaseId, Kind, TargetPath, FilePattern, ConfigRevision, Enabled, CreatedAt, UpdatedAt);

/// <summary>
/// 知识库词法统计指标
/// </summary>
public sealed record KnowledgeBaseLexicalStats(
    int TotalChunks,
    int TotalDocuments,
    int TotalVocabularySize,
    double AverageDocumentLength);

/// <summary>
/// 词法倒排索引生命周期状态（读路径只允许"读 + 打点 + 投递补齐意图"，任何状态都禁止同步写索引）
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LexicalIndexStatus
{
    /// <summary>库内无分块，无需索引</summary>
    Empty = 0,

    /// <summary>索引缺失（表不存在或为空），需要全量构建</summary>
    Absent = 1,

    /// <summary>索引落后于内容代次，需要增量补齐</summary>
    Stale = 2,

    /// <summary>索引就绪且与内容代次一致</summary>
    Ready = 3
}

/// <summary>
/// 词法索引状态快照（O(1) 裁决，不做任何写操作）
/// </summary>
public sealed record LexicalIndexStatusInfo(
    LexicalIndexStatus Status,
    int ChunkCount,
    int IndexedCount,
    long ContentGeneration,
    long LexicalGeneration,
    string? TokenizerVersion = null,
    long IndexedRowId = 0)
{
    /// <summary>补齐进度（0~100）；Ready 快路径未做 COUNT 时返回 100</summary>
    public int ProgressPercent => IndexedCount < 0 || ChunkCount <= 0
        ? 100
        : (int)Math.Clamp(IndexedCount * 100L / ChunkCount, 0, 100);
}

/// <summary>
/// 词法索引后台补齐进度
/// </summary>
public sealed record LexicalIndexProgress(
    string KnowledgeBaseId,
    int Processed,
    int Total,
    int BatchIndex,
    string Phase)
{
    public int Percent => Total <= 0 ? 100 : (int)Math.Clamp(Processed * 100L / Total, 0, 100);
}

/// <summary>
/// 词法索引补齐结果
/// </summary>
public sealed record LexicalIndexBackfillResult(
    string KnowledgeBaseId,
    int Indexed,
    int Total,
    long DurationMs,
    bool Completed,
    bool Rebuilt,
    string? Message = null);


/// <summary>
/// 逻辑嵌入模型规范契约（不可变 EmbeddingSpec，支持搜索与向量生成双通道解耦）
/// </summary>
public sealed record EmbeddingSpec(
    string Id,
    string ModelName,
    int Dimension,
    string Metric = "cosine",
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
    string? EmbeddingText = null,
    int StartLine = 0,
    int EndLine = 0,
    string? HeadingPath = null);

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
    string? ConnectorId = null,
    bool UseLexicalTokenizer = false,
    bool AllowDegrade = false,
    IReadOnlyList<string>? Folders = null)
{
    /// <summary>Trusted server-side document filter, evaluated before candidate limits.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, string, string?, bool>? AuthorizeDocument { get; init; }
}

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
    string? ConnectorId = null,
    string? Locator = null,
    int StartLine = 0,
    int EndLine = 0,
    string? HeadingPath = null,
    /// <summary>
    /// 查询词覆盖率（命中锚点数 / 锚点总数，0~1）。null 表示本次未计算
    /// （短查询无区分度、或走了向量/融合路径）。
    /// 用途：把"宽松兜底"与"精确命中"在界面上区分开，避免地板层结果被误认为精确匹配。
    /// </summary>
    double? Coverage = null,
    /// <summary>该结果来自召回地板层（锚点内容词在本语料不存在时的宽松兜底）。</summary>
    bool IsFloor = false);

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
    string? DegradationReason = null,
    bool EarlyPruned = false,
    /// <summary>本次召回是否触发了「召回地板」（宽松兜底）层，供上层透出给前端提示。</summary>
    bool UsedRecallFloor = false);

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
    DateTime UpdatedAt,
    string? PreviousEmbeddingSpecHash = null);

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

/// <summary>
/// 知识库关联文档记录
/// </summary>
public sealed record KnowledgeBaseDocumentRecord(
    string Id,
    string ConnectorId,
    string SourceKey,
    string? Locator,
    string? SourceRevision,
    string ContentHash,
    long DocumentVersion,
    int ChunkCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// 知识库关联切片记录
/// </summary>
public sealed record KnowledgeBaseChunkRecord(
    string Id,
    string DocumentId,
    long DocumentVersion,
    int Ordinal,
    string Text,
    int TokenCount,
    string? EmbeddingText,
    bool HasVector,
    bool Enabled,
    string? DocumentSourceKey,
    int StartLine = 0,
    int EndLine = 0,
    string? HeadingPath = null);

/// <summary>
/// 知识库更新请求
/// </summary>
public sealed record UpdateKnowledgeBaseRequest(
    string? Name = null,
    string? Description = null,
    bool? Enabled = null,
    string? EmbeddingSpecId = null,
    string? ChunkingSpecId = null);

/// <summary>
/// 知识库模型蓝绿影子重构索引结果
/// </summary>
/// <summary>
/// 作业操作类型常量（JobDefinition.Operation）
/// </summary>
public static class JobOperation
{
    public const string Sync = "sync";
    public const string Reindex = "reindex";
}

/// <summary>
/// 同知识库已有进行中的重构作业（单飞冲突 → HTTP 409）
/// </summary>
public sealed class KnowledgeBaseJobConflictException : InvalidOperationException
{
    public KnowledgeBaseJobConflictException(string message) : base(message) { }
}

/// <summary>
/// 蓝绿影子重构进度（每批回调）
/// </summary>
public sealed record ShadowReindexProgress(int ProcessedChunks, int TotalChunks);

/// <summary>
/// 蓝绿影子重构执行结果
/// </summary>
public sealed record ShadowReindexResult(
    int ProcessedChunks,
    int TotalChunks,
    long NewVectorGeneration,
    int ResumedChunks);

/// <summary>
/// 向量代次与影子/旧代表状态（供前端进度抽屉与回滚按钮裁决）
/// </summary>
public sealed record VectorGenerationInfo(
    string? ActiveSpecId,
    string? PreviousSpecId,
    bool CanRollback,
    int ActiveVectorCount,
    bool ShadowExists,
    int ShadowRowCount);

/// <summary>
/// 知识库重构作业面板状态：活跃作业 + 物理代次信息
/// </summary>
public sealed record ReindexJobStatus(
    JobDefinition? ActiveJob,
    VectorGenerationInfo Generation);

/// <summary>
/// 知识库重新分段请求
/// </summary>
public sealed record RechunkKnowledgeBaseRequest(
    string? TargetChunkingSpecId = null);

/// <summary>
/// 知识库重新分段与索引结果
/// </summary>
public sealed record RechunkResult(
    string KnowledgeBaseId,
    string TargetChunkingSpecId,
    int ProcessedDocuments,
    int ProcessedChunks,
    bool Success,
    string Message);

/// <summary>
/// 连接器更新请求
/// </summary>
public sealed record UpdateConnectorRequest(
    string? TargetPath = null,
    string? FilePattern = null,
    bool? Enabled = null);

/// <summary>
/// 切片创建请求
/// </summary>
public sealed record CreateChunkRequest(
    string? DocumentId,
    string Text,
    string? EmbeddingText = null);

/// <summary>
/// 切片更新请求
/// </summary>
public sealed record UpdateChunkRequest(
    string Text,
    string? EmbeddingText = null,
    bool RecomputeEmbeddingImmediately = true);

