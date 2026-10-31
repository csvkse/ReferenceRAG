using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 对话轮次请求契约（支持绑定知识库与两阶段租约执行）
/// </summary>
public sealed record KnowledgeBaseChatTurnRequest(
    string SessionId,
    string UserMessage,
    IReadOnlyList<string> BoundKnowledgeBaseIds,
    ModelSelection ModelSelection,
    bool EnableTools = true,
    int TopK = 5);

/// <summary>
/// 对话轮次响应契约（包含引用与租约释放证据）
/// </summary>
public sealed record KnowledgeBaseChatTurnResponse(
    string SessionId,
    string Answer,
    IReadOnlyList<RecallItem> Citations,
    int ToolCallsExecuted,
    int LeaseReleasesCount,
    TimeSpan Elapsed);

/// <summary>
/// 知识库多轮对话与防死锁工具调度服务接口
/// </summary>
public interface IKnowledgeBaseChatService
{
    Task<KnowledgeBaseChatTurnResponse> ExecuteTurnAsync(
        KnowledgeBaseChatTurnRequest request,
        CancellationToken ct = default);
}
