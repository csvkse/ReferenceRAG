using System.IO;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Rerank;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Hubs;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Desktop;

/// <summary>
/// 构建 WebApplication 实例，复制 ReferenceRAG.Service/Program.cs 的全部 DI 注册。
/// 调用方负责在后台线程调用 app.RunAsync(cts.Token)。
/// </summary>
public static class HostBootstrapper
{
    /// <summary>
    /// 构建并配置 WebApplication，绑定到指定的本地端口。
    /// </summary>
    /// <param name="port">由 PortHelper.GetFreeTcpPort() 分配的空闲端口。</param>
    /// <returns>已配置、未启动的 WebApplication。</returns>
    public static WebApplication Build(int port)
    {
        // 与 Service/Program.cs 保持一致：确保工作目录为应用目录
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        var builder = WebApplication.CreateBuilder();

        // Desktop 使用动态端口，不读取 appsettings.json 中的端口配置
        builder.WebHost.UseUrls($"http://localhost:{port}");

        // =====================================================================
        // 依赖注入服务注册（与 Service/Program.cs 保持同步）
        // =====================================================================

        builder.Services.AddControllers()
            .AddApplicationPart(typeof(ReferenceRAG.Service.Controllers.AIQueryController).Assembly)
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = null;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        builder.Services.AddEndpointsApiExplorer();
        // Desktop 模式下禁用 Swagger（本地只读访问，无需 API 文档）

        // 配置管理
        builder.Services.AddSingleton<ConfigManager>();

        // 模型管理器
        builder.Services.AddSingleton<IModelManager>(sp =>
        {
            var configManager = sp.GetRequiredService<ConfigManager>();
            var cfg = configManager.Load();
            var dataPath = cfg.DataPath ?? "data";

            string modelsPath;
            var modelsRootPath = cfg.ModelsRootPath;
            if (!string.IsNullOrEmpty(modelsRootPath) && Path.IsPathRooted(modelsRootPath))
            {
                modelsPath = modelsRootPath;
            }
            else
            {
                modelsPath = Path.Combine(dataPath, modelsRootPath ?? "models");
            }

            Console.WriteLine($"[Desktop/ModelManager] 使用模型路径: {modelsPath}");
            return new ModelManager(modelsPath, configManager);
        });

        // 核心服务
        builder.Services.AddSingleton<ITokenizer, SimpleTokenizer>();
        builder.Services.AddSingleton<ITextEnhancer, TextEnhancer>();
        builder.Services.AddSingleton<IMarkdownChunker, MarkdownChunker>();

        // 向量存储
        builder.Services.AddSingleton<IVectorStore>(sp =>
        {
            var config = sp.GetRequiredService<ConfigManager>();
            var cfg = config.Load();
            var dataPath = cfg.DataPath ?? "data";
            var dbPath = Path.Combine(dataPath, "vectors.db");
            return new SqliteVectorStore(dbPath);
        });

        // BM25 存储
        builder.Services.AddSingleton<IBM25Store>(sp =>
        {
            var config = sp.GetRequiredService<ConfigManager>();
            var cfg = config.Load();
            var dataPath = cfg.DataPath ?? "data";
            var dbPath = Path.Combine(dataPath, "vectors.db");

            var bm25Provider = cfg.Search?.BM25Provider?.ToLowerInvariant() ?? "fts5";
            Console.WriteLine($"[Desktop/BM25 Provider] Config bm25Provider='{bm25Provider}'");

            return bm25Provider switch
            {
                "fts5" => new Fts5BM25Store(dbPath),
                _      => new Fts5BM25Store(dbPath)
            };
        });

        // 向量化服务
        builder.Services.AddSingleton<IEmbeddingService>(sp =>
        {
            var config = sp.GetRequiredService<ConfigManager>();
            var cfg = config.Load();
            return new EmbeddingService(new EmbeddingOptions
            {
                ModelPath          = cfg.Embedding.ModelPath,
                ModelName          = cfg.Embedding.ModelName,
                MaxSequenceLength  = cfg.Embedding.MaxSequenceLength,
                BatchSize          = cfg.Embedding.BatchSize,
                UseCuda            = cfg.Embedding.UseCuda,
                CudaDeviceId       = cfg.Embedding.CudaDeviceId,
                CudaLibraryPath    = cfg.Embedding.CudaLibraryPath
            });
        });

        // 重排服务
        builder.Services.AddSingleton<IRerankService>(sp =>
        {
            var config = sp.GetRequiredService<ConfigManager>();
            var cfg = config.Load();
            var rerankConfig = cfg.Rerank;
            string modelPath = rerankConfig.ModelPath ?? string.Empty;

            if (string.IsNullOrEmpty(modelPath))
            {
                var dataPath = cfg.DataPath ?? "data";
                var modelsPath = Path.Combine(dataPath, "models");
                modelPath = Path.Combine(modelsPath, rerankConfig.ModelName, "model.onnx");
            }

            return new OnnxRerankService(new RerankOptions
            {
                ModelPath       = modelPath,
                ModelName       = rerankConfig.ModelName,
                UseCuda         = rerankConfig.UseCuda,
                CudaDeviceId    = rerankConfig.CudaDeviceId,
                CudaLibraryPath = cfg.Embedding.CudaLibraryPath
            });
        });

        // 业务服务
        builder.Services.AddSingleton<ContentHashDetector>();
        builder.Services.AddSingleton<QueryOptimizer>();
        builder.Services.AddSingleton<VectorAggregator>();
        builder.Services.AddSingleton<ContextBuilder>();
        builder.Services.AddSingleton<ObsidianLinkGenerator>();
        builder.Services.AddSingleton<MetricsCollector>();
        builder.Services.AddSingleton<AlertService>();

        // 查询统计
        builder.Services.AddSingleton(sp =>
        {
            var configManager = sp.GetRequiredService<ConfigManager>();
            var config = configManager.Load();
            var dataPath = config.DataPath ?? "data";
            var statsDbPath = Path.Combine(dataPath, "query_stats.db");
            return new QueryStatsService(statsDbPath);
        });

        // 文件变更检测
        builder.Services.AddSingleton<IFileChangeDetector>(sp =>
        {
            var configManager = sp.GetRequiredService<ConfigManager>();
            var config = configManager.Load();
            var firstSource = config.Sources.FirstOrDefault();
            return new FileChangeDetector(
                firstSource?.Path ?? Directory.GetCurrentDirectory(),
                config.Indexing?.DebounceMs ?? 500,
                firstSource?.FilePatterns);
        });

        // 搜索服务（Scoped）
        builder.Services.AddScoped<ISearchService>(sp =>
        {
            var vectorStore      = sp.GetRequiredService<IVectorStore>();
            var embeddingService = sp.GetRequiredService<IEmbeddingService>();
            var textEnhancer     = sp.GetRequiredService<ITextEnhancer>();
            var configManager    = sp.GetRequiredService<ConfigManager>();
            var logger           = sp.GetRequiredService<ILogger<SearchService>>();
            var hybridService    = sp.GetRequiredService<HybridSearchService>();
            var rerankService    = sp.GetRequiredService<IRerankService>();
            var graphStore       = sp.GetService<ReferenceRAG.Core.Interfaces.IGraphStore>();

            return new SearchService(
                vectorStore, embeddingService, textEnhancer,
                configManager, logger, hybridService, rerankService, graphStore);
        });

        builder.Services.AddScoped<HierarchicalSearchService>();

        // 混合搜索服务
        builder.Services.AddSingleton<HybridSearchService>(sp =>
        {
            var hybridSearchConfig = sp.GetRequiredService<IConfiguration>().GetSection("HybridSearch");
            var options = new HybridSearchOptions();

            if (hybridSearchConfig.Exists())
            {
                hybridSearchConfig.Bind(options);
                try { options.Validate(); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Desktop/HybridSearch] Config validation failed: {ex.Message}, using defaults");
                    options = new HybridSearchOptions();
                }
            }

            Console.WriteLine($"[Desktop/HybridSearch] UseRRF={options.UseRRF}, BM25Weight={options.BM25Weight}");
            return new HybridSearchService(
                sp.GetRequiredService<IVectorStore>(),
                sp.GetRequiredService<IEmbeddingService>(),
                sp.GetRequiredService<IBM25Store>(),
                options,
                sp.GetRequiredService<ILogger<HybridSearchService>>());
        });

