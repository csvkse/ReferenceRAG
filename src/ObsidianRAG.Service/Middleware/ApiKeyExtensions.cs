using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace ObsidianRAG.Service.Middleware;

/// <summary>
/// API Key 中间件扩展方法
/// </summary>
public static class ApiKeyExtensions
{
    public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder app)
    {
        var config = app.ApplicationServices.GetRequiredService<IConfiguration>();
        var enabled = config.GetValue<bool>("ApiKey:Enabled", false);
        var apiKey = config.GetValue<string>("ApiKey:Key", "");

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
