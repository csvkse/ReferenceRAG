using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.ApiHost.Authentication;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Middleware;

/// <summary>
/// API Key 访问控制与认证中间件
/// 策略模型：默认强制认证（Default-Deny），仅显式公共白名单放行（公开探测端点、前端页面与静态资源、独立诊断通道）。
/// 密钥仲裁：严格遵循“配置文件优先级最高，其次是环境变量”策略。
/// 凭据提取：支持 Header (X-API-Key / Authorization: Bearer)、Query (access_token / apiKey / api_key)、Cookie (reference_rag_api_key)。
/// </summary>
public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public const string ApiKeyHeaderName = "X-API-Key";
    public const string ApiKeyCookieName = "reference_rag_api_key";

    // 显式白名单：前端 SPA 页面视图路径（仅限 GET 请求）
    private static readonly HashSet<string> WhitelistedSpaPageRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/",
        "/index.html",
        "/login",
        "/dashboard",
        "/search",
        "/sources",
        "/settings",
        "/performance",
        "/api-help",
        "/models",
        "/system",
        "/bm25-index",
        "/guide",
        "/graph",
        "/chat",
        "/logs"
    };

    // 显式白名单：前端静态资源目录前缀
    private static readonly string[] WhitelistedStaticPrefixes = new[]
    {
        "/app/",
        "/core/",
        "/shared/",
        "/styles/",
        "/vendor/",
        "/assets/"
    };

    // 显式白名单：根目录静态文件
    private static readonly HashSet<string> WhitelistedStaticFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "/app.js",
        "/favicon.ico",
        "/favicon.svg"
    };

    public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var (enabled, expectedApiKey) = GetApiKeyConfig(context);

        // 1. 特殊显式端点: /api/auth/check - 始终响应当前鉴权需求状态，无条件放行
        if (context.Request.Path.StartsWithSegments("/api/auth/check"))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(new
            {
                authRequired = enabled,
                message = enabled ? "API Key is required" : "No authentication required"
            });
            return;
        }

        // 2. 若未启用认证（未配置密钥），所有接口全部放行（免密兼容模式）
        if (!enabled || string.IsNullOrEmpty(expectedApiKey))
        {
            await _next(context);
            return;
        }

        // 3. 显式公开白名单判定（CORS OPTIONS、前端页面与静态资源、独立诊断通道）
        if (IsWhitelisted(context))
        {
            await _next(context);
            return;
        }

        // 4. 默认拒绝模型（Default-Deny）：所有非白名单端点必须携带合法凭据
        var providedKey = ExtractApiKey(context);

        if (string.IsNullOrEmpty(providedKey))
        {
            _logger.LogWarning("API Key missing for protected request: {Method} {Path} from {RemoteIp}",
                context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Missing API Key. Please provide via X-API-Key header, Authorization: Bearer <key>, query parameter ?apiKey=<key>, or cookie."
            });
            return;
        }

        if (!string.Equals(providedKey, expectedApiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("API Key verification failed, source IP: {RemoteIp}, path: {Path}",
                context.Connection.RemoteIpAddress, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Invalid API Key"
            });
            return;
        }

        // 5. 凭据校验通过：若通过 query string 传入，且为浏览器 GET 请求（例如访问 /swagger?apiKey=...），自动注入 Session Cookie 方便页面子资源加载
        if (HttpMethods.IsGet(context.Request.Method) && HasQueryApiKey(context))
        {
            context.Response.Cookies.Append(ApiKeyCookieName, expectedApiKey, new CookieOptions
            {
                Path = "/",
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps
            });
        }

        await _next(context);
    }

    /// <summary>
    /// 判定请求是否命中显式白名单公开接口或带有匿名特性
    /// </summary>
    private static bool IsWhitelisted(HttpContext context)
    {
        // 0. 特性检查：使用匿名特性的端点（控制器/Action 标记 [AllowAnonymous] 或挂载 IAllowAnonymous 元数据）直接放行
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata?.GetMetadata<IAllowAnonymous>() != null)
        {
            return true;
        }

        // 1. CORS 预检请求（浏览器不携带自定义请求头，不可阻断）
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            return true;
        }

        // 2. 受控临时诊断通道（由 TemporaryDiagnostics 的独立中间件与 DIAG_KEY 管辖，物理隔离）
        var diagPrefix = ResolveDiagPrefix(context);
        if (context.Request.Path.StartsWithSegments(diagPrefix))
        {
            return true;
        }

        // 3. 静态资源前缀（/app/、/core/、/shared/、/styles/、/vendor/、/assets/）
        var pathStr = context.Request.Path.Value ?? "";
        foreach (var prefix in WhitelistedStaticPrefixes)
        {
            if (pathStr.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 4. 根目录静态图标/脚本
        if (WhitelistedStaticFiles.Contains(pathStr))
        {
            return true;
        }

        // 5. 前端 SPA 页面路由（仅在 GET 请求时放行伺服 index.html 视图）
        if (HttpMethods.IsGet(context.Request.Method))
        {
            var normalizedPath = pathStr.TrimEnd('/');
            if (string.IsNullOrEmpty(normalizedPath))
            {
                normalizedPath = "/";
            }

            if (WhitelistedSpaPageRoutes.Contains(normalizedPath))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveDiagPrefix(HttpContext context)
    {
        try
        {
            var appConfig = context.RequestServices.GetService<IConfiguration>();
            var configManager = context.RequestServices.GetService<ConfigManager>();
            var prefix = configManager?.Load()?.Service?.DiagRoutePrefix
                ?? appConfig?.GetValue<string>("Diagnostics:RoutePrefix")
                ?? "/__diag";
            return string.IsNullOrWhiteSpace(prefix) ? "/__diag" : prefix;
        }
        catch
        {
            return "/__diag";
        }
    }

    private static bool HasQueryApiKey(HttpContext context)
    {
        return context.Request.Query.ContainsKey("apiKey")
            || context.Request.Query.ContainsKey("api_key")
            || context.Request.Query.ContainsKey("access_token");
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
            return headerKey.ToString().Trim();
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

        // 3. Query string: access_token (SignalR WebSocket / SSE 标准握手), apiKey, api_key (Swagger UI 等浏览器直链)
        if (context.Request.Query.TryGetValue("access_token", out var tokenQuery) && !string.IsNullOrWhiteSpace(tokenQuery))
        {
            return tokenQuery.ToString().Trim();
        }

        if (context.Request.Query.TryGetValue("apiKey", out var apiKeyQuery) && !string.IsNullOrWhiteSpace(apiKeyQuery))
        {
            return apiKeyQuery.ToString().Trim();
        }

        if (context.Request.Query.TryGetValue("api_key", out var apiKeySnakeQuery) && !string.IsNullOrWhiteSpace(apiKeySnakeQuery))
        {
            return apiKeySnakeQuery.ToString().Trim();
        }

        // 4. Cookie: reference_rag_api_key (浏览器访问 Swagger UI 等场景的自动会话维持)
        if (context.Request.Cookies.TryGetValue(ApiKeyCookieName, out var cookieKey) && !string.IsNullOrWhiteSpace(cookieKey))
        {
            return cookieKey.Trim();
        }

        return null;
    }
}
