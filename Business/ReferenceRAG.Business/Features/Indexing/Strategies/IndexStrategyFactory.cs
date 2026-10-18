using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Business.Features.Indexing.Strategies;

/// <summary>
/// 索引策略工厂：依据请求类型解析分发对应的索引执行策略
/// </summary>
public class IndexStrategyFactory
{
    private readonly IEnumerable<IIndexExecutionStrategy> _strategies;

    public IndexStrategyFactory(IEnumerable<IIndexExecutionStrategy>? strategies = null)
    {
        _strategies = strategies ?? new IIndexExecutionStrategy[]
        {
            new VectorOnlyIndexStrategy(),
            new RechunkIndexStrategy(),
            new RebuildIndexStrategy(),
            new IncrementalIndexStrategy()
        };
    }

    /// <summary>
    /// 解析适合当前请求的策略
    /// </summary>
    public IIndexExecutionStrategy GetStrategy(IndexRequest request)
    {
        foreach (var strategy in _strategies)
        {
            if (strategy.CanHandle(request))
                return strategy;
        }

        return new IncrementalIndexStrategy();
    }
}
