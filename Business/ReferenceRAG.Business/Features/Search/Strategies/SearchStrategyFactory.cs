using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Search.Strategies;

/// <summary>
/// 搜索策略工厂接口
/// </summary>
public interface ISearchStrategyFactory
{
    ISearchExecutionStrategy GetStrategy(QueryMode mode);
}

/// <summary>
/// 搜索策略工厂默认实现
/// </summary>
public class SearchStrategyFactory : ISearchStrategyFactory
{
    private readonly Dictionary<QueryMode, ISearchExecutionStrategy> _strategies;

    public SearchStrategyFactory(IHybridSearchService? hybridSearchService = null)
    {
        _strategies = new Dictionary<QueryMode, ISearchExecutionStrategy>
        {
            [QueryMode.Standard] = new StandardSearchStrategy(),
            [QueryMode.Quick] = new QuickSearchStrategy(),
            [QueryMode.Deep] = hybridSearchService != null
                ? new DeepSearchStrategy(hybridSearchService)
                : new StandardSearchStrategy(),
            [QueryMode.Hybrid] = hybridSearchService != null
                ? new HybridSearchStrategy(hybridSearchService)
                : new StandardSearchStrategy(),
            [QueryMode.HybridRerank] = hybridSearchService != null
                ? new HybridRerankSearchStrategy(hybridSearchService)
                : new StandardSearchStrategy()
        };
    }

    public ISearchExecutionStrategy GetStrategy(QueryMode mode)
    {
        if (_strategies.TryGetValue(mode, out var strategy))
        {
            return strategy;
        }

        return _strategies[QueryMode.Standard];
    }
}
