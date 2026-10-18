using ReferenceRAG.Business.Features.Indexing.StateMachine;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Business.Features.Indexing.Strategies;

/// <summary>
/// 索引任务执行策略接口（策略模式）
/// </summary>
public interface IIndexExecutionStrategy
{
    /// <summary>策略名称</summary>
    string Name { get; }

    /// <summary>判断是否能处理当前请求</summary>
    bool CanHandle(IndexRequest request);

    /// <summary>执行策略</summary>
    Task<IndexExecutionResult> ExecuteAsync(
        IndexExecutionContext context,
        IndexJobStateMachine stateMachine,
        CancellationToken ct);
}
