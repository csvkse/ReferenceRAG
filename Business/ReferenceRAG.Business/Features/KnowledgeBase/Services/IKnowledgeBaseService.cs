using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 知识库与连接器管理应用服务契约
/// </summary>
public interface IKnowledgeBaseService
{
    Task<KnowledgeBaseDefinition> CreateKnowledgeBaseAsync(
        string name,
        string? description,
        string? embeddingSpecId = null,
        EmbeddingSpec? customSpec = null,
        string? chunkingSpecId = null,
        ChunkingSpec? customChunkingSpec = null,
        CancellationToken ct = default);

    Task<KnowledgeBaseDefinition> UpdateKnowledgeBaseAsync(
        string id,
        UpdateKnowledgeBaseRequest request,
        CancellationToken ct = default);

    Task<bool> ToggleKnowledgeBaseAsync(string id, bool enabled, CancellationToken ct = default);

    Task<IReadOnlyList<EmbeddingSpec>> ListEmbeddingSpecsAsync(CancellationToken ct = default);

    Task<EmbeddingSpec?> GetEmbeddingSpecAsync(string id, CancellationToken ct = default);

    Task SaveEmbeddingSpecAsync(EmbeddingSpec spec, CancellationToken ct = default);

    Task<bool> DeleteEmbeddingSpecAsync(string specId, CancellationToken ct = default);

    Task<IReadOnlyList<ChunkingSpec>> ListChunkingSpecsAsync(CancellationToken ct = default);

    Task<ChunkingSpec?> GetChunkingSpecAsync(string id, CancellationToken ct = default);

    Task SaveChunkingSpecAsync(ChunkingSpec spec, CancellationToken ct = default);

    Task<bool> DeleteChunkingSpecAsync(string specId, CancellationToken ct = default);

    Task<ConfigDriftResult> CheckConfigDriftAsync(string knowledgeBaseId, CancellationToken ct = default);

    Task<ModelReindexResult> TriggerModelReindexAsync(string knowledgeBaseId, string? targetSpecId = null, CancellationToken ct = default);

    Task<KnowledgeBaseDefinition> UnbindModelAsync(string knowledgeBaseId, CancellationToken ct = default);

    Task<RechunkResult> TriggerRechunkAsync(string knowledgeBaseId, string? targetChunkingSpecId = null, CancellationToken ct = default);

    Task<KnowledgeBaseDefinition?> GetKnowledgeBaseAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<KnowledgeBaseDefinition>> ListKnowledgeBasesAsync(CancellationToken ct = default);

    Task<bool> DeleteKnowledgeBaseAsync(string id, CancellationToken ct = default);

    Task<ConnectorDefinition> AddConnectorAsync(
        string knowledgeBaseId,
        string kind,
        string targetPath,
        string? filePattern = null,
        CancellationToken ct = default);

    /// <summary>
    /// 幂等挂载：同一知识库内已存在相同路径的连接器时直接返回，不重复创建。
    /// 跨知识库允许同一路径多次挂载。
    /// </summary>
    Task<(ConnectorDefinition Connector, bool Created)> EnsureConnectorAsync(
        string knowledgeBaseId,
        string kind,
        string targetPath,
        string? filePattern = null,
        CancellationToken ct = default);

    Task<ConnectorDefinition> UpdateConnectorAsync(
        string knowledgeBaseId,
        string connectorId,
        UpdateConnectorRequest request,
        CancellationToken ct = default);

    Task<ConnectorDefinition?> GetConnectorAsync(string connectorId, CancellationToken ct = default);

    Task<IReadOnlyList<ConnectorDetailDto>> GetConnectorsAsync(string knowledgeBaseId, CancellationToken ct = default);

    Task<bool> DeleteConnectorAsync(string knowledgeBaseId, string connectorId, bool cascade = true, CancellationToken ct = default);

    Task<JobDefinition> TriggerSyncJobAsync(string knowledgeBaseId, string connectorId, CancellationToken ct = default);

    Task<JobDefinition?> GetJobAsync(string jobId, CancellationToken ct = default);

