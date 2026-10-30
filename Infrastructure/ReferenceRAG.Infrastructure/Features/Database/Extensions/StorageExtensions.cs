using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.FileSystem;
using ReferenceRAG.Infrastructure.Features.Security;

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

        // QueryStats: query_stats.db（查询统计持久化属于存储层）
        services.AddSingleton<IQueryStatsService>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>().Load();
            var dbPath = Path.Combine(cfg.DataPath ?? "data", "query_stats.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            return new QueryStatsService(dbPath);
        });

        // ApiKeyStore: auth.db（API Key 树状授权与微秒并发缓存）
        services.AddSingleton<IApiKeyStore>(sp =>
        {
            var cfg = sp.GetRequiredService<ConfigManager>().Load();
            var dbPath = Path.Combine(cfg.DataPath ?? "data", "auth.db");
            var logger = sp.GetService<ILogger<ApiKeyStore>>();
            var store = new ApiKeyStore(dbPath, logger);
            store.InitializeAsync().GetAwaiter().GetResult();
            return store;
        });

        // FileReaderService: 流式行范围读取与 Markdown 大纲状态机
        services.AddSingleton<IFileReaderService>(sp =>
        {
            var vectorStore = sp.GetService<IVectorStore>();
            var logger = sp.GetService<ILogger<FileReaderService>>();
            return new FileReaderService(vectorStore, logger);
        });

        // FileSearchEngine: Agent 级文件高效关键字与正则搜索引擎
        services.AddSingleton<IFileSearchEngine>(sp =>
        {
            var configMgr = sp.GetRequiredService<ConfigManager>();
            var logger = sp.GetService<ILogger<ManagedFileSearchEngine>>();
            return new ManagedFileSearchEngine(configMgr, logger);
        });

        return services;
    }
}
