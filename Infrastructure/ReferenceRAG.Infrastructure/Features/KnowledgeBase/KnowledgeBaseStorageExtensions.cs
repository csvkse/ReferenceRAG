using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Profiles;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Runtime;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Core.Features.KnowledgeBase;

public static class KnowledgeBaseStorageExtensions
{
    public static IServiceCollection AddKnowledgeBaseStorage(
        this IServiceCollection services,
        string? customDataRoot = null)
    {
        services.AddSingleton<ICatalogStore>(sp =>
        {
            string dataRoot;
            if (!string.IsNullOrEmpty(customDataRoot))
            {
                dataRoot = customDataRoot;
            }
            else
            {
                var cfg = sp.GetService<ConfigManager>()?.Load();
                dataRoot = cfg?.DataPath ?? "data";
            }

            var catalogDb = Path.Combine(dataRoot, "system", "catalog.db");
            var logger = sp.GetService<ILogger<CatalogStore>>();
            var store = new CatalogStore(catalogDb, logger);
            store.InitializeAsync().GetAwaiter().GetResult();
            return store;
        });

        services.AddSingleton<IKnowledgeBaseStorageManager>(sp =>
        {
            string dataRoot;
            if (!string.IsNullOrEmpty(customDataRoot))
            {
                dataRoot = customDataRoot;
            }
            else
            {
                var cfg = sp.GetService<ConfigManager>()?.Load();
                dataRoot = cfg?.DataPath ?? "data";
            }

            var logger = sp.GetService<ILogger<KnowledgeBaseStorageManager>>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new KnowledgeBaseStorageManager(dataRoot, logger, loggerFactory);
        });

        services.AddSingleton<IResourceScheduler>(sp =>
        {
            var logger = sp.GetService<ILogger<ResourceScheduler>>();
            return new ResourceScheduler(logger: logger);
        });

        services.AddSingleton<IModelRouter>(sp =>
        {
            var scheduler = sp.GetRequiredService<IResourceScheduler>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var logger = sp.GetService<ILogger<ModelRouter>>();
            return new ModelRouter(scheduler, loggerFactory, logger);
        });

        services.AddSingleton<ISearchFusionStrategy, RrfSearchFusionStrategy>();
        services.AddSingleton<IContentProfileRegistry, ContentProfileRegistry>();

        return services;
    }
}
