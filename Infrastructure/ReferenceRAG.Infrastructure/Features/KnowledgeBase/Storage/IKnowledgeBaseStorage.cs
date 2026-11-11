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

    Task EnsureLexicalIndexAsync(CancellationToken ct = default);

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
    /// 蓝绿影子重构索引：在保持当前向量检索零停机的情况下，在影子表中重算所有向量并原子切换
    /// </summary>
    Task<int> ExecuteShadowModelReindexAsync(
        Func<IReadOnlyList<string>, Task<IReadOnlyList<float[]>>> vectorProvider,
        string newSpecHash,
        int newDimension,
        CancellationToken ct = default);
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
