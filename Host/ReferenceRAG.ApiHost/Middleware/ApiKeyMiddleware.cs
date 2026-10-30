using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.ApiHost.Authentication;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.Security;

namespace ReferenceRAG.Service.Middleware;

/// <summary>
/// API Key 访问控制与双轨认证中间件：
/// 策略模型：默认强制认证（Default-Deny），支持系统主密钥与数据库多租户 API Key 并存；
/// 细粒度控制：涵盖通道控制 (REST API vs MCP)、业务模块树状控制 (Chat/Search/Sources/Files/Index/Models/System) 以及数据源隔离上下文注入；
/// 性能保障：内存加盐 SHA-256 并发字典直查，拦截路径零 I/O 阻塞。
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
        "/logs",
        "/apikeys"
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

    // 显式白名单：公开探针与遥测 API 路径（健康检查与匿名硬件加速感知接口）
    private static readonly HashSet<string> WhitelistedPublicApiRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/system/health",
        "/api/Settings/hardware-acceleration/status",
        "/api/Settings/cuda-availability",
        "/api/auth/check"
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
        var apiKeyStore = context.RequestServices.GetService<IApiKeyStore>();
        var (enabled, expectedApiKey) = GetApiKeyConfig(context);

        // 0. 特殊显式健康检查端点: /api/system/health - Docker/K8s 容器健康检查端点，无条件放行（支持 GET 与 HEAD）
        if (context.Request.Path.Equals("/api/system/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // 1. 特殊显式端点: /api/auth/check - 始终响应当前鉴权需求状态，无条件放行
        if (context.Request.Path.StartsWithSegments("/api/auth/check"))
        {
            var hasDbKeys = apiKeyStore != null && (await apiKeyStore.GetAllAsync()).Any(k => k.IsEnabled);
            var isAuthRequired = enabled || hasDbKeys;
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsJsonAsync(new
            {
                authRequired = isAuthRequired,
                message = isAuthRequired ? "API Key is required" : "No authentication required"
            });
            return;
        }

        // 2. 若系统既未配置静态主 Key，数据库中也没有任何已启用的自定义 Key，全部放行（免密兼容模式）
        var hasActiveCustomKeys = apiKeyStore != null && (await apiKeyStore.GetAllAsync()).Any(k => k.IsEnabled);
        if ((!enabled || string.IsNullOrEmpty(expectedApiKey)) && !hasActiveCustomKeys)
        {
            // 空对象模式（Null Object Pattern）：注入匿名全通实体，确保下游 MCP 与工具安全守卫无需处理 null
            context.Items["CurrentApiKey"] = new ApiKeyEntity
            {
                Id = "anonymous_open_access",
                Name = "免密开放访问",
                IsEnabled = true,
                AllowApi = true,
                AllowMcp = true,
                AllowedScopes = "[\"*\"]",
                AllowedMcpTools = "[\"*\"]",
                SourceScopeMode = "all",
                AllowedSources = "[]"
            };
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

        ApiKeyEntity? matchedKey = null;

        // 4.1 优先校验静态主密钥（完全权限超级管理员）
        if (enabled && !string.IsNullOrEmpty(expectedApiKey) && string.Equals(providedKey, expectedApiKey, StringComparison.Ordinal))
        {
            matchedKey = new ApiKeyEntity
            {
                Id = "system_master_key",
                Name = "系统静态主密钥",
                KeyPrefix = expectedApiKey.Length > 8 ? expectedApiKey[..8] : expectedApiKey,
                IsEnabled = true,
                AllowApi = true,
                AllowMcp = true,
                AllowedScopes = "[\"*\"]",
                AllowedMcpTools = "[\"*\"]",
                SourceScopeMode = "all",
                AllowedSources = "[]"
            };
        }
        else if (apiKeyStore != null)
        {
            // 4.2 校验数据库 API Key (加盐 SHA-256 微秒并发缓存查验)
            var hash = ApiKeyStore.ComputeHash(providedKey);
            matchedKey = apiKeyStore.FindByHash(hash);
        }

        if (matchedKey == null)
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

        if (!matchedKey.IsEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "API Key 已被禁用" });
            return;
        }

        if (matchedKey.ExpiresAt.HasValue && matchedKey.ExpiresAt.Value < DateTime.UtcNow)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "API Key 已过期" });
            return;
        }

        // 4.3 协议通道校验 (Channels)
        var pathStr = context.Request.Path.Value ?? "";
        var isMcpRequest = pathStr.StartsWith("/api/mcp", StringComparison.OrdinalIgnoreCase)
                        || pathStr.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase);

        if (isMcpRequest && !matchedKey.AllowMcp)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "此 API Key 未被授权使用 MCP 协议通道" });
            return;
        }

        if (!isMcpRequest && !matchedKey.AllowApi)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "此 API Key 未被授权使用 REST API 通道" });
            return;
        }

        // 4.4 业务模块权限校验 (Scopes)
        var targetModule = ResolveTargetModule(pathStr);
        if (!string.IsNullOrEmpty(targetModule) && !ApiKeyPermissionMatrix.HasScope(matchedKey, targetModule))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = $"此 API Key 无权访问业务模块: {targetModule}" });
            return;
        }

        // 注入当前上下文
        context.Items["CurrentApiKey"] = matchedKey;
        if (matchedKey.Id != "system_master_key" && apiKeyStore != null)
        {
            apiKeyStore.RecordLastUsed(matchedKey.Id);
        }

        // 5. 凭据校验通过：若通过 query string 传入，且为浏览器 GET 请求，自动注入 Session Cookie 方便页面子资源加载
        if (HttpMethods.IsGet(context.Request.Method) && HasQueryApiKey(context))
        {
            context.Response.Cookies.Append(ApiKeyCookieName, providedKey, new CookieOptions
            {
                Path = "/",
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps
            });
        }

        await _next(context);
    }

    private static string? ResolveTargetModule(string path)
    {
        if (path.StartsWith("/api/chat", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/mafchat", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/v1/chat", StringComparison.OrdinalIgnoreCase))
            return "chat";

        if (path.StartsWith("/api/aiquery", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/search", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/bm25index/search", StringComparison.OrdinalIgnoreCase))
            return "search";

        if (path.StartsWith("/api/sources", StringComparison.OrdinalIgnoreCase))
            return "sources";

        if (path.StartsWith("/api/filesystem", StringComparison.OrdinalIgnoreCase))
            return "files";

        if (path.StartsWith("/api/index", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/bm25index", StringComparison.OrdinalIgnoreCase))
            return "index";

        if (path.StartsWith("/api/models", StringComparison.OrdinalIgnoreCase))
            return "models";

        if (path.StartsWith("/api/system", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/settings", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/logs", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/apikeys", StringComparison.OrdinalIgnoreCase))
            return "system";

        return null;
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

        // 1. 显式公开探针与只读遥测 API 路径（无论路由是否已绑定端点元数据）
        var pathStr = context.Request.Path.Value ?? "";
        var normalizedApiPath = pathStr.TrimEnd('/');
        if (WhitelistedPublicApiRoutes.Contains(normalizedApiPath))
        {
            return true;
        }

        // 2. CORS 预检请求（浏览器不携带自定义请求头，不可阻断）
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            return true;
        }

        // 3. 受控临时诊断通道（由 TemporaryDiagnostics 的独立中间件与 DIAG_KEY 管辖，物理隔离）
        var diagPrefix = ResolveDiagPrefix(context);
        if (context.Request.Path.StartsWithSegments(diagPrefix))
        {
            return true;
        }

        // 4. 静态资源前缀（/app/、/core/、/shared/、/styles/、/vendor/、/assets/）
        foreach (var prefix in WhitelistedStaticPrefixes)
        {
            if (pathStr.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 5. 根目录静态文件（/app.js、/favicon.ico、/favicon.svg）
        if (WhitelistedStaticFiles.Contains(normalizedApiPath))
        {
            return true;
        }

        // 6. 静态 HTML/SPA 页面与视图（仅限安全只读的 GET / HEAD 方法）
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            if (WhitelistedSpaPageRoutes.Contains(normalizedApiPath) ||
                string.IsNullOrEmpty(normalizedApiPath))
            {
                return true;
            }

            var extension = Path.GetExtension(normalizedApiPath);
            if (!string.IsNullOrEmpty(extension))
            {
                if (extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".woff2", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
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

        if (context.Items["ApiKeyEnabled"] is bool itemsEnabled && itemsEnabled)
        {
            var itemsKey = context.Items["ApiKeyValue"] as string;
            return (true, itemsKey);
        }

        return (false, null);
    }

    private static string? ExtractApiKey(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(ApiKeyHeaderName, out var headerKey) && !string.IsNullOrWhiteSpace(headerKey))
        {
            return headerKey.ToString().Trim();
        }

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

        if (context.Request.Cookies.TryGetValue(ApiKeyCookieName, out var cookieKey) && !string.IsNullOrWhiteSpace(cookieKey))
        {
            return cookieKey.Trim();
        }

        return null;
    }
}
