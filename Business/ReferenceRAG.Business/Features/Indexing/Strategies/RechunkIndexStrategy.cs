using ReferenceRAG.Business.Features.Indexing.StateMachine;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Business.Features.Indexing.Strategies;

/// <summary>
/// 强制重建切片策略（Rechunk）：
/// 针对指定数据源或全局，使用当前最新分块设置重新对所有文件执行切分，
/// 采用文件级流水线原子替换旧切片，并重新生成对应的向量与检索索引（无需提前清空数据源）。
/// </summary>
public class RechunkIndexStrategy : IIndexExecutionStrategy
{
    private readonly IncrementalIndexStrategy _innerStrategy;

    public RechunkIndexStrategy()
    {
        _innerStrategy = new IncrementalIndexStrategy();
    }

    public string Name => "Rechunk";

    public bool CanHandle(IndexRequest request) => request.Rechunk && !request.VectorOnly;

    public async Task<IndexExecutionResult> ExecuteAsync(
        IndexExecutionContext context,
        IndexJobStateMachine stateMachine,
        CancellationToken ct)
    {
        // 强制标记生效，确保即使文件 ContentHash 未变化也重新执行切片
        context.Request.Force = true;
        return await _innerStrategy.ExecuteAsync(context, stateMachine, ct);
    }
}
