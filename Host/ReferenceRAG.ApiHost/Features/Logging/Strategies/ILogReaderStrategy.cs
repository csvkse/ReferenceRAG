namespace ReferenceRAG.ApiHost.Features.Logging.Strategies;

/// <summary>
/// 日志读取策略接口 (策略模式)
/// </summary>
public interface ILogReaderStrategy
{
    /// <summary>
    /// 判断当前策略是否适用于目标文件
    /// </summary>
    bool CanHandle(FileInfo fileInfo, int lines);

    /// <summary>
    /// 读取日志末尾 N 行内容
    /// </summary>
    Task<(string Content, bool IsTruncated)> ReadTailAsync(
        string filePath,
        int lines,
        CancellationToken cancellationToken = default);
}
