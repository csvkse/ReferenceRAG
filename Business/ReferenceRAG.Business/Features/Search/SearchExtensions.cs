using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Business.Features.Search.Strategies;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;

using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Business.Features.Search.Cognitive;
using ReferenceRAG.Business.Features.Search.Adapters;
using ReferenceRAG.Infrastructure.Features.Cognitive;

namespace ReferenceRAG.Core.Extensions;

public static class SearchExtensions
{
    public static IServiceCollection AddSearch(
        this IServiceCollection services,
        HybridSearchOptions? hybridOptions = null)
    {
        services.AddSingleton<QueryOptimizer>();
        services.AddSingleton<VectorAggregator>();
        services.AddSingleton<MetricsCollector>();
        services.AddSingleton<AlertService>();

        services.AddSingleton<ReferenceRAG.Infrastructure.Features.Resilience.ResilienceCircuitBreaker>();

        services.AddSingleton<ICognitiveEvidenceEvaluatorFactory>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new CognitiveEvidenceEvaluatorFactory(
                rerankAdapterFactory: () => sp.GetService<IRerankExecutionAdapter>()
                    ?? new RerankExecutionAdapter(sp.GetService<IRerankService>(), sp.GetService<IModelTelemetryService>()),
                jevClientFactory: () =>
                {
                    var existing = sp.GetService<ReferenceRAG.Infrastructure.Features.Cognitive.ISystemOneClient>();
                    if (existing != null) return existing;
                    var cfgMgr = sp.GetService<ConfigManager>();
                    var config = cfgMgr?.Load();
                    if (config?.SystemOne != null && !string.IsNullOrWhiteSpace(config.SystemOne.ApiKey))
                    {
                        var proxy = ReferenceRAG.Infrastructure.Features.Network.DynamicWebProxyAdapter.ResolveProxy(
                            config,
                            config.SystemOne.ProxyPolicy,
                            config.SystemOne.CustomProxyUrl);
                        return new ReferenceRAG.Infrastructure.Features.Cognitive.JevSystemOneClient(
                            apiKey: config.SystemOne.ApiKey,
                            baseUrl: config.SystemOne.BaseUrl,
                            model: config.SystemOne.Model,
                            timeoutSeconds: config.SystemOne.TimeoutMs > 0 ? (int)Math.Ceiling(config.SystemOne.TimeoutMs / 1000.0) : 15,
                            proxy: proxy);
                    }
                    return null;
                },
                loggerFactory: loggerFactory);
        });

        var options = hybridOptions ?? new HybridSearchOptions();
        services.AddSingleton<IHybridSearchService>(sp =>
        {
            Console.WriteLine($"[HybridSearch] Config loaded: UseRRF={options.UseRRF}, RRFK={options.RRFK}, BM25Weight={options.BM25Weight}, EmbeddingWeight={options.EmbeddingWeight}");
            return new HybridSearchService(
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingService>(),
                sp.GetRequiredService<IBM25Store>(),
                options,
                sp.GetRequiredService<ILogger<HybridSearchService>>(),
                synonymService: new SynonymService());
        });

        services.AddSingleton<ISearchStrategyFactory>(sp =>
            new SearchStrategyFactory(
                sp.GetService<IHybridSearchService>(),
                () => sp.GetRequiredService<IBM25Store>(),
                sp.GetService<ReferenceRAG.Infrastructure.Features.Resilience.ResilienceCircuitBreaker>(),
                sp.GetService<IKnowledgeBaseSearchService>(),
                sp.GetService<ICatalogStore>(),
                sp.GetService<ICognitiveEvidenceEvaluatorFactory>(),
                sp.GetService<ConfigManager>()));

        services.AddScoped<ISearchService>(sp => new SearchService(
            sp.GetRequiredService<IVectorStore>(),
            new Lazy<IEmbeddingService>(() => sp.GetRequiredService<IEmbeddingService>()),
            sp.GetRequiredService<ITextEnhancer>(),
            sp.GetRequiredService<ConfigManager>(),
            sp.GetRequiredService<ILogger<SearchService>>(),
            new Lazy<IHybridSearchService?>(() => sp.GetRequiredService<IHybridSearchService>()),
            new Lazy<IRerankService?>(() => sp.GetRequiredService<IRerankService>()),
            sp.GetService<IGraphStore>(),
            new Lazy<IModelTelemetryService?>(() => sp.GetService<IModelTelemetryService>()),
            () => sp.GetRequiredService<ReferenceRAG.Storage.LexicalIndexCoordinator>(),
            () => ReferenceRAG.Storage.LexicalIndexCoordinator.HasPersistedStaleVectors(
                Path.Combine(sp.GetRequiredService<ConfigManager>().Load().DataPath ?? "data", "vectors.db")),
            () => sp.GetRequiredService<IBM25Store>(),
            sp.GetService<IKnowledgeBaseSearchService>(),
            sp.GetService<ICatalogStore>(),
            sp.GetService<ICognitiveEvidenceEvaluatorFactory>()));

        return services;
    }
}
