using System.Diagnostics;
using System.Text.Json.Serialization;

using Microsoft.OpenApi;

using ReferenceRAG.Core.Extensions;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Rerank;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Hubs;
using ReferenceRAG.Service.Middleware;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Storage;
using ReferenceRAG.Storage.Extensions;

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
var allowNetwork = bool.TryParse(serviceConfig["allowNetworkAccess"], out var _allowNet) && _allowNet;
var host = allowNetwork ? "0.0.0.0" : "localhost";
var port = serviceConfig["port"] ?? "5000";
var urls = $"http://{host}:{port}";
builder.WebHost.UseUrls(urls);
Console.WriteLine($"[配置] 服务地址: {urls} (AllowNetworkAccess={allowNetwork})");
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

// 注册配置管理（其他扩展依赖它，必须第一个注册）
builder.Services.AddSingleton<ConfigManager>();

// 读取 HybridSearch 配置（Core 库不依赖 IConfiguration，在此预读后传入）
var hybridOptions = new HybridSearchOptions();
var hybridSection = builder.Configuration.GetSection("HybridSearch");
if (hybridSection.Exists())
{
    hybridSection.Bind(hybridOptions);
    try { hybridOptions.Validate(); }
    catch (Exception ex)
    {
        Console.WriteLine($"[HybridSearch] Configuration validation failed: {ex.Message}, using defaults");
        hybridOptions = new HybridSearchOptions();
    }
}

// 领域服务注册
builder.Services
    .AddFileMonitor()
    .AddChunking()
    .AddModelManagement()
    .AddRagStorage()
    .AddSearch(hybridOptions)
    .AddIndexingPipeline();

// 后台服务
builder.Services.AddSingleton<IndexService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndexService>());
builder.Services.AddHostedService<AutoIndexService>();
builder.Services.AddHostedService<StartupSyncService>();

// 测试记录存储（Service 层，非领域服务）
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
