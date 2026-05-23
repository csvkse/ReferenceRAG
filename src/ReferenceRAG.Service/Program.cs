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

// 核心领域服务（与 Desktop/HostBootstrapper 共用）
builder.Services.AddRagCoreServices(
    builder.Configuration,
    signalRDetailedErrors: builder.Environment.IsDevelopment());



// 配置 CORS
builder.Services.AddHttpClient();
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

var app = builder.Build();

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

// 共用端点注册、目录初始化（与 Desktop/HostBootstrapper 共用）
app.UseRagEndpoints();
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
