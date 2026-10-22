using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ReferenceRAG.ApiHost.Features.Logging.Models;
using ReferenceRAG.ApiHost.Features.Logging.Strategies;

namespace ReferenceRAG.ApiHost.Features.Logging.Services;

/// <summary>
/// 日志文件服务实现 (适配器 + 策略编排)
/// </summary>
public sealed partial class LogFileService : ILogFileService
{
    private readonly string _logDirectory;
    private readonly ILogger<LogFileService> _logger;
    private readonly ILogReaderStrategy _fastStrategy;
    private readonly ILogReaderStrategy _queueStrategy;

    public LogFileService(IConfiguration configuration, ILogger<LogFileService> logger)
    {
        _logger = logger;
        _logDirectory = ResolveLogDirectory(configuration);
        _fastStrategy = new FastReverseSeekTailStrategy();
        _queueStrategy = new StreamingQueueTailStrategy();

        _logger.LogInformation("LogFileService 初始化就绪，监听日志目录: {LogDirectory}", _logDirectory);
    }

    /// <summary>
    /// 解析日志目录，保持与 ServiceManager.ConfigureLogging 的优先级逻辑完全一致
    /// </summary>
    public static string ResolveLogDirectory(IConfiguration configuration)
    {
        var workDir = configuration["Work:Directory"];
        workDir = string.IsNullOrWhiteSpace(workDir) ? AppContext.BaseDirectory : workDir;

        var configuredLogsPath = configuration["LogsPath"]
            ?? Environment.GetEnvironmentVariable("REFERENCERAG_LOGS_PATH")
            ?? Environment.GetEnvironmentVariable("LogsPath")
            ?? configuration["ReferenceRAG:logsPath"];

        if (!string.IsNullOrWhiteSpace(configuredLogsPath))
        {
            return Path.IsPathRooted(configuredLogsPath)
                ? Path.GetFullPath(configuredLogsPath)
                : Path.GetFullPath(Path.Combine(workDir, configuredLogsPath));
        }

        var lowerLogs = Path.Combine(workDir, "logs");
        var upperLogs = Path.Combine(workDir, "Logs");
        if (Directory.Exists(lowerLogs)) return lowerLogs;
        if (Directory.Exists(upperLogs)) return upperLogs;

        return OperatingSystem.IsWindows() ? upperLogs : lowerLogs;
    }

    public IReadOnlyList<LogFileInfo> GetLogFiles()
    {
        if (!Directory.Exists(_logDirectory))
        {
            return Array.Empty<LogFileInfo>();
        }

        try
        {
            var dirInfo = new DirectoryInfo(_logDirectory);
            var today = DateTime.Today;

            var files = dirInfo.EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
                            f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new LogFileInfo(
                    Name: f.Name,
                    LengthBytes: f.Length,
                    LastWriteTime: f.LastWriteTime,
                    IsActiveToday: f.LastWriteTime.Date == today
                ))
                .ToList();

            return files;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取日志文件列表失败: {Directory}", _logDirectory);
            return Array.Empty<LogFileInfo>();
        }
    }

    public async Task<LogContentResult?> ReadTailAsync(
        string fileName,
        int lines = 300,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateFileName(fileName, out var safePath))
        {
            _logger.LogWarning("拒绝潜在路径穿越或非法文件名的日志读取请求: {FileName}", fileName);
            return null;
        }

        if (!File.Exists(safePath))
        {
            return null;
        }

        try
        {
            var fileInfo = new FileInfo(safePath);
            var strategy = _queueStrategy.CanHandle(fileInfo, lines) ? _queueStrategy : _fastStrategy;

            var (content, isTruncated) = await strategy.ReadTailAsync(safePath, lines, cancellationToken);

            var lineCount = content.Length == 0 ? 0 : content.Count(c => c == '\n') + 1;

            return new LogContentResult(
                Name: fileName,
                RequestedLines: lines,
                ReturnedLines: lineCount,
                IsTruncated: isTruncated,
                Content: content
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取日志文件尾部失败: {FileName}", fileName);
            throw;
        }
    }

    public (Stream Stream, string ContentType, string DownloadName)? OpenRead(string fileName)
    {
        if (!ValidateFileName(fileName, out var safePath) || !File.Exists(safePath))
        {
            return null;
        }

        try
        {
            var stream = new FileStream(
                safePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                useAsync: true);

            return (stream, "text/plain; charset=utf-8", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开日志下载流失败: {FileName}", fileName);
            return null;
        }
    }

    /// <summary>
    /// 双重校验：白名单正则 + 物理路径限制，坚决防御路径穿越攻击
    /// </summary>
    public bool ValidateFileName(string? fileName, out string safeFullPath)
    {
        safeFullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (!SafeFileNameRegex().IsMatch(fileName))
        {
            return false;
        }

        var candidatePath = Path.GetFullPath(Path.Combine(_logDirectory, fileName));
        var normalizedLogDir = Path.GetFullPath(_logDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 验证候选物理路径必须位于日志根目录之下
        if (!candidatePath.StartsWith(normalizedLogDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !candidatePath.Equals(normalizedLogDir, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        safeFullPath = candidatePath;
        return true;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.]+\.(log|txt)$")]
    private static partial Regex SafeFileNameRegex();
}
