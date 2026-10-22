namespace ReferenceRAG.ApiHost.Features.Logging.Models;

/// <summary>
/// 操作审计日志项
/// </summary>
public sealed class OperationLogEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 操作类别/名称，如 RebuildIndex, SwitchModel, SaveSettings, RestartService 等
    /// </summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>
    /// 操作目标类型，如 Source, Model, Settings, System 等
    /// </summary>
    public string TargetType { get; init; } = string.Empty;

    /// <summary>
    /// 操作目标名称/标识
    /// </summary>
    public string TargetName { get; init; } = string.Empty;

    /// <summary>
    /// 操作详情描述
    /// </summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>
    /// 客户端 IP 地址
    /// </summary>
    public string? ClientIp { get; init; }

    /// <summary>
    /// 操作是否成功
    /// </summary>
    public bool Success { get; init; } = true;
}
