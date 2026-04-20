using System.Diagnostics;
using System.Text.Json.Serialization;

using Microsoft.OpenApi;

using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Rerank;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Hubs;
using ReferenceRAG.Service.Middleware;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Storage;

using Serilog;

using WebApiWindowsService;

// MCP Helper
using McpHelper.Extensions;
using McpHelper.Models;
using ReferenceRAG.Service.McpTools;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

#region 必备环境配置
// Set working directory to application directory (important for Windows Service)
Directory.SetCurrentDirectory(AppContext.BaseDirectory);
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;
#endregion


var builder = WebApplication.CreateBuilder(args);

#region 配置服务端口
// 从配置文件中读取端口设置
var serviceConfig = builder.Configuration.GetSection("ReferenceRAG:Service");
var host = serviceConfig["host"] ?? "localhost";
var port = serviceConfig["port"] ?? "5000";
var urls = $"http://{host}:{port}";
builder.WebHost.UseUrls(urls);
Console.WriteLine($"[配置] 服务地址: {urls}");
#endregion

#region 服务注入：配置服务和日志
// 配置日志
ServiceManager.ConfigureLogging(builder);
// 配置服务
var isService = ServiceManager.ConfigureService(args, builder);
#endregion

#region 互斥检测
//// ====================== 进程互斥检查（核心逻辑）======================
//string currentProcessName = Process.GetCurrentProcess().ProcessName;
//int maxRetryCount = 3;    // 最多重试3次
//int waitSeconds = 10;     // 每次等待10秒

//for (int retry = 1; retry <= maxRetryCount; retry++)
//{
//    // 判断是否存在多个同名进程
//    if (Process.GetProcessesByName(currentProcessName).Length <= 1)
//    {
//        Log.Information("无占用进程，程序继续启动...");
//        break;
//    }

//    // 第几次等待
//    Log.Information($"[{retry}/{maxRetryCount}] 发现已有进程运行，等待 {waitSeconds} 秒...");

//    // 最后一次还失败 → 直接退出
//    if (retry == maxRetryCount)
//    {
//        Log.Information("重试3次仍被占用，程序自动退出！");
//        return;
//    }

//    // 等待 10 秒
//    Thread.Sleep(waitSeconds * 1000);
//}
//// ================================================================== 
#endregion

#region 简单日志（Obsolete）
//// 文件日志（写入 logs/ 目录，按日期轮转）
//var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
//Directory.CreateDirectory(logDir);
//builder.Logging.AddProvider(new ReferenceRAG.Service.Services.FileLoggerProvider(logDir));
#endregion

#region 依赖注入服务管理
// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Obsidian RAG API",
        Version = "v1",
        Description = "Obsidian 笔记库向量检索 API"
    });
});

// 注册配置管理
builder.Services.AddSingleton<ConfigManager>();

// 注册模型管理器
builder.Services.AddSingleton<IModelManager>(sp =>
{
    var configManager = sp.GetRequiredService<ConfigManager>();
    var cfg = configManager.Load();
    var dataPath = cfg.DataPath ?? "data";

    // 使用顶层 ModelsRootPath（支持绝对路径和相对路径）
    string modelsPath;
    var modelsRootPath = cfg.ModelsRootPath;
    if (!string.IsNullOrEmpty(modelsRootPath) && Path.IsPathRooted(modelsRootPath))
    {
        // 绝对路径直接使用
        modelsPath = modelsRootPath;
    }
    else
    {
        // 相对路径：dataPath + modelsRootPath（或默认 "models"）
        modelsPath = Path.Combine(dataPath, modelsRootPath ?? "models");
    }

    Console.WriteLine($"[ModelManager] 使用模型路径: {modelsPath}");
    return new ModelManager(modelsPath, configManager);
});

// 注册核心服务
builder.Services.AddSingleton<ITokenizer, SimpleTokenizer>();
builder.Services.AddSingleton<ITextEnhancer, TextEnhancer>();
builder.Services.AddSingleton<IMarkdownChunker, MarkdownChunker>();
// 注册向量存储（支持多模型维度兼容）
builder.Services.AddSingleton<IVectorStore>(sp =>
{
    var config = sp.GetRequiredService<ConfigManager>();
    var cfg = config.Load();
    var dataPath = cfg.DataPath ?? "data";
    var dbPath = Path.Combine(dataPath, "vectors.db");
    return new SqliteVectorStore(dbPath);
});
// 注册 BM25 存储（与向量存储共用同一个数据库）
// 根据配置选择 fts5（推荐）或 legacy（备用）实现
builder.Services.AddSingleton<IBM25Store>(sp =>
{
    var config = sp.GetRequiredService<ConfigManager>();
    var cfg = config.Load();
    var dataPath = cfg.DataPath ?? "data";
    var dbPath = Path.Combine(dataPath, "vectors.db");

    var bm25Provider = cfg.Search?.BM25Provider?.ToLowerInvariant() ?? "fts5";

    // 记录选择的 BM25 provider
    Console.WriteLine($"[BM25 Provider] Config bm25Provider='{bm25Provider}', selecting implementation...");

    return bm25Provider switch
    {
        "fts5" => new Fts5BM25Store(dbPath),
        _ => new Fts5BM25Store(dbPath) // 默认使用 FTS5
    };
});
builder.Services.AddSingleton<IEmbeddingService>(sp =>
{
    var config = sp.GetRequiredService<ConfigManager>();
    var cfg = config.Load();
    return new EmbeddingService(new EmbeddingOptions
    {
        ModelPath = cfg.Embedding.ModelPath,
        ModelName = cfg.Embedding.ModelName,
        MaxSequenceLength = cfg.Embedding.MaxSequenceLength,
        BatchSize = cfg.Embedding.BatchSize,
        UseCuda = cfg.Embedding.UseCuda,
        CudaDeviceId = cfg.Embedding.CudaDeviceId,
        CudaLibraryPath = cfg.Embedding.CudaLibraryPath
    });
});

