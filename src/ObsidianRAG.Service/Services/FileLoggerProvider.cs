namespace ObsidianRAG.Service.Services;

/// <summary>
/// 简单文件日志提供器，按日期轮转，所有 logger 共享一个文件写入器
/// </summary>
public class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLoggerWriter _writer;

    public FileLoggerProvider(string logDir) => _writer = new FileLoggerWriter(logDir);

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer);

    public void Dispose() => _writer.Dispose();
}

/// <summary>
/// 全局共享的文件写入器，线程安全
/// </summary>
public class FileLoggerWriter : IDisposable
{
    private readonly string _logDir;
    private StreamWriter? _writer;
    private string? _currentDate;
    private readonly Lock _lock = new();

    public FileLoggerWriter(string logDir) => _logDir = logDir;

    public void WriteLine(string line)
    {
        lock (_lock)
        {
            EnsureWriter();
            _writer?.WriteLine(line);
            _writer?.Flush();
        }
    }

    private void EnsureWriter()
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        if (date == _currentDate && _writer != null) return;

        _writer?.Dispose();
        _currentDate = date;
        var filePath = Path.Combine(_logDir, $"obsidian-rag-{date}.log");
        _writer = new StreamWriter(filePath, append: true) { AutoFlush = true };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}

public class FileLogger : ILogger
{
    private readonly string _category;
    private readonly FileLoggerWriter _writer;

    public FileLogger(string category, FileLoggerWriter writer)
    {
        _category = category;
        _writer = writer;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var message = formatter(state, exception);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_category}: {message}";
        if (exception != null)
            line += $"\n{exception}";

        _writer.WriteLine(line);
    }
}
