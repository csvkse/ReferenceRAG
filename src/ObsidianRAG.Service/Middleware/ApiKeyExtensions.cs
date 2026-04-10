using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace ObsidianRAG.Service.Middleware;

/// <summary>
/// API Key 中间件扩展方法
/// </summary>
public static class ApiKeyExtensions
{
    /// <summary>
    /// 需要认证的 HTTP 方法（写操作）
    /// </summary>
    private static readonly HashSet<string> AuthenticatedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "POST", "PUT", "DELETE", "PATCH"
    };

    /// <summary>
    /// 无需认证的只读端点前缀（GET 请求到以下路径无需认证）
    /// </summary>
    private static readonly string[] PublicReadOnlyPrefixes = new[]
    {
        "/api/system/health",
        "/api/system/status"
    };

    public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder app)
    {
        var config = app.ApplicationServices.GetRequiredService<IConfiguration>();
        var enabled = config.GetValue<bool>("ApiKey:Enabled", false);
        var apiKey = config.GetValue<string>("ApiKey:Key", "");

        app.Use(async (context, next) =>
        {
            context.Items["ApiKeyEnabled"] = enabled;
            context.Items["ApiKeyValue"] = apiKey;
            context.Items["ApiKeyRequireAuth"] = IsAuthRequired(context);
            await next();
        });

        app.UseMiddleware<ApiKeyMiddleware>();
        return app;
    }

    /// <summary>
    /// 判断当前请求是否需要认证
    /// 策略：写操作(POST/PUT/DELETE/PATCH)需要认证，GET请求不需要认证
    /// </summary>
    private static bool IsAuthRequired(HttpContext context)
    {
        var method = context.Request.Method;

        // 写操作需要认证
        if (AuthenticatedMethods.Contains(method))
        {
            return true;
        }

        // GET 请求默认不需要认证
        return false;
    }
}