// 注册重排服务
builder.Services.AddSingleton<IRerankService>(sp =>
{
    var config = sp.GetRequiredService<ConfigManager>();
    var cfg = config.Load();

    // 获取重排模型路径
    var rerankConfig = cfg.Rerank;
    string modelPath = rerankConfig.ModelPath ?? string.Empty;

    if (string.IsNullOrEmpty(modelPath))
    {
        // 如果没有指定路径，从模型管理器获取
        var dataPath = cfg.DataPath ?? "data";
        var modelsPath = Path.Combine(dataPath, "models");
        modelPath = Path.Combine(modelsPath, rerankConfig.ModelName, "model.onnx");
    }

    return new OnnxRerankService(new RerankOptions
    {
        ModelPath = modelPath,
        ModelName = rerankConfig.ModelName,
        UseCuda = rerankConfig.UseCuda,
        CudaDeviceId = rerankConfig.CudaDeviceId,
        CudaLibraryPath = cfg.Embedding.CudaLibraryPath
    });
});

// 注册业务服务
builder.Services.AddSingleton<ContentHashDetector>();
builder.Services.AddSingleton<QueryOptimizer>();
builder.Services.AddSingleton<VectorAggregator>();
builder.Services.AddSingleton<ContextBuilder>();
builder.Services.AddSingleton<ObsidianLinkGenerator>();
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddSingleton<AlertService>();
// 查询统计服务 - 使用独立的 SQLite 数据库
builder.Services.AddSingleton(sp =>
{
    var configManager = sp.GetRequiredService<ConfigManager>();
    var config = configManager.Load();
    var dataPath = config.DataPath ?? "data";
    var statsDbPath = Path.Combine(dataPath, "query_stats.db");
    return new QueryStatsService(statsDbPath);
});
// FileChangeDetector 需要配置路径，使用工厂方法延迟创建
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
builder.Services.AddScoped<ISearchService>(sp =>
{
    var vectorStore = sp.GetRequiredService<IVectorStore>();
    var embeddingService = sp.GetRequiredService<IEmbeddingService>();
    var textEnhancer = sp.GetRequiredService<ITextEnhancer>();
    var configManager = sp.GetRequiredService<ConfigManager>();
    var logger = sp.GetRequiredService<ILogger<SearchService>>();
    var hybridSearchService = sp.GetRequiredService<HybridSearchService>();
    var rerankService = sp.GetRequiredService<IRerankService>();

    return new SearchService(
        vectorStore,
        embeddingService,
        textEnhancer,
        configManager,
        logger,
        hybridSearchService,
        rerankService);
});
builder.Services.AddScoped<HierarchicalSearchService>();
// 注册混合搜索服务（从 appsettings.json 读取 HybridSearch 配置）
builder.Services.AddSingleton<HybridSearchService>(sp =>
{
    var hybridSearchConfig = sp.GetRequiredService<IConfiguration>().GetSection("HybridSearch");
    var options = new HybridSearchOptions();

    if (hybridSearchConfig.Exists())
    {
        hybridSearchConfig.Bind(options);
        // 验证配置有效性
        try
        {
            options.Validate();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HybridSearch] Configuration validation failed: {ex.Message}, using defaults");
            options = new HybridSearchOptions();
        }
    }

    Console.WriteLine($"[HybridSearch] Config loaded: UseRRF={options.UseRRF}, RRFK={options.RRFK}, BM25Weight={options.BM25Weight}, EmbeddingWeight={options.EmbeddingWeight}");
    return new HybridSearchService(
        sp.GetRequiredService<IVectorStore>(),
        sp.GetRequiredService<IEmbeddingService>(),
        sp.GetRequiredService<IBM25Store>(),
        options,
        sp.GetRequiredService<ILogger<HybridSearchService>>());
});

// 注册索引服务（后台服务）
builder.Services.AddSingleton<IndexService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexService>());

