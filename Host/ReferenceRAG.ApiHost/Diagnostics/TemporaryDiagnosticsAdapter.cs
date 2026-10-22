using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;
using TemporaryDiagnostics.AspNetCore.Extensions;

namespace ReferenceRAG.ApiHost.Diagnostics;

/// <summary>
/// TemporaryDiagnostics 中间件受控适配器（适配器模式 + 状态机模式）
/// 严格根据环境变量（DIAG_KEY / TEMPORARY_DIAGNOSTICS_KEY）控制是否启动诊断中枢
/// </summary>
public sealed class TemporaryDiagnosticsAdapter : IDiagnosticsEngineAdapter
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("TemporaryDiagnosticsAdapter");

    private const string DefaultRoutePrefix = "/__diag";
    private const int MinimumEntropyKeyLength = 8;

    private static readonly string[] CandidateVariableNames = new[]
    {
        "DIAG_KEY",
        "TEMPORARY_DIAGNOSTICS_KEY",
        "DIAGNOSTIC_ACCESS_KEY"
    };

    private readonly Func<string, string?> _envAccessor;
    private DiagnosticsLifecycleState _state = DiagnosticsLifecycleState.Unconfigured;
    private string? _resolvedKey;
    private string _routePrefix = DefaultRoutePrefix;
    private string _statusMessage = "未配置诊断密钥（诊断中间件未启用）";

    public DiagnosticsLifecycleState CurrentState => _state;
    public bool IsEnabled => _state == DiagnosticsLifecycleState.ArmedAndActive;
    public string RoutePrefix => _routePrefix;

    public TemporaryDiagnosticsAdapter(Func<string, string?>? envAccessor = null)
    {
        _envAccessor = envAccessor ?? Environment.GetEnvironmentVariable;
        EvaluateEnvironment();
    }

    public DiagnosticsLifecycleReport EvaluateEnvironment()
    {
        // 1. 检索路由前缀（可通过 DIAG_ROUTE_PREFIX 自定义）
        var customPrefix = _envAccessor("DIAG_ROUTE_PREFIX")?.Trim();
        if (!string.IsNullOrWhiteSpace(customPrefix))
        {
            _routePrefix = customPrefix.StartsWith('/') ? customPrefix : "/" + customPrefix;
        }
        else
        {
            _routePrefix = DefaultRoutePrefix;
        }

        // 2. 检索诊断密钥
        string? foundKey = null;
        string? matchedVarName = null;

        foreach (var varName in CandidateVariableNames)
        {
            var val = _envAccessor(varName)?.Trim();
            if (!string.IsNullOrEmpty(val))
            {
                foundKey = val;
                matchedVarName = varName;
                break;
            }
        }

        if (string.IsNullOrEmpty(foundKey))
        {
            _state = DiagnosticsLifecycleState.Unconfigured;
            _resolvedKey = null;
            _statusMessage = "未配置环境变量 DIAG_KEY（诊断中间件完全不启动，零内存/零路由开销）";
            return new DiagnosticsLifecycleReport(_state, IsEnabled: false, _routePrefix, _statusMessage);
        }

        // 3. 安全防御校验：强制要求高熵与防弱口令
        if (foundKey.Length < MinimumEntropyKeyLength)
        {
            _state = DiagnosticsLifecycleState.InvalidEntropy;
            _resolvedKey = null;
            _statusMessage = $"检测到环境变量 {matchedVarName}，但长度不足 {MinimumEntropyKeyLength} 位（防弱口令安全策略），为保障生产安全已拒绝启动诊断中间件！";
            _logger.LogWarning(_statusMessage);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[警告] [TemporaryDiagnostics] {_statusMessage}");
            Console.ResetColor();
            return new DiagnosticsLifecycleReport(_state, IsEnabled: false, _routePrefix, _statusMessage);
        }

        // 4. 校验通过，激活
        _state = DiagnosticsLifecycleState.ArmedAndActive;
        _resolvedKey = foundKey;
        _statusMessage = $"诊断中间件已就绪 (变量来源: {matchedVarName}, 路径前缀: {_routePrefix})";

        return new DiagnosticsLifecycleReport(_state, IsEnabled: true, _routePrefix, _statusMessage);
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        if (_state != DiagnosticsLifecycleState.ArmedAndActive || string.IsNullOrEmpty(_resolvedKey))
        {
            // 未激活状态下完全不注入诊断基础设施，保证 0 开销
            return;
        }

        _logger.LogInformation($"[TemporaryDiagnostics] 正在注册诊断基础设施与依赖注入探查器...");
        Console.WriteLine($"[TemporaryDiagnostics] 正在装载受控诊断中间件 (访问前缀: {_routePrefix})");

        services.AddTemporaryDiagnostics(options =>
        {
            options.AccessKey = _resolvedKey;
            options.RoutePrefix = _routePrefix;
            options.SessionTimeoutMinutes = 60;
            options.EnableDynamicInvoke = true;
            options.EnableTerminal = true;
            options.EnableMcp = true;
        });

        // 注入适配器自身供运行时状态查询
        services.AddSingleton<IDiagnosticsEngineAdapter>(this);
    }

    public void UseMiddlewareAndEndpoints(WebApplication app)
    {
        if (_state != DiagnosticsLifecycleState.ArmedAndActive)
        {
            return;
        }

        _logger.LogInformation($"[TemporaryDiagnostics] 正在挂载 WebSockets、安全鉴权与诊断路由端点...");
        app.UseTemporaryDiagnostics();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[TemporaryDiagnostics] 受控诊断中枢已就绪:");
        Console.WriteLine($"  - 交互控制台: {_routePrefix}");
        Console.WriteLine($"  - OpenAPI 文档: {_routePrefix}/docs");
        Console.WriteLine($"  - 原生 MCP 端点: {_routePrefix}/mcp");
        Console.ResetColor();
    }
}
