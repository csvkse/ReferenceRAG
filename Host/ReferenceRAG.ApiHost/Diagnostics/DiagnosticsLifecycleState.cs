using ReferenceRAG.ApiHost.Authentication;

namespace ReferenceRAG.ApiHost.Diagnostics;

/// <summary>
/// 诊断中间件生命周期状态
/// </summary>
public enum DiagnosticsLifecycleState
{
    /// <summary>
    /// 未配置（配置文件与环境变量中均未配置有效 DIAG_KEY，诊断中间件完全不启动，零内存/零路由开销）
    /// </summary>
    Unconfigured = 0,

    /// <summary>
    /// 密钥熵值或格式无效（配置了 DIAG_KEY 但长度不足 8 位，处于安全防御阻断态）
    /// </summary>
    InvalidEntropy = 1,

    /// <summary>
    /// 就绪且已激活（密钥校验通过，诊断路由、Web Terminal 与 MCP 全部装载运行）
    /// </summary>
    ArmedAndActive = 2,

    /// <summary>
    /// 已挂起或撤销
    /// </summary>
    Suspended = 3
}

/// <summary>
/// 诊断生命周期评估报告
/// </summary>
public sealed record DiagnosticsLifecycleReport(
    DiagnosticsLifecycleState State,
    bool IsEnabled,
    string? RoutePrefix,
    string StatusMessage,
    KeyResolutionSource Source = KeyResolutionSource.None
);
