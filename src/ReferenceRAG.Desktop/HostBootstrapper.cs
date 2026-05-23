using System.IO;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using McpHelper.Extensions;
using McpHelper.Models;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Extensions;
using ReferenceRAG.Service.Middleware;

namespace ReferenceRAG.Desktop;

/// <summary>
/// 构建 WebApplication 实例，供 App.xaml.cs 在后台线程调用 RunAsync。
/// </summary>
public static class HostBootstrapper
{
    /// <summary>
    /// 构建并配置 WebApplication，绑定到指定本地端口。
    /// port 由 App.ResolvePort() 决定：优先读 appsettings.json，端口冲突时 fallback 到随机空闲端口。
    /// </summary>
    public static WebApplication Build(int port)
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{port}");

        // Desktop 跨程序集加载控制器（Service 程序集）
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(AIQueryController).Assembly)
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
                Title = "Obsidian RAG API (Desktop)",
                Version = "v1",
                Description = "Obsidian 笔记库向量检索 API - Desktop Edition"
            });
        });

        // 核心领域服务（与 Service/Program.cs 共用）
        builder.Services.AddRagCoreServices(builder.Configuration);

        // CORS：WebView2 与 Kestrel 同源，仅允许本机此端口
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

        var app = builder.Build();

        app.UseCors();

        // MCP 中间件（必须在 CORS 之后，其他中间件之前）
        app.UseAppMcpHelper();

        // Swagger
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Obsidian RAG API v1");
        });

        // API Key 认证
        app.UseApiKeyAuthentication();

        // 静态文件（Vue SPA）：index.html 禁缓存，带 hash 的资源永久缓存
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
                    ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                }
            }
        });

        // 共用端点注册、目录初始化（与 Service/Program.cs 共用）
        app.UseRagEndpoints();

        return app;
    }

    /// <summary>
    /// 初始化 BM25 索引，须在 Kestrel 启动前调用。
    /// </summary>
    public static Task InitializeSearchAsync(WebApplication app)
        => app.InitializeSearchAsync();
}
