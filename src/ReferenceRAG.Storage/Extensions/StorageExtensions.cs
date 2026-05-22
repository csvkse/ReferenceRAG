using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Storage.Extensions;

public static class StorageExtensions
{
    public static IServiceCollection AddRagStorage(this IServiceCollection services)
    {
        services.AddSingleton<SharedSqliteConnection>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>().Load();
            var dbPath = Path.Combine(cfg.DataPath ?? "data", "vectors.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            return new SharedSqliteConnection(dbPath);
        });

        services.AddSingleton<IVectorStore>(sp =>
            new SqliteVectorStore(sp.GetRequiredService<SharedSqliteConnection>()));

        services.AddSingleton<IGraphStore>(sp =>
            new SqliteGraphStore(sp.GetRequiredService<SharedSqliteConnection>()));

        services.AddSingleton<IBM25Store>(sp =>
            new Fts5BM25Store(sp.GetRequiredService<SharedSqliteConnection>()));

        return services;
    }
}
