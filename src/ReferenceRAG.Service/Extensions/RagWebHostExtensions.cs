using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Extensions;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Hubs;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Storage.Extensions;

namespace ReferenceRAG.Service.Extensions;

/// <summary>
/// Program.cs 与 Desktop/HostBootstrapper 共用的 RAG 宿主扩展。
/// </summary>
public static class RagWebHostExtensions
{
    /// <summary>
    /// 注册 RAG 核心领域服务、后台服务、SignalR。
    /// ConfigManager 必须第一个注册，其余扩展依赖它。
    /// </summary>
    public static IServiceCollection AddRagCoreServices(
        this IServiceCollection services,
        IConfiguration configuration,
        bool signalRDetailedErrors = false)
    {
        services.AddSingleton<ConfigManager>();

        var hybridOptions = new HybridSearchOptions();
        var hybridSection = configuration.GetSection("HybridSearch");
        if (hybridSection.Exists())
        {
            hybridSection.Bind(hybridOptions);
            try { hybridOptions.Validate(); }
            catch (Exception ex)
            {
                Console.WriteLine($"[HybridSearch] Config validation failed: {ex.Message}, using defaults");
                hybridOptions = new HybridSearchOptions();
            }
        }

        services
            .AddRagStorage()
            .AddChunking()
            .AddModelManagement()
            .AddFileMonitor()
            .AddSearch(hybridOptions)
            .AddIndexingPipeline();

        services.AddSingleton<IndexService>();
        services.AddHostedService(sp => sp.GetRequiredService<IndexService>());
        services.AddHostedService<AutoIndexService>();
        services.AddHostedService<StartupSyncService>();
        services.AddSingleton<TestRecordStore>();
        services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = signalRDetailedErrors;
            options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        });

        return services;
    }

    /// <summary>
    /// 注册共用中间件和端点：授权、控制器、SPA fallback、SignalR Hub、数据目录创建。
    /// 调用方须在此之前完成 UseCors()、静态文件等宿主特定中间件。
    /// </summary>
    public static WebApplication UseRagEndpoints(this WebApplication app)
    {
        StaticLogger.LoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

        app.UseAuthorization();
        app.MapControllers();
        app.MapFallbackToFile("index.html");
        app.MapHub<IndexHub>("/hubs/index");

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
    /// 初始化 BM25 索引，须在 Kestrel 启动前调用。
    /// </summary>
    public static async Task InitializeSearchAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
        try
        {
            await searchService.InitializeAsync();
            app.Logger.LogInformation("搜索服务 BM25 索引初始化完成");
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "BM25 索引初始化失败，混合搜索可能退化为纯向量搜索");
        }
    }
}
