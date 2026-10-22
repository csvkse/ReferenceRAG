using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ReferenceRAG.ApiHost.Diagnostics;

/// <summary>
/// 诊断模块依赖注入与中间件注册快捷扩展
/// </summary>
public static class DiagnosticsServiceCollectionExtensions
{
    private static IDiagnosticsEngineAdapter? _adapterInstance;

    /// <summary>
    /// 条件注册 TemporaryDiagnostics 诊断服务（若传入有效 DIAG_KEY 环境变量才注册）
    /// </summary>
    public static IServiceCollection AddConditionalTemporaryDiagnostics(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        _adapterInstance = new TemporaryDiagnosticsAdapter();
        _adapterInstance.ConfigureServices(services, configuration);
        return services;
    }

    /// <summary>
    /// 条件挂载 TemporaryDiagnostics 管道与端点（若状态为 ArmedAndActive 才挂载）
    /// </summary>
    public static WebApplication UseConditionalTemporaryDiagnostics(this WebApplication app)
    {
        _adapterInstance ??= new TemporaryDiagnosticsAdapter();
        _adapterInstance.UseMiddlewareAndEndpoints(app);
        return app;
    }
}
