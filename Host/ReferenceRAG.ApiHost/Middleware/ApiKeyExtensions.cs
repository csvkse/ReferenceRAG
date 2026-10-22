using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.ApiHost.Authentication;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Middleware;

/// <summary>
/// API Key 中间件扩展方法
/// 采用“配置文件优先级最高，其次是环境变量”策略仲裁解析访问密钥
/// </summary>
public static class ApiKeyExtensions
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("ApiKeyExtensions");

    public static IApplicationBuilder UseApiKeyAuthentication(this IApplicationBuilder app)
    {
        var configManager = app.ApplicationServices.GetService<ConfigManager>();
        var configuration = app.ApplicationServices.GetService<IConfiguration>();

        var resolver = app.ApplicationServices.GetService<IAuthenticationKeyResolver>()
            ?? new AuthenticationKeyResolutionCoordinator(configManager: configManager, configuration: configuration);

        var result = resolver.ResolveKey();

        if (result.IsEnabled)
        {
            Console.WriteLine($"[ApiKey] API Key 认证已启用 ({result.Details})，受保护接口需要认证");
            _logger.LogInformation($"[ApiKey] API Key 认证已启用 ({result.Details})，受保护接口需要认证");
        }
        else
        {
            Console.WriteLine($"[ApiKey] API Key 认证未启用（未在配置文件或环境变量中配置 ApiKey）");
            _logger.LogInformation($"[ApiKey] API Key 认证未启用（未在配置文件或环境变量中配置 ApiKey）");
        }

        app.UseMiddleware<ApiKeyMiddleware>();
        return app;
    }
}
