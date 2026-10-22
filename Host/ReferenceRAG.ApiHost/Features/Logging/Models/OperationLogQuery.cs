namespace ReferenceRAG.ApiHost.Features.Logging.Models;

/// <summary>
/// 操作审计日志查询参数
/// </summary>
public sealed class OperationLogQuery
{
    public string? Keyword { get; set; }

    public string? Action { get; set; }

    public string? TargetType { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 20;
}
