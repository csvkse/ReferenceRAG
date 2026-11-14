using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Storage;

/// <summary>
/// 知识库存储租约（生命周期管理与引用计数）
/// </summary>
public interface IStorageLease : IAsyncDisposable, IDisposable
{
    string KnowledgeBaseId { get; }
    long IndexEpochSnapshot { get; }
    long ReadRevisionSnapshot { get; }
    IKnowledgeBaseStore Store { get; }
}

/// <summary>
/// 单个知识库内部存储接口（对应特定 kb.db）
/// </summary>
public interface IKnowledgeBaseStore
{
    string KnowledgeBaseId { get; }

    /// <summary>
    /// 获取当前知识库物理内嵌的知识图谱存储契约
    /// </summary>
    IGraphStore GraphStore { get; }

    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// 确保词法索引就绪（仅供后台/索引期调用；读路径禁止调用，读路径请用 <see cref="GetLexicalIndexStatusAsync"/>）。
    /// </summary>
    Task EnsureLexicalIndexAsync(CancellationToken ct = default);

    /// <summary>
    /// O(1) 裁决词法索引状态，不执行任何写操作（建表/填充），可安全用于查询请求线程。
    /// </summary>
    Task<LexicalIndexStatusInfo> GetLexicalIndexStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// 后台分批补齐词法倒排索引（可断点续传、分批提交、并行分词），不阻塞查询。
    /// </summary>
    /// <param name="force">
    /// true = 忽略检查点，先清空词法表再从零重建（运维修复用：清理历史重复行 / 强制重分词）。
    /// </param>
    Task<LexicalIndexBackfillResult> BackfillLexicalIndexAsync(
        int batchSize = 500,
        IProgress<LexicalIndexProgress>? progress = null,
        CancellationToken ct = default,
        bool force = false);

    /// <summary>
    /// 清理词法索引中的重复行与孤儿行（FTS5 无唯一约束，历史并发补齐会留下重复行）。
    /// 仅在后台补齐收尾或运维端点调用，读路径禁止调用。
    /// </summary>
    /// <returns>被清理的行数。</returns>
    Task<int> RepairLexicalIndexAsync(CancellationToken ct = default);

    /// <summary>
    /// 合并 FTS5 段（<c>optimize</c>）。段碎片化会让「查询一个不存在的词」退化为
    /// 逐 segment 查 term 索引（实测 230ms vs 有命中 4ms）。
    /// 仅在后台补齐收尾或运维端点调用，读路径禁止调用（会重写整个倒排表）。
    /// </summary>
    /// <returns>是否成功。</returns>
    Task<bool> OptimizeLexicalIndexAsync(CancellationToken ct = default);

    Task<IndexManifest> GetManifestAsync(CancellationToken ct = default);

    Task<CommitResult> CommitDocumentAsync(IndexBatch batch, CancellationToken ct = default);

    Task<RecallSnapshot> RecallAsync(RecallRequest request, CancellationToken ct = default);

    Task<bool> HasAuthorizedDocumentsAsync(Func<string, string, string?, bool> authorizeDocument, CancellationToken ct = default);

    Task<bool> DeleteDocumentAsync(string connectorId, string sourceKey, CancellationToken ct = default);

    Task<long> RebuildFtsIndexAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取当前知识库真实的物理词法与切片统计指标
    /// </summary>
    Task<KnowledgeBaseLexicalStats> GetLexicalStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取当前知识库所有连接器与其下文档的路径树信息
    /// </summary>
    Task<IReadOnlyList<SourcePathInfo>> GetPathsAsync(IReadOnlyList<ConnectorDefinition> connectors, CancellationToken ct = default);

    Task<IReadOnlyList<ChunkData>> GetChunksMissingVectorsAsync(int limit = 100, CancellationToken ct = default);

    Task CloseAsync(CancellationToken ct = default);

    // ==================== 文档增删改查 ====================
    Task<(IReadOnlyList<KnowledgeBaseDocumentRecord> Documents, int TotalCount)> GetDocumentsAsync(
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? connectorId = null,
        CancellationToken ct = default);

    Task<KnowledgeBaseDocumentRecord?> GetDocumentByIdAsync(string documentId, CancellationToken ct = default);

    Task<bool> DeleteDocumentByIdAsync(string documentId, CancellationToken ct = default);

    /// <summary>
    /// 级联物理清空指定连接器在当前知识库下的所有文档、分块、向量与词法索引
    /// </summary>
    Task<int> PurgeConnectorDataAsync(string connectorId, CancellationToken ct = default);

    /// <summary>
    /// 获取指定连接器在当前知识库下的物理文档与分块统计
    /// </summary>
    Task<(int DocumentCount, int ChunkCount)> GetConnectorStatsAsync(string connectorId, CancellationToken ct = default);

    /// <summary>
    /// 手动录入单篇文档并自动分块索引（支持指定 ChunkingSpec）
    /// </summary>
    Task<KnowledgeBaseDocumentRecord> CreateDocumentAsync(
        string connectorId,
        string sourceKey,
        string? title,
        string content,
        ChunkingSpec? chunkingSpec = null,
        CancellationToken ct = default);

    // ==================== 切片增删改查 (HITL) ====================
    Task<(IReadOnlyList<KnowledgeBaseChunkRecord> Chunks, int TotalCount)> GetChunksAsync(
        string? documentId = null,
        int page = 1,
        int pageSize = 50,
        string? search = null,
        bool? enabledOnly = null,
        CancellationToken ct = default);

