using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ReferenceRAG.ApiHost.Diagnostics;

/// <summary>
/// 诊断引擎适配器接口（适配器模式）
/// 负责解耦宿主与 TemporaryDiagnostics SDK，实现条件注册与受控装载
/// </summary>
public interface IDiagnosticsEngineAdapter
{
    /// <summary>
    /// 当前生命周期状态
    /// </summary>
    DiagnosticsLifecycleState CurrentState { get; }

    /// <summary>
    /// 是否已成功激活运行
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// 诊断路由前缀（默认 /__diag）
    /// </summary>
    string RoutePrefix { get; }

    /// <summary>
    /// 评估环境并获取诊断生命周期状态报告
    /// </summary>
    DiagnosticsLifecycleReport EvaluateEnvironment();

    /// <summary>
    /// 条件配置 DI 依赖注入服务
    /// </summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// 条件挂载 WebSockets、安全中间件与受控诊断端点
    /// </summary>
    void UseMiddlewareAndEndpoints(WebApplication app);
}
