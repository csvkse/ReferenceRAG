using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;

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

        services.AddSingleton<IQueryStatsService>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>().Load();
            var statsDbPath = Path.Combine(cfg.DataPath ?? "data", "query_stats.db");
            return new QueryStatsService(statsDbPath);
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

        services.AddScoped<ISearchService>(sp => new SearchService(
            sp.GetRequiredService<IVectorStore>(),
            sp.GetRequiredService<IEmbeddingService>(),
            sp.GetRequiredService<ITextEnhancer>(),
            sp.GetRequiredService<ConfigManager>(),
            sp.GetRequiredService<ILogger<SearchService>>(),
            sp.GetRequiredService<IHybridSearchService>(),
            sp.GetRequiredService<IRerankService>(),
            sp.GetService<IGraphStore>()));

        return services;
    }
}