    Task<KnowledgeBaseChunkRecord?> GetChunkByIdAsync(string chunkId, CancellationToken ct = default);

    Task<KnowledgeBaseChunkRecord> CreateChunkAsync(
        string? documentId,
        string text,
        string? embeddingText = null,
        float[]? vector = null,
        CancellationToken ct = default);

    Task<KnowledgeBaseChunkRecord?> UpdateChunkAtomicAsync(
        string chunkId,
        string newText,
        string? embeddingText = null,
        float[]? newVector = null,
        CancellationToken ct = default);

    Task<bool> ToggleChunkAsync(string chunkId, bool enabled, CancellationToken ct = default);

    Task<bool> DeleteChunkByIdAsync(string chunkId, CancellationToken ct = default);

    /// <summary>
    /// 蓝绿影子重构索引：在保持当前向量检索零停机的情况下，在影子表中重算所有向量并原子切换。
    /// 影子表按 chunk_id + source_version 续跑（崩溃残留可复用）；运行期写路径自动维护影子一致性，
    /// 切换前在写栅栏内追平脏切片后单事务换表，并保留上一代 chunk_vectors_old 供回滚。
    /// 每批执行数量 + 维度 + 非零 + 有限值验收，任一不满足即抛异常（调用方负责清理影子表）。
    /// </summary>
    Task<ShadowReindexResult> ExecuteShadowModelReindexAsync(
        Func<IReadOnlyList<string>, Task<IReadOnlyList<float[]>>> vectorProvider,
        string newSpecHash,
        int newDimension,
        IProgress<ShadowReindexProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// 清理影子表（作业失败/取消后的终态清理；崩溃路径不调用，残留供下次续跑）。
    /// </summary>
    Task DropShadowVectorTableAsync(CancellationToken ct = default);

    /// <summary>
    /// 回滚到上一代向量（chunk_vectors_old → chunk_vectors），返回恢复的规格 Id；
    /// 无旧代时返回 null。回滚消耗旧代（一次性）。
    /// </summary>
    Task<string?> RollbackVectorGenerationAsync(CancellationToken ct = default);

    /// <summary>
    /// 读取向量代次信息（活跃/上一代规格、旧代可回滚、影子表残留），只读不写。
    /// </summary>
    Task<VectorGenerationInfo> GetVectorGenerationInfoAsync(CancellationToken ct = default);
}

/// <summary>
/// 知识库存储管理器（按库提供 Lease，隔离物理句柄与连接）
/// </summary>
public interface IKnowledgeBaseStorageManager
{
    ValueTask<IStorageLease> AcquireLeaseAsync(
        string knowledgeBaseId,
        StorageAccess access = StorageAccess.ReadOnly,
        CancellationToken ct = default);

    Task<bool> ExistsAsync(string knowledgeBaseId, CancellationToken ct = default);

    Task DeleteKnowledgeBaseAsync(string knowledgeBaseId, CancellationToken ct = default);

    Task BackupKnowledgeBaseAsync(string knowledgeBaseId, string backupFilePath, CancellationToken ct = default);

    Task RestoreKnowledgeBaseAsync(string knowledgeBaseId, string backupFilePath, CancellationToken ct = default);
}

/// <summary>
/// 全局元数据与目录存储（保存在 catalog.db）
/// </summary>
public interface ICatalogStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<KnowledgeBaseDefinition?> GetKnowledgeBaseAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<KnowledgeBaseDefinition>> ListKnowledgeBasesAsync(CancellationToken ct = default);

    Task<KnowledgeBaseDefinition> SaveKnowledgeBaseAsync(KnowledgeBaseDefinition kb, CancellationToken ct = default);

    Task<bool> DeleteKnowledgeBaseAsync(string id, CancellationToken ct = default);

    Task<ConnectorDefinition> SaveConnectorAsync(ConnectorDefinition connector, CancellationToken ct = default);

    Task<ConnectorDefinition?> GetConnectorAsync(string connectorId, CancellationToken ct = default);

    Task<IReadOnlyList<ConnectorDefinition>> GetConnectorsByKnowledgeBaseAsync(string kbId, CancellationToken ct = default);

    Task<bool> DeleteConnectorAsync(string connectorId, CancellationToken ct = default);

    Task<JobDefinition> SaveJobAsync(JobDefinition job, CancellationToken ct = default);

    Task<JobDefinition?> GetJobAsync(string jobId, CancellationToken ct = default);

    Task<IReadOnlyList<JobDefinition>> ListJobsAsync(string? kbId = null, CancellationToken ct = default);

    Task<bool> DeleteJobAsync(string jobId, CancellationToken ct = default);

    Task<int> PruneJobsAsync(string? kbId = null, TimeSpan? olderThan = null, CancellationToken ct = default);

    Task SaveEmbeddingSpecAsync(EmbeddingSpec spec, CancellationToken ct = default);

    Task<EmbeddingSpec?> GetEmbeddingSpecAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<EmbeddingSpec>> ListEmbeddingSpecsAsync(CancellationToken ct = default);

    Task<bool> DeleteEmbeddingSpecAsync(string specId, CancellationToken ct = default);

    Task SaveChunkingSpecAsync(ChunkingSpec spec, CancellationToken ct = default);

    Task<ChunkingSpec?> GetChunkingSpecAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<ChunkingSpec>> ListChunkingSpecsAsync(CancellationToken ct = default);

    Task<bool> DeleteChunkingSpecAsync(string specId, CancellationToken ct = default);
}
