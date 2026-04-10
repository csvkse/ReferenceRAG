using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ObsidianRAG.Service.Middleware;

/// <summary>
/// API Key 认证中间件 - 可通过配置 ApiKey:Enabled 关闭
/// </summary>
public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;
    private const string ApiKeyHeaderName = "X-API-Key";

    public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // 检查是否启用 API Key 认证
        var enabled = context.Items["ApiKeyEnabled"] as bool? ?? false;
        if (!enabled)
        {
            await _next(context);
            return;
        }

        // 健康检查端点无需认证
        if (context.Request.Path.StartsWithSegments("/api/system/health"))
        {
            await _next(context);
            return;
        }

        var expectedApiKey = context.Items["ApiKeyValue"] as string;
        if (string.IsNullOrEmpty(expectedApiKey))
        {
            await _next(context);
            return;
        }

        // 支持 Header 和 QueryString 传递 API Key (QueryString 方便 SignalR 连接)
        string? providedKey = null;
        if (context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var headerKey))
        {
            providedKey = headerKey;
        }
        else if (context.Request.Query.TryGetValue("api_key", out var queryKey))
        {
            providedKey = queryKey;
        }

        if (string.IsNullOrEmpty(providedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "缺少 API Key。请在请求头中添加 X-API-Key 或在查询参数中添加 api_key" });
            return;
        }

        if (!string.Equals(providedKey, expectedApiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("API Key 验证失败，来源 IP: {RemoteIp}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "API Key 无效" });
            return;
        }

        await _next(context);
    }
}
