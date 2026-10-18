using ReferenceRAG.Business.Features.Indexing.StateMachine;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Business.Features.Indexing.Strategies;

/// <summary>
/// 强制全量重建策略（Rebuild / Force）：
/// 无论 contentHash 与 chunkingHash 是否发生变动，均强制重读、强制重新分块与全量更新。
/// </summary>
public class RebuildIndexStrategy : IIndexExecutionStrategy
{
    private readonly IncrementalIndexStrategy _innerStrategy;

    public RebuildIndexStrategy()
    {
        _innerStrategy = new IncrementalIndexStrategy();
    }

    public string Name => "Rebuild";

    public bool CanHandle(IndexRequest request) => request.Force && !request.VectorOnly && !request.Rechunk;

    public async Task<IndexExecutionResult> ExecuteAsync(
        IndexExecutionContext context,
        IndexJobStateMachine stateMachine,
        CancellationToken ct)
    {
        // 保证强制标记生效
        context.Request.Force = true;
        return await _innerStrategy.ExecuteAsync(context, stateMachine, ct);
    }
}
