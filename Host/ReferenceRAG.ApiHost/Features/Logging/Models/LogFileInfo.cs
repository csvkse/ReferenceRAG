namespace ReferenceRAG.ApiHost.Features.Logging.Models;

/// <summary>
/// 日志文件元数据项
/// </summary>
public sealed record LogFileInfo(
    string Name,
    long LengthBytes,
    DateTime LastWriteTime,
    bool IsActiveToday
);
