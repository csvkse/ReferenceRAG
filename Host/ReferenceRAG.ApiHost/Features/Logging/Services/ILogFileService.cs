using ReferenceRAG.ApiHost.Features.Logging.Models;

namespace ReferenceRAG.ApiHost.Features.Logging.Services;

/// <summary>
/// 日志文件服务接口 (适配器模式)
/// 屏蔽底层文件系统与各种日志轮转前缀差异，向控制器提供统一门面。
/// </summary>
public interface ILogFileService
{
    /// <summary>
    /// 获取当前可用的日志文件列表（按最后修改时间降序）
    /// </summary>
    IReadOnlyList<LogFileInfo> GetLogFiles();

    /// <summary>
    /// 读取指定日志文件末尾 N 行
    /// </summary>
    Task<LogContentResult?> ReadTailAsync(
        string fileName,
        int lines = 300,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 打开指定日志文件流用于下载
    /// </summary>
    (Stream Stream, string ContentType, string DownloadName)? OpenRead(string fileName);
}
