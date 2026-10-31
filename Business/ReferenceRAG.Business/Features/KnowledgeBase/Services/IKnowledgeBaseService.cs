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

    Task<IReadOnlyList<EmbeddingSpec>> ListEmbeddingSpecsAsync(CancellationToken ct = default);

    Task<KnowledgeBaseDefinition?> GetKnowledgeBaseAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<KnowledgeBaseDefinition>> ListKnowledgeBasesAsync(CancellationToken ct = default);

    Task<bool> DeleteKnowledgeBaseAsync(string id, CancellationToken ct = default);

    Task<ConnectorDefinition> AddConnectorAsync(
        string knowledgeBaseId,
        string kind,
        string targetPath,
        string? filePattern = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ConnectorDefinition>> GetConnectorsAsync(string knowledgeBaseId, CancellationToken ct = default);

    Task<bool> DeleteConnectorAsync(string connectorId, CancellationToken ct = default);

    Task<JobDefinition> TriggerSyncJobAsync(string knowledgeBaseId, string connectorId, CancellationToken ct = default);

    Task<JobDefinition?> GetJobAsync(string jobId, CancellationToken ct = default);

    Task<IReadOnlyList<JobDefinition>> ListJobsAsync(string? knowledgeBaseId = null, CancellationToken ct = default);

    Task<bool> CancelJobAsync(string jobId, CancellationToken ct = default);
}

/// <summary>
/// 跨知识库检索应用服务契约
/// </summary>
public interface IKnowledgeBaseSearchService
{
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
    float[]? QueryVector = null);

public sealed record CrossKnowledgeBaseSearchResponse(
    string Query,
    IReadOnlyList<RecallItem> Items,
    IReadOnlyList<string> SuccessfulKnowledgeBaseIds,
    IReadOnlyList<string> FailedKnowledgeBaseIds,
    bool PartialSuccess,
    ScoreKind ScoreKind,
    TimeSpan Elapsed);
