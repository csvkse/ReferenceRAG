using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.ApiHost.Authentication;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Middleware;

/// <summary>
/// API Key authentication middleware - supports /api/*, /v1/*, and SignalR /indexHub
/// Supports X-API-Key header, Authorization: Bearer <key>, and dynamic configuration reload.
/// 严格遵循“配置文件优先级最高，其次是环境变量”策略。
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
        var (enabled, expectedApiKey) = GetApiKeyConfig(context);

        // Special endpoint: /api/auth/check - always allowed, returns auth status
        if (context.Request.Path.StartsWithSegments("/api/auth/check"))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(new {
                authRequired = enabled,
                message = enabled ? "API Key is required" : "No authentication required"
            });
            return;
        }

        // Only authenticate protected API and hub paths, skip static files and web pages
        if (!IsProtectedPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        // Check if API Key authentication is enabled
        if (!enabled || string.IsNullOrEmpty(expectedApiKey))
        {
            await _next(context);
            return;
        }

        var providedKey = ExtractApiKey(context);

        if (string.IsNullOrEmpty(providedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "Missing API Key. Please provide via X-API-Key header or Authorization: Bearer <key>" });
            return;
        }

        if (!string.Equals(providedKey, expectedApiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("API Key verification failed, source IP: {RemoteIp}, path: {Path}", context.Connection.RemoteIpAddress, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid API Key" });
            return;
        }

        await _next(context);
    }

    private static bool IsProtectedPath(PathString path)
    {
        return path.StartsWithSegments("/api")
            || path.StartsWithSegments("/v1")
            || path.StartsWithSegments("/indexHub");
    }

    private static (bool Enabled, string? Key) GetApiKeyConfig(HttpContext context)
    {
        // 1. 优先通过 IAuthenticationKeyResolver 仲裁解析（严格满足：配置文件 > 环境变量）
        var resolver = context.RequestServices.GetService<IAuthenticationKeyResolver>();
        if (resolver == null)
        {
            var configManager = context.RequestServices.GetService<ConfigManager>();
            var appConfig = context.RequestServices.GetService<IConfiguration>();
            resolver = new AuthenticationKeyResolutionCoordinator(configManager: configManager, configuration: appConfig);
        }

        var result = resolver.ResolveKey();
        if (result.IsEnabled && !string.IsNullOrEmpty(result.ApiKey))
        {
            return (true, result.ApiKey);
        }

        // 2. 回退检查 HttpContext.Items 覆盖（用于特定单元测试场景）
        if (context.Items["ApiKeyEnabled"] is bool itemsEnabled && itemsEnabled)
        {
            var itemsKey = context.Items["ApiKeyValue"] as string;
            return (true, itemsKey);
        }

        return (false, null);
    }

    private static string? ExtractApiKey(HttpContext context)
    {
        // 1. Header: X-API-Key
        if (context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var headerKey) && !string.IsNullOrWhiteSpace(headerKey))
        {
            return headerKey.ToString();
        }

        // 2. Header: Authorization: Bearer <key>
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var authStr = authHeader.ToString();
            if (authStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authStr["Bearer ".Length..].Trim();
                if (!string.IsNullOrEmpty(token))
                {
                    return token;
                }
            }
        }

        // 3. Query string for SignalR: access_token
        if (context.Request.Path.StartsWithSegments("/indexHub") &&
            context.Request.Query.TryGetValue("access_token", out var tokenQuery) &&
            !string.IsNullOrWhiteSpace(tokenQuery))
        {
            return tokenQuery.ToString();
        }

        return null;
    }
}