    Task<IReadOnlyList<JobDefinition>> ListJobsAsync(string? knowledgeBaseId = null, CancellationToken ct = default);

    Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default);

    Task<bool> DeleteJobAsync(string jobId, CancellationToken ct = default);

    Task<int> PruneJobsAsync(string? knowledgeBaseId = null, TimeSpan? olderThan = null, CancellationToken ct = default);

    // ==================== 文档增删改查 ====================
    Task<(IReadOnlyList<KnowledgeBaseDocumentRecord> Documents, int TotalCount)> GetDocumentsAsync(
        string knowledgeBaseId,
        int page = 1,
        int pageSize = 50,
        string? search = null,
        string? connectorId = null,
        CancellationToken ct = default);

    Task<KnowledgeBaseDocumentRecord?> GetDocumentByIdAsync(
        string knowledgeBaseId,
        string documentId,
        CancellationToken ct = default);

    Task<bool> DeleteDocumentByIdAsync(
        string knowledgeBaseId,
        string documentId,
        CancellationToken ct = default);

    Task<KnowledgeBaseDocumentRecord> CreateDocumentAsync(
        string knowledgeBaseId,
        string? connectorId,
        string? title,
        string content,
        string? sourceKey = null,
        CancellationToken ct = default);

    // ==================== 切片增删改查 (HITL) ====================
    Task<(IReadOnlyList<KnowledgeBaseChunkRecord> Chunks, int TotalCount)> GetChunksAsync(
        string knowledgeBaseId,
        string? documentId = null,
        int page = 1,
        int pageSize = 50,
        string? search = null,
        bool? enabledOnly = null,
        CancellationToken ct = default);

    Task<KnowledgeBaseChunkRecord?> GetChunkByIdAsync(
        string knowledgeBaseId,
        string chunkId,
        CancellationToken ct = default);

    Task<KnowledgeBaseChunkRecord> CreateChunkAsync(
        string knowledgeBaseId,
        CreateChunkRequest request,
        CancellationToken ct = default);

    Task<KnowledgeBaseChunkRecord?> UpdateChunkAsync(
        string knowledgeBaseId,
        string chunkId,
        UpdateChunkRequest request,
        CancellationToken ct = default);

    Task<bool> ToggleChunkAsync(
        string knowledgeBaseId,
        string chunkId,
        bool enabled,
        CancellationToken ct = default);

    Task<bool> DeleteChunkAsync(
        string knowledgeBaseId,
        string chunkId,
        CancellationToken ct = default);

    /// <summary>
    /// 获取指定知识库或批量知识库的连接器路由与目录树
    /// </summary>
    Task<PathsResponse> GetPathsAsync(IReadOnlyList<string> knowledgeBaseIds, CancellationToken ct = default);
}

/// <summary>
/// 跨知识库检索应用服务契约
/// </summary>
public interface IKnowledgeBaseSearchService
{
    Task<IReadOnlyList<KnowledgeBaseDefinition>> ListAccessibleKnowledgeBasesAsync(
        IReadOnlyList<string> authorizedSourceNames, CancellationToken ct = default);

    Task<CrossKnowledgeBaseSearchResponse> SearchAsync(
        CrossKnowledgeBaseSearchRequest request,
        CancellationToken ct = default);
}

public sealed record CrossKnowledgeBaseSearchRequest(
    string Query,
    IReadOnlyList<string> KnowledgeBaseIds,
    int TopK = 10,
    FailurePolicy FailurePolicy = FailurePolicy.AllowPartial,
    TimeSpan? Timeout = null,
    float[]? QueryVector = null,
    bool UseLexicalTokenizer = false,
    bool AllowDegrade = false,
    IReadOnlyList<string>? Folders = null)
{
    /// <summary>Trusted server-side API-key scope; null means unrestricted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string>? AuthorizedSourceNames { get; init; }
}

public sealed record CrossKnowledgeBaseSearchResponse(
    string Query,
    IReadOnlyList<RecallItem> Items,
    IReadOnlyList<string> SuccessfulKnowledgeBaseIds,
    IReadOnlyList<string> FailedKnowledgeBaseIds,
    bool PartialSuccess,
    ScoreKind ScoreKind,
    TimeSpan Elapsed,
    bool EarlyPruned = false,
    bool Degraded = false);
