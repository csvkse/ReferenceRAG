using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ObsidianRAG.Service.Middleware;

/// <summary>
/// API Key authentication middleware - only authenticates /api/* paths
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
        // Special endpoint: /api/auth/check - always allowed, returns auth status
        if (context.Request.Path.StartsWithSegments("/api/auth/check"))
        {
            var authEnabled = context.Items["ApiKeyEnabled"] as bool? ?? false;
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(new {
                authRequired = authEnabled,
                message = authEnabled ? "API Key is required" : "No authentication required"
            });
            return;
        }

        // Only authenticate /api/* paths, skip static files
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        // Check if API Key authentication is enabled
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

        // Only support Header for API Key (security consideration)
        string? providedKey = null;
        if (context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var headerKey))
        {
            providedKey = headerKey;
        }

        if (string.IsNullOrEmpty(providedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing API Key. Please add X-API-Key header" });
            return;
        }

        if (!string.Equals(providedKey, expectedApiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("API Key verification failed, source IP: {RemoteIp}", context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid API Key" });
            return;
        }

        await _next(context);
    }
}
