using System;
using System.Collections.Generic;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Business.Features.Search.Cognitive;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
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
        ResilienceCircuitBreaker? circuitBreaker = null,
        IKnowledgeBaseSearchService? kbSearchService = null,
        ICatalogStore? catalogStore = null,
        ICognitiveEvidenceEvaluatorFactory? cognitiveFactory = null,
        ConfigManager? configManager = null)
    {
        var cb = circuitBreaker ?? new ResilienceCircuitBreaker(3, TimeSpan.FromSeconds(30));
        ISearchExecutionStrategy lexical;

        var config = configManager?.Load();
        var cogConfig = config?.Search?.CognitiveLexical;
        var evaluator = (cogConfig != null && cogConfig.Backend != CognitiveLexicalBackend.Disabled && cognitiveFactory != null)
            ? cognitiveFactory.GetEvaluator(cogConfig)
            : null;

        if (evaluator != null)
        {
            lexical = new CognitiveEnhancedLexicalStrategy(
                kbSearchService,
                catalogStore,
                evaluator,
                cogConfig!,
                bm25StoreFactory);
        }
        else
        {
            lexical = new LexicalSearchStrategy(bm25StoreFactory);
        }

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
