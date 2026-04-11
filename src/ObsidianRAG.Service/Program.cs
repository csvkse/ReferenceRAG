using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Services;
using ObsidianRAG.Core.Services.Rerank;
using ObsidianRAG.Storage;
using ObsidianRAG.Service.Hubs;
using ObsidianRAG.Service.Services;
using ObsidianRAG.Service.Controllers;
using ObsidianRAG.Service.Middleware;
using Microsoft.OpenApi;
using System.Text.Json.Serialization;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding= System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

// 文件日志（写入 logs/ 目录，按日期轮转）
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);
builder.Logging.AddProvider(new ObsidianRAG.Service.Services.FileLoggerProvider(logDir));

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
    var modelsPath = Path.Combine(dataPath, "models");
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
        "legacy" => new SqliteBM25Store(dbPath),
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
    return new FileChangeDetector(firstSource?.Path ?? Directory.GetCurrentDirectory());
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
builder.Services.AddSingleton<HybridSearchService>();

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
            // 开发环境：仅允许 localhost 来源
            policy.WithOrigins("http://localhost:5000", "http://localhost:5001", "http://localhost:3000")
                  .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH")
                  .WithHeaders("Content-Type", "Authorization", "X-API-Key")
                  .AllowCredentials();
        }
        else
        {
            // Production: restrict to configured origins and methods
            var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? new[] { "http://localhost:5000", "http://localhost:5001" };
            policy.WithOrigins(allowedOrigins)
                  .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH")
                  .WithHeaders("Content-Type", "Authorization", "X-API-Key")
                  .AllowCredentials();
        }
    });
});

var app = builder.Build();

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

app.UseCors();
app.UseApiKeyAuthentication();
app.UseAuthorization();
app.MapControllers();

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

app.Run();
