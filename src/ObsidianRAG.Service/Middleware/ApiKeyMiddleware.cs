using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ObsidianRAG.Service.Middleware;

/// <summary>
/// API Key 认证中间件 - 仅对 /api/* 路径进行认证
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
        // 只对 /api/* 路径进行认证，静态文件直接放行
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        // 检查是否启用 API Key 认证
        var enabled = context.Items["ApiKeyEnabled"] as bool? ?? false;
        if (!enabled)
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

        // 仅支持 Header 传递 API Key (安全考虑)
        string? providedKey = null;
        if (context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var headerKey))
        {
            providedKey = headerKey;
        }

        if (string.IsNullOrEmpty(providedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "缺少 API Key。请在请求头中添加 X-API-Key" });
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
