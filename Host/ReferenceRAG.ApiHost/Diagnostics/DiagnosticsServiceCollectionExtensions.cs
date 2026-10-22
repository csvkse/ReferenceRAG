using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.ApiHost.Diagnostics;

/// <summary>
/// 诊断模块依赖注入与中间件注册快捷扩展
/// </summary>
public static class DiagnosticsServiceCollectionExtensions
{
    private static IDiagnosticsEngineAdapter? _adapterInstance;

    /// <summary>
    /// 条件注册 TemporaryDiagnostics 诊断服务（严格满足：配置文件 > 环境变量）
    /// </summary>
    public static IServiceCollection AddConditionalTemporaryDiagnostics(
        this IServiceCollection services,
        IConfiguration configuration,
        ConfigManager? configManager = null)
    {
        try
        {
            configManager ??= new ConfigManager();
        }
        catch
        {
            // 忽略非关键初始化异常，依靠 IConfiguration 降级
        }

        _adapterInstance = new TemporaryDiagnosticsAdapter(configManager, configuration);
        _adapterInstance.ConfigureServices(services, configuration);
        return services;
    }

    /// <summary>
    /// 条件挂载 TemporaryDiagnostics 管道与端点（若状态为 ArmedAndActive 才挂载）
    /// </summary>
    public static WebApplication UseConditionalTemporaryDiagnostics(this WebApplication app)
    {
        if (_adapterInstance == null)
        {
            var configManager = app.Services.GetService<ConfigManager>();
            var configuration = app.Services.GetService<IConfiguration>();
            _adapterInstance = new TemporaryDiagnosticsAdapter(configManager, configuration);
        }
        _adapterInstance.UseMiddlewareAndEndpoints(app);
        return app;
    }
}