        // 索引服务（后台服务）
        builder.Services.AddSingleton<IndexService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexService>());

        // 文件监控与自动索引
        builder.Services.AddSingleton<IFileMonitorService>(sp =>
        {
            var configManager = sp.GetRequiredService<ConfigManager>();
            var config = configManager.Load();
            var debounceMs = config.Indexing?.DebounceMs ?? 500;
            var logger = sp.GetService<ILogger<FileMonitorService>>();
            return new FileMonitorService(debounceMs, logger);
        });
        builder.Services.AddHostedService<AutoIndexService>();

        // 启动同步服务
        builder.Services.AddHostedService<StartupSyncService>();

        // 测试记录存储
        builder.Services.AddSingleton<TestRecordStore>();

        // 知识图谱存储（与向量存储共用同一 DB）
        builder.Services.AddSingleton<ReferenceRAG.Core.Interfaces.IGraphStore>(sp =>
        {
            var config = sp.GetRequiredService<ConfigManager>();
            var cfg = config.Load();
            var dataPath = cfg.DataPath ?? "data";
            var dbPath = Path.Combine(dataPath, "vectors.db");
            return new ReferenceRAG.Storage.SqliteGraphStore(dbPath);
        });
        builder.Services.AddSingleton<ReferenceRAG.Core.Services.Graph.WikiLinkExtractor>();
        builder.Services.AddSingleton<ReferenceRAG.Core.Services.Graph.GraphIndexingService>();

        // SignalR
        builder.Services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        });

        // CORS：Desktop 模式下 WebView2 与 Kestrel 同源
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.WithOrigins($"http://localhost:{port}")
                      .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
                      .WithHeaders("Content-Type", "Authorization", "X-API-Key")
                      .AllowCredentials();
            });
        });

        // =====================================================================
        // 构建应用
        // =====================================================================
        var app = builder.Build();

        // =====================================================================
        // 中间件配置
        // =====================================================================

        StaticLogger.LoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        app.UseCors();

        // Desktop 模式不启用 Swagger、MCP 中间件、API Key 认证

        // 静态文件服务（Vue SPA）
        // index.html 禁止缓存（每次都从磁盘读），哈希资源永久缓存
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var path = ctx.File.Name;
                if (path.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                    ctx.Context.Response.Headers["Pragma"] = "no-cache";
                    ctx.Context.Response.Headers["Expires"] = "0";
                }
                else
                {
                    // Vite 输出带 hash 的文件名，内容不变则永久缓存
                    ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                }
            }
        });

        app.UseAuthorization();
        app.MapControllers();

        // SPA fallback
        app.MapFallbackToFile("index.html");

        // SignalR Hub
        app.MapHub<ReferenceRAG.Service.Hubs.IndexHub>("/hubs/index");

        // 确保数据目录存在
        var configManager = app.Services.GetRequiredService<ConfigManager>();
        var config = configManager.Load();
        var dataPath = config.DataPath ?? "data";
        if (!Directory.Exists(dataPath))
            Directory.CreateDirectory(dataPath);

        var modelDir = Path.GetDirectoryName(config.Embedding.ModelPath);
        if (!string.IsNullOrEmpty(modelDir) && !Directory.Exists(modelDir))
            Directory.CreateDirectory(modelDir);

        return app;
    }

    /// <summary>
    /// 在应用构建完成后初始化 BM25 索引（需在 Kestrel 启动前调用）。
    /// </summary>
    public static async Task InitializeSearchAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
        try
        {
            await searchService.InitializeAsync();
            app.Logger.LogInformation("[Desktop] 搜索服务 BM25 索引初始化完成");
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "[Desktop] BM25 索引初始化失败，混合搜索可能退化为纯向量搜索");
        }
    }
}