// 注册文件监控和自动索引服务
builder.Services.AddSingleton<IFileMonitorService>(sp =>
{
    var configManager = sp.GetRequiredService<ConfigManager>();
    var config = configManager.Load();
    var debounceMs = config.Indexing?.DebounceMs ?? 500;
    var logger = sp.GetService<ILogger<FileMonitorService>>();
    return new FileMonitorService(debounceMs, logger);
});
builder.Services.AddHostedService<AutoIndexService>();

// 注册启动同步服务
builder.Services.AddHostedService<StartupSyncService>();

// 注册测试记录存储
builder.Services.AddSingleton<TestRecordStore>();

// 注册 SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
});

// 配置 CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment())
        {
            // 开发环境：从配置文件读取允许的 localhost 端口
            var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? new[] { "http://localhost:3000", "http://localhost:7897" };
            policy.WithOrigins(allowedOrigins)
                  .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
                  .WithHeaders("Content-Type", "Authorization", "X-API-Key")
                  .AllowCredentials();
        }
        else
        {
            // Production: restrict to configured origins and methods
            var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? new[] { "http://localhost:5000", "http://localhost:5001", "http://localhost:7897" };
            policy.WithOrigins(allowedOrigins)
                  .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
                  .WithHeaders("Content-Type", "Authorization", "X-API-Key")
                  .AllowCredentials();
        }
    });
}); 
#endregion


#region MCP Server 配置
// 获取服务配置
var serviceApiKey = builder.Configuration.GetSection("ReferenceRAG:Service:ApiKey").Get<string>();
var mcApiKeys = string.IsNullOrWhiteSpace(serviceApiKey)
    ? []
    : new List<string> { serviceApiKey };

var appMiddlewareOptions = new AppMiddlewareOptions
{
    Authentication = new AuthenticationOptions  // 添加 MCP 认证配置
    {
        Enabled = true,
        Type = AuthenticationType.ApiKey,
        ApiKey = new ApiKeyOptions
        {
            Keys = mcApiKeys,
            HeaderName = "X-Api-Key"
        }
    },
    Mcp = new MopOptions
    {
        Enabled = true,
        EnableInfo = false,
        ServerName = "ReferenceRAG-MCP",
        ServerVersion = "1.0.0",
        TransportType = MopTransportType.Sse,
        SseEndpoint = "/api/mcp",  // 添加 SSE 端点
        Backends = new List<BackendEndpoint>
        { }
    }
};

if (mcApiKeys.Count==0)
{
    appMiddlewareOptions.Authentication.Enabled = false;
}


// 注册完整的中间件套件
builder.Services.AddAppMcpHelper(appMiddlewareOptions); 

// 注册自定义 MCP Tools
builder.Services.AddMcpToolRegistry(registry =>
{
    //registry.RegisterLocalTool<TestTools>();
    registry.RegisterLocalTool<RagSearchTools>();
    registry.RegisterLocalTool<EmbeddingTools>();
    //registry.RegisterLocalTool<IndexStatusTools>();
    //registry.RegisterLocalTool<SourceManagementTools>();
});
#endregion

var app = builder.Build();


#region 静态类配置
StaticLogger.LoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
#endregion

#region 中间件管理

// CORS 必须在其他中间件之前
app.UseCors();

// MCP 中间件（必须在 CORS 之后，其他中间件之前）
app.UseAppMcpHelper();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Obsidian RAG API v1");
    });
}
else
{
    // Production: Swagger disabled by default for security
    // Can be enabled via SWAGGER_ENABLED=true environment variable
    var swaggerEnabled = builder.Configuration.GetValue<bool>("SwaggerEnabled", false);
    if (swaggerEnabled)
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Obsidian RAG API v1");
        });
    }
}

// 静态文件服务（Vue 前端）
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseApiKeyAuthentication();
app.UseAuthorization();
app.MapControllers();

// SPA fallback：非 API 请求返回 index.html
app.MapFallbackToFile("index.html");

// 映射 SignalR Hub
app.MapHub<IndexHub>("/hubs/index");

// 确保数据目录存在
var configManager = app.Services.GetRequiredService<ConfigManager>();
var config = configManager.Load();
var dataPath = config.DataPath ?? "data";
if (!Directory.Exists(dataPath))
{
    Directory.CreateDirectory(dataPath);
}

// 确保模型目录存在
var modelDir = Path.GetDirectoryName(config.Embedding.ModelPath);
if (!string.IsNullOrEmpty(modelDir) && !Directory.Exists(modelDir))
{
    Directory.CreateDirectory(modelDir);
}

// 初始化混合搜索服务的 BM25 索引
using (var scope = app.Services.CreateScope())
{
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
#endregion

#region 中间件：支持程序重启
// 使用原版（不支持重启）
app.Run();

//// 支持程序重启
//ServiceManager.AppLaunch(args, builder, app);
#endregion
