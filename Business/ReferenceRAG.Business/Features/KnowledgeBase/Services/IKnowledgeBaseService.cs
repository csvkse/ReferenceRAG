using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 知识库与连接器管理应用服务契约
/// </summary>
public interface IKnowledgeBaseService
{
    Task<KnowledgeBaseDefinition> CreateKnowledgeBaseAsync(
        string name,
        string? description,
        string embeddingSpecId = "default-spec",
        EmbeddingSpec? customSpec = null,
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

    Task<KnowledgeBaseDefinition?> GetKnowledgeBaseAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<KnowledgeBaseDefinition>> ListKnowledgeBasesAsync(CancellationToken ct = default);

    Task<bool> DeleteKnowledgeBaseAsync(string id, CancellationToken ct = default);

    Task<ConnectorDefinition> AddConnectorAsync(
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

    Task<IReadOnlyList<ConnectorDefinition>> GetConnectorsAsync(string knowledgeBaseId, CancellationToken ct = default);

    Task<bool> DeleteConnectorAsync(string connectorId, CancellationToken ct = default);

    Task<JobDefinition> TriggerSyncJobAsync(string knowledgeBaseId, string connectorId, CancellationToken ct = default);

    Task<JobDefinition?> GetJobAsync(string jobId, CancellationToken ct = default);

    Task<IReadOnlyList<JobDefinition>> ListJobsAsync(string? knowledgeBaseId = null, CancellationToken ct = default);

    Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default);

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
    bool UseLexicalTokenizer = false)
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
    TimeSpan Elapsed);
