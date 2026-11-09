using System;
using System.Collections.Generic;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.Resilience;

namespace ReferenceRAG.Business.Features.Search.Strategies;

/// <summary>
/// 搜索策略工厂接口
/// </summary>
public interface ISearchStrategyFactory
{
    ISearchExecutionStrategy GetStrategy(QueryMode mode);
}

/// <summary>
/// 搜索策略工厂默认实现 (原生集成词法策略与容灾降级装饰器)
/// </summary>
public class SearchStrategyFactory : ISearchStrategyFactory
{
    private readonly Dictionary<QueryMode, ISearchExecutionStrategy> _strategies;

    public SearchStrategyFactory(
        IHybridSearchService? hybridSearchService = null,
        Func<IBM25Store>? bm25StoreFactory = null,
        ResilienceCircuitBreaker? circuitBreaker = null)
    {
        var cb = circuitBreaker ?? new ResilienceCircuitBreaker(3, TimeSpan.FromSeconds(30));
        var lexical = new LexicalSearchStrategy(bm25StoreFactory);

        ISearchExecutionStrategy Wrap(ISearchExecutionStrategy primary) =>
            new FallbackResilientSearchStrategy(primary, lexical, cb);

        _strategies = new Dictionary<QueryMode, ISearchExecutionStrategy>
        {
            [QueryMode.Lexical] = lexical,
            [QueryMode.Standard] = Wrap(new StandardSearchStrategy()),
            [QueryMode.Quick] = Wrap(new QuickSearchStrategy()),
            [QueryMode.Deep] = Wrap(hybridSearchService != null
                ? new DeepSearchStrategy(hybridSearchService)
                : new StandardSearchStrategy()),
            [QueryMode.Hybrid] = Wrap(hybridSearchService != null
                ? new HybridSearchStrategy(hybridSearchService)
                : new StandardSearchStrategy()),
            [QueryMode.HybridRerank] = Wrap(hybridSearchService != null
                ? new HybridRerankSearchStrategy(hybridSearchService)
                : new StandardSearchStrategy())
        };
    }

    public ISearchExecutionStrategy GetStrategy(QueryMode mode)
    {
        if (_strategies.TryGetValue(mode, out var strategy))
        {
            return strategy;
        }

        return _strategies[QueryMode.HybridRerank];
    }
}
