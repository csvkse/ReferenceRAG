using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using ObsidianRAG.Core.Services;

namespace ObsidianRAG.Service.Middleware;

/// <summary>
/// API Key 中间件扩展方法
/// </summary>
public static class ApiKeyExtensions
{
    public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder app)
    {
        // 从 ConfigManager 读取配置（优先级高于 appsettings.json）
        var configManager = app.ApplicationServices.GetRequiredService<ConfigManager>();
        var config = configManager.Load();
        var apiKey = config.Service?.ApiKey;

        // 判断是否启用认证：ApiKey 不为空则启用
        var enabled = !string.IsNullOrEmpty(apiKey);

        // 同时支持从 appsettings.json 读取（向后兼容）
        var appConfig = app.ApplicationServices.GetRequiredService<IConfiguration>();
        if (!enabled)
        {
            enabled = appConfig.GetValue<bool>("ApiKey:Enabled", false);
            apiKey = appConfig.GetValue<string>("ApiKey:Key", "");
        }

        if (enabled)
        {
            Console.WriteLine($"[ApiKey] API Key 认证已启用，所有接口需要认证");
        }
        else
        {
            Console.WriteLine($"[ApiKey] API Key 认证未启用（未配置 ApiKey）");
        }

        app.Use(async (context, next) =>
        {
            context.Items["ApiKeyEnabled"] = enabled;
            context.Items["ApiKeyValue"] = apiKey;
            await next();
        });

        app.UseMiddleware<ApiKeyMiddleware>();
        return app;
    }
}
