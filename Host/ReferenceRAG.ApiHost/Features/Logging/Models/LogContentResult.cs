namespace ReferenceRAG.ApiHost.Features.Logging.Models;

/// <summary>
/// 日志内容查询结果
/// </summary>
public sealed record LogContentResult(
    string Name,
    int RequestedLines,
    int ReturnedLines,
    bool IsTruncated,
    string Content
);
