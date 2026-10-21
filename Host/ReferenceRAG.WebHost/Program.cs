using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;
using ReferenceRAG.Service.Extensions;
using ReferenceRAG.Service.Middleware;
using Serilog;
using WebApiWindowsService;
using McpHelper.Extensions;
using McpHelper.Models;
using ReferenceRAG.Service.McpTools;
using ReferenceRAG.Service.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using ReferenceRAG.ApiHost.Configuration;
using ReferenceRAG.ApiHost.Transport;

#region 必备环境配置
// Set working directory to application directory (important for Windows Service)
Directory.SetCurrentDirectory(Environment.GetEnvironmentVariable("REFERENCERAG_CONTENT_ROOT") ?? AppContext.BaseDirectory);
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;
ReferenceRAG.Core.Features.HardwareAcceleration.OnnxExecutionProviderHelper.InitializeNativeResolver();
var bootstrapReport = ReferenceRAG.Infrastructure.Features.Storage.Bootstrap.ConfigBootstrap.EnsureInitialized();
if (bootstrapReport.FinalState == ReferenceRAG.Infrastructure.Features.Storage.Bootstrap.ConfigLifecycleState.Failed)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Error.WriteLine($"[警告] 配置文件初始化未就绪: {bootstrapReport.ErrorMessage}");
    Console.ResetColor();
}
#endregion


var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = Directory.GetCurrentDirectory(), WebRootPath = Path.Combine(AppContext.BaseDirectory,"wwwroot") });

var activeConfigPath = ReferenceRAG.Infrastructure.Features.Storage.Bootstrap.ConfigBootstrap.ActiveConfigPath;
builder.Configuration.AddJsonFile(activeConfigPath, optional: true, reloadOnChange: true);

#region 配置服务端口
var bindingResult = NetworkBindingResolver.Resolve(builder.Configuration);
builder.WebHost.UseUrls(bindingResult.EffectiveUrls);
builder.Services.AddInProcessServer(listen: true);
Console.WriteLine($"[配置] 服务地址: {bindingResult.EffectiveUrls} (AllowNetworkAccess={bindingResult.AllowNetworkAccess})");
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

#region 依赖注入服务管理
// Add services to the container
var serviceConfig = builder.Configuration.GetSection("ReferenceRAG:Service");
builder.Services.AddControllers()
    .AddApplicationPart(typeof(ReferenceRAG.Service.Controllers.AIQueryController).Assembly)
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

// 核心领域服务（与 Desktop/HostBootstrapper 共用）
builder.Services.AddRagCoreServices(
    builder.Configuration,
    signalRDetailedErrors: builder.Environment.IsDevelopment());



// 配置 CORS
// 是否启用由设置页的「启用 CORS」控制（默认启用）。
var enableCors = serviceConfig.GetValue("enableCors", true);
if (enableCors)
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            // 允许来源按环境给默认值，可由 Cors:AllowedOrigins 覆盖
            var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? (builder.Environment.IsDevelopment()
                    ? new[] { "http://localhost:3000", "http://localhost:7897" }
                    : new[] { "http://localhost:5000", "http://localhost:5001", "http://localhost:7897" });
            policy.WithOrigins(allowedOrigins)
                  .WithMethods("GET", "POST", "PUT", "DELETE", "PATCH", "OPTIONS")
                  .WithHeaders("Content-Type", "Authorization", "X-API-Key")
                  .AllowCredentials();
        });
    });
}
#endregion

var app = builder.Build();

#region 中间件管理

// CORS 必须在其他中间件之前
if (enableCors) app.UseCors();

// 全局异常处理：未处理异常统一转成 JSON 错误体，避免空白 500。
// 需在 MCP / Swagger / 静态文件 / 鉴权之前，才能覆盖它们的异常。
app.UseGlobalExceptionHandler();

// MCP 中间件（必须在 CORS 之后，其他中间件之前）
app.UseAppMcpHelper();

// Swagger 由设置页的「启用 Swagger」控制（ReferenceRAG:Service:enableSwagger），
// 兼容既有的顶层 SwaggerEnabled 与 SWAGGER_ENABLED 环境变量。
var swaggerEnabled = serviceConfig.GetValue<bool?>("enableSwagger")
    ?? builder.Configuration.GetValue<bool>("SwaggerEnabled", false);
if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Obsidian RAG API v1");
    });
}

// 静态文件服务（Vue 前端）
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache" });

app.UseApiKeyAuthentication();

// 共用端点注册、目录初始化（与 Desktop/HostBootstrapper 共用）
try
{
    app.UseRagEndpoints();
}
catch (UnauthorizedAccessException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"\n[FATAL] ReferenceRAG 启动失败（目录权限不足）: {ex.Message}");
    Console.Error.WriteLine("\n[Docker 权限排查指引]");
    Console.Error.WriteLine("  1. 请检查宿主机数据挂载目录权限是否可写。");
    Console.Error.WriteLine("  2. 在宿主机执行权限修正: chown -R 1000:1000 <宿主机目录> && chmod -R 775 <宿主机目录>");
    Console.Error.WriteLine("  3. 或在 docker run 中添加环境变量: -e PUID=0 -e PGID=0 (以 root 运行)");
    Console.ResetColor();
    throw;
}
#endregion

#region 初始化搜索索引
await app.InitializeSearchAsync();
#endregion

#region 中间件：支持程序重启
// 使用原版（不支持重启）
app.Run();

//// 支持程序重启
//ServiceManager.AppLaunch(args, builder, app);
#endregion
