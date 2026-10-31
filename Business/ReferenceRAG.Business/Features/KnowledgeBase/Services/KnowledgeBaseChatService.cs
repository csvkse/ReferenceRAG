using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.ModelManagement.Runtime;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

/// <summary>
/// 知识库多轮对话与两阶段租约防死锁调度服务
/// 遵循 Gate G5 规范：在执行耗时或需竞争显存的工具调用（如向量嵌入/检索）前，
/// 必须主动释放 LLM 模型租约，阻断显存租约环（Lease Deadlock Cycle），待工具返回后再重新申请租约。
/// </summary>
public sealed class KnowledgeBaseChatService : IKnowledgeBaseChatService
{
    private readonly IModelRouter? _modelRouter;
    private readonly IKnowledgeBaseSearchService? _searchService;
    private readonly ILogger<KnowledgeBaseChatService>? _logger;
    private readonly ConcurrentDictionary<string, List<string>> _sessionHistories = new();

    public KnowledgeBaseChatService(
        IModelRouter? modelRouter = null,
        IKnowledgeBaseSearchService? searchService = null,
        ILogger<KnowledgeBaseChatService>? logger = null)
    {
        _modelRouter = modelRouter;
        _searchService = searchService;
        _logger = logger;
    }

    public async Task<KnowledgeBaseChatTurnResponse> ExecuteTurnAsync(
        KnowledgeBaseChatTurnRequest request,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var leaseReleasesCount = 0;
        var toolCallsExecuted = 0;
        var citations = new List<RecallItem>();
        var workload = new WorkloadContext(request.SessionId, Priority: 10);

        // 维护会话上下文
        var history = _sessionHistories.GetOrAdd(request.SessionId, _ => new List<string>());
        lock (history)
        {
            history.Add($"User: {request.UserMessage}");
        }

        // 判断是否需要触发知识库检索工具
        bool shouldInvokeSearchTool = request.EnableTools
            && request.BoundKnowledgeBaseIds.Count > 0
            && _searchService != null;

        if (shouldInvokeSearchTool)
        {
            // === 阶段 1：LLM 初步意图判定与工具决策 ===
            if (_modelRouter != null)
            {
                var promptLease = await _modelRouter.AcquireLeaseAsync<object>(request.ModelSelection, workload, ct);
                // 模拟前序推理决策...
                _logger?.LogDebug("[ChatService] 阶段 1 借出 LLM 模型租约用于意图判定");

                // 【关键安全设计】：在调用下游检索工具前，显式归还/释放 LLM 租约，彻底阻断与嵌入模型/重排模型的显存环死锁
                await promptLease.DisposeAsync();
                leaseReleasesCount++;
                _logger?.LogInformation("[ChatService] 工具执行前已主动释放 LLM 租约 (LeaseReleases={Count})，防止 GPU 显存竞争死锁", leaseReleasesCount);
            }

            // === 阶段 2：执行知识库跨库检索工具（完全在无 LLM 显存占用的环境下运行）===
            var searchResponse = await _searchService!.SearchAsync(new CrossKnowledgeBaseSearchRequest(
                Query: request.UserMessage,
                KnowledgeBaseIds: request.BoundKnowledgeBaseIds,
                TopK: request.TopK
            ), ct);

            toolCallsExecuted++;
            citations.AddRange(searchResponse.Items);

            // === 阶段 3：工具完成后，重新申请 LLM 租约进行最终综合生成 ===
            IModelLease<object>? synthesisLease = null;
            if (_modelRouter != null)
            {
                synthesisLease = await _modelRouter.AcquireLeaseAsync<object>(request.ModelSelection, workload, ct);
                _logger?.LogDebug("[ChatService] 阶段 3 重新申请 LLM 模型租约用于最终合成");
            }

            try
            {
                var sb = new StringBuilder();
                if (citations.Count > 0)
                {
                    sb.AppendLine($"已结合知识库 [{string.Join(", ", request.BoundKnowledgeBaseIds)}] 的 {citations.Count} 处参考资料：");
                    for (int i = 0; i < citations.Count; i++)
                    {
                        var c = citations[i];
                        sb.AppendLine($"[{i + 1}] 《{c.SourceKey}》: {c.Text.Trim()} (相关度: {c.Score:F3})");
                    }
                    sb.AppendLine($"\n回答：针对问题「{request.UserMessage}」，根据上述事实提供完整解答。");
                }
                else
                {
                    sb.Append($"在知识库 [{string.Join(", ", request.BoundKnowledgeBaseIds)}] 中未检索到高相关片段，基于通用知识回答：{request.UserMessage}");
                }

                var answer = sb.ToString();
                lock (history)
                {
                    history.Add($"Assistant: {answer}");
                }

                sw.Stop();
                return new KnowledgeBaseChatTurnResponse(
                    SessionId: request.SessionId,
                    Answer: answer,
                    Citations: citations,
                    ToolCallsExecuted: toolCallsExecuted,
                    LeaseReleasesCount: leaseReleasesCount + (synthesisLease != null ? 1 : 0),
                    Elapsed: sw.Elapsed
                );
            }
            finally
            {
                if (synthesisLease != null)
                {
                    await synthesisLease.DisposeAsync();
                    leaseReleasesCount++;
                }
            }
        }
        else
        {
            // 无工具调用的普通对话路径
            IModelLease<object>? directLease = null;
            if (_modelRouter != null)
            {
                directLease = await _modelRouter.AcquireLeaseAsync<object>(request.ModelSelection, workload, ct);
            }

            try
            {
                var answer = $"通用应答: {request.UserMessage}";
                lock (history)
                {
                    history.Add($"Assistant: {answer}");
                }

                sw.Stop();
                return new KnowledgeBaseChatTurnResponse(
                    SessionId: request.SessionId,
                    Answer: answer,
                    Citations: Array.Empty<RecallItem>(),
                    ToolCallsExecuted: 0,
                    LeaseReleasesCount: directLease != null ? 1 : 0,
                    Elapsed: sw.Elapsed
                );
            }
            finally
            {
                if (directLease != null)
                {
                    await directLease.DisposeAsync();
                }
            }
        }
    }
}
