using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Storage.Extensions;

public static class StorageExtensions
{
    public static IServiceCollection AddRagStorage(this IServiceCollection services)
    {
        // VectorStore: vectors.db（含 files / chunks / vec_* 表）
        services.AddSingleton<SharedSqliteConnection>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>().Load();
            var dbPath = Path.Combine(cfg.DataPath ?? "data", "vectors.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            return new SharedSqliteConnection(dbPath);
        });

        services.AddSingleton<IVectorStore>(sp =>
            new SqliteVectorStore(sp.GetRequiredService<SharedSqliteConnection>()));

        // BM25Store: bm25.db（独立连接+锁，重建时不阻塞向量检索）
        services.AddSingleton<IBM25Store>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>().Load();
            var dbPath = Path.Combine(cfg.DataPath ?? "data", "bm25.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            return new Fts5BM25Store(dbPath);
        });

        // GraphStore: graph.db（独立连接+锁，图谱重建时不阻塞向量检索）
        services.AddSingleton<IGraphStore>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>().Load();
            var dbPath = Path.Combine(cfg.DataPath ?? "data", "graph.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            return new SqliteGraphStore(dbPath);
        });

        return services;
    }
}
