using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 文件监控服务接口
/// </summary>
public interface IFileMonitorService
{
    /// <summary>
    /// 启动监控
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止监控
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 获取监控状态
    /// </summary>
    FileMonitorStatus GetStatus();

    /// <summary>
    /// 添加监控源
    /// </summary>
    void AddSource(string path, string name);

    /// <summary>
    /// 移除监控源
    /// </summary>
    void RemoveSource(string name);

    /// <summary>
    /// 文件变动事件
    /// </summary>
    event EventHandler<FileChangeEventArgs>? FileChanged;
}

/// <summary>
/// 文件监控状态
/// </summary>
public class FileMonitorStatus
{
    public bool IsRunning { get; set; }
    public int WatchedDirectories { get; set; }
    public int TotalFiles { get; set; }
    public DateTime? LastChange { get; set; }
    public int ChangesDetected { get; set; }
    public int IndexingQueueSize { get; set; }
}

/// <summary>
/// 待处理的文件变更记录
/// </summary>
internal class PendingChange
{
    public string FilePath { get; set; } = string.Empty;
    public string? OldFilePath { get; set; }
    public ChangeType ChangeType { get; set; }
    public DateTime Timestamp { get; set; }
    public string? Source { get; set; }
}

/// <summary>
/// 文件监控服务实现
/// </summary>
public class FileMonitorService : IFileMonitorService, IDisposable
{
    // 混合模式常量
    private const int ActiveThresholdMinutes = 5;
    private const int IdleScanIntervalSeconds = 30;
    private const int DefaultInotifyLimit = 8192;

    // 活跃目录追踪（线程安全）
    private readonly ConcurrentDictionary<string, DateTime> _activeDirectories = new();

    // 非活跃目录轮询扫描器
    private Timer? _idleScanner;

    // 混合模式状态
    private bool _hybridModeEnabled;
    private int _inotifyLimit = DefaultInotifyLimit;

    private readonly Dictionary<string, FileSystemWatcher> _watchers;
    private readonly Dictionary<string, string> _sources;
    private readonly ConcurrentDictionary<string, string> _oldPaths;
    private readonly ConcurrentDictionary<string, Timer> _debounceTimers;
    private readonly Timer _statusTimer;
    private readonly int _debounceMs;
    private readonly ILogger<FileMonitorService>? _logger;
    private readonly object _lock = new();
    private bool _disposed;
    private bool _isRunning;
    private int _changesDetected;
    private DateTime? _lastChange;

    public event EventHandler<FileChangeEventArgs>? FileChanged;

    public FileMonitorService(int debounceMs = 500, ILogger<FileMonitorService>? logger = null)
    {
        _watchers = new Dictionary<string, FileSystemWatcher>();
        _sources = new Dictionary<string, string>();
        _oldPaths = new ConcurrentDictionary<string, string>();
        _debounceTimers = new ConcurrentDictionary<string, Timer>();
        _debounceMs = debounceMs;
        _logger = logger;

        _statusTimer = new Timer(UpdateStatus, null, 5000, 5000);

        // 初始化非活跃目录扫描器（默认不启用）
        _idleScanner = new Timer(ScanIdleDirectories, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// 标记目录为活跃状态
    /// </summary>
    public void MarkDirectoryActive(string dirPath)
    {
        var normalizedPath = PathUtility.NormalizePath(dirPath);
        _activeDirectories[normalizedPath] = DateTime.UtcNow;
    }

    /// <summary>
    /// 判断目录是否活跃
    /// </summary>
    private bool IsDirectoryActive(string dirPath)
    {
        var normalizedPath = PathUtility.NormalizePath(dirPath);
        if (_activeDirectories.TryGetValue(normalizedPath, out var lastActive))
        {
            return DateTime.UtcNow - lastActive < TimeSpan.FromMinutes(ActiveThresholdMinutes);
        }
        return false;
    }

    /// <summary>
    /// 非活跃目录轮询扫描回调
    /// </summary>
    private void ScanIdleDirectories(object? state)
    {
        if (!_hybridModeEnabled || !_isRunning)
            return;

        try
        {
            foreach (var kvp in _activeDirectories.ToList())
            {
                var dirPath = kvp.Key;
                var lastActive = kvp.Value;

                // 如果目录已不活跃且仍在监控列表中，轮询检查变化
                if (DateTime.UtcNow - lastActive >= TimeSpan.FromMinutes(ActiveThresholdMinutes))
                {
                    if (Directory.Exists(dirPath) && !_watchers.Values.Any(w => w.Path == dirPath))
                    {
                        // 目录已降级为轮询模式，手动扫描检查变化
                        ScanDirectoryForChanges(dirPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "非活跃目录扫描出错");
        }
    }

    /// <summary>
    /// 扫描目录检查文件变化（轮询模式）
    /// </summary>
    private void ScanDirectoryForChanges(string dirPath)
    {
        try
        {
            if (!Directory.Exists(dirPath))
                return;

            var mdFiles = Directory.EnumerateFiles(dirPath, "*.md", SearchOption.AllDirectories);
            foreach (var filePath in mdFiles)
            {
                // 为每个文件触发变更事件（带防抖）
                var source = _sources.FirstOrDefault(s => s.Value == dirPath).Key;
                if (!string.IsNullOrEmpty(source))
                {
                    OnFileChanged(filePath, source, ChangeType.Modified);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "轮询扫描目录失败: {Path}", dirPath);
        }
    }

    public void AddSource(string path, string name)
    {
        var normalizedPath = PathUtility.NormalizePath(path);
        lock (_lock)
        {
            _sources[name] = normalizedPath;

            if (_isRunning)
            {
                CreateWatcher(normalizedPath, name);
            }
        }

        _logger?.LogInformation("添加监控源: {Name} ({Path})", name, normalizedPath);
    }

    public void RemoveSource(string name)
    {
        lock (_lock)
        {
            if (_sources.Remove(name))
            {
                if (_watchers.TryGetValue(name, out var watcher))
                {
                    watcher.Dispose();
                    _watchers.Remove(name);
                }
            }
        }

        _logger?.LogInformation("移除监控源: {Name}", name);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                _logger?.LogWarning("监控已在运行");
                return Task.CompletedTask;
            }

            foreach (var (name, path) in _sources)
            {
                CreateWatcher(path, name);
            }

            _isRunning = true;
            _changesDetected = 0;
            _lastChange = null;
        }

        _logger?.LogInformation("监控已启动，监控 {Count} 个目录", _watchers.Count);

        // 检查是否需要启用混合模式
        if (_sources.Count > _inotifyLimit)
        {
            EnableHybridMode();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 启用混合模式：活跃目录使用FSW，非活跃目录使用轮询
    /// </summary>
    private void EnableHybridMode()
    {
        if (_hybridModeEnabled)
            return;

        _hybridModeEnabled = true;
        _logger?.LogWarning("目录数量({Count})超过系统限制({Limit})，启用混合模式", _sources.Count, _inotifyLimit);

        // 将所有已监控目录标记为活跃
        foreach (var path in _sources.Values)
        {
            MarkDirectoryActive(path);
        }

        // 启动非活跃目录扫描器
        _idleScanner?.Change(TimeSpan.FromSeconds(IdleScanIntervalSeconds), TimeSpan.FromSeconds(IdleScanIntervalSeconds));
    }

    /// <summary>
    /// 设置 inotify 限制（用于测试或自定义配置）
    /// </summary>
    public void SetInotifyLimit(int limit)
    {
        _inotifyLimit = limit;
    }

    public Task StopAsync()
    {
        lock (_lock)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
            _isRunning = false;
        }

        _logger?.LogInformation("监控已停止");
        return Task.CompletedTask;
    }

    public FileMonitorStatus GetStatus()
    {
        lock (_lock)
        {
            var totalFiles = 0;
            foreach (var path in _sources.Values)
            {
                if (Directory.Exists(path))
                {
                    totalFiles += Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories).Count();
                }
            }

            return new FileMonitorStatus
            {
                IsRunning = _isRunning,
                WatchedDirectories = _watchers.Count,
                TotalFiles = totalFiles,
                LastChange = _lastChange,
                ChangesDetected = _changesDetected,
                IndexingQueueSize = _debounceTimers.Count
            };
        }
    }

    private void CreateWatcher(string path, string name)
    {
        if (!Directory.Exists(path))
        {
            _logger?.LogWarning("目录不存在: {Path}", path);
            return;
        }

        var watcher = new FileSystemWatcher(path)
        {
            Filter = "*.md",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };

        watcher.Changed += (s, e) => OnFileChanged(e.FullPath, name, ChangeType.Modified);
        watcher.Created += (s, e) => OnFileChanged(e.FullPath, name, ChangeType.Created);
        watcher.Deleted += (s, e) => OnFileChanged(e.FullPath, name, ChangeType.Deleted);
        watcher.Renamed += (s, e) => OnFileRenamed(e.OldFullPath, e.FullPath, name);
        watcher.Error += (s, e) => _logger?.LogError(e.GetException(), "文件监控错误");

        watcher.EnableRaisingEvents = true;
        _watchers[name] = watcher;

        _logger?.LogInformation("开始监控: {Path}", path);
    }

    /// <summary>
    /// Per-file 独立防抖：每个文件有独立的定时器，避免全局阻塞
    /// </summary>
    private void Debounce(string filePath, string source, ChangeType changeType)
    {
        _debounceTimers.AddOrUpdate(
            filePath,
            _ => new Timer(_ => Flush(filePath, source, changeType), null, _debounceMs, Timeout.Infinite),
            (_, existingTimer) =>
            {
                // 重置定时器
                existingTimer.Change(_debounceMs, Timeout.Infinite);
                return existingTimer;
            }
        );

        _logger?.LogDebug("防抖触发: {FileName} ({ChangeType})", Path.GetFileName(filePath), changeType);
    }

    /// <summary>
    /// 防抖到期后触发实际事件处理
    /// </summary>
    private void Flush(string filePath, string source, ChangeType changeType)
    {
        // 移除定时器
        if (_debounceTimers.TryRemove(filePath, out var timer))
        {
            timer.Dispose();
        }

        // 更新统计信息
        lock (_lock)
        {
            _changesDetected++;
            _lastChange = DateTime.UtcNow;
        }

        // 根据文件存在状态判定最终类型（重命名事件保持原类型）
        var finalChangeType = changeType == ChangeType.Renamed
            ? changeType
            : (File.Exists(filePath) ? ChangeType.Modified : ChangeType.Deleted);

        // 获取旧路径（仅重命名事件）
        string? oldFilePath = null;
        if (finalChangeType == ChangeType.Renamed && _oldPaths.TryRemove(filePath, out var oldPath))
        {
            oldFilePath = oldPath;
        }

        FileChanged?.Invoke(this, new FileChangeEventArgs
        {
            FilePath = filePath,
            OldFilePath = oldFilePath,
            Source = source,
            ChangeType = finalChangeType,
            Timestamp = DateTime.UtcNow
        });

        _logger?.LogDebug("事件触发: {FileName} ({ChangeType})", Path.GetFileName(filePath), finalChangeType);
    }

    /// <summary>
    /// 安全读取文件内容：支持重试和共享读取模式，减少文件锁定冲突
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>文件内容，失败返回 null</returns>
    public async Task<string?> SafeReadFileAsync(string path, CancellationToken ct = default)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                // 使用 FileShare.ReadWrite 允许其他进程同时读写，减少锁定冲突
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return await sr.ReadToEndAsync(ct);
            }
            catch (IOException) when (i < 2)
            {
                // 文件被占用，等待后重试
                _logger?.LogWarning("文件被占用，等待重试 ({Attempt}/3): {FileName}", i + 1, Path.GetFileName(path));
                await Task.Delay(100 * (i + 1), ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "读取文件失败: {FileName}", Path.GetFileName(path));
                return null;
            }
        }
        return null;
    }

    private void OnFileChanged(string filePath, string source, ChangeType changeType)
    {
        // 标记目录为活跃状态
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            MarkDirectoryActive(dir);
        }

        // 使用 per-file 独立防抖
        Debounce(filePath, source, changeType);

        _logger?.LogDebug("文件变动: {FileName} ({ChangeType})", Path.GetFileName(filePath), changeType);
    }

    private void OnFileRenamed(string oldPath, string newPath, string source)
    {
        // 取消旧路径的防抖定时器
        if (_debounceTimers.TryRemove(oldPath, out var oldTimer))
        {
            oldTimer.Dispose();
        }

        // 存储旧路径供重命名事件使用
        _oldPaths[newPath] = oldPath;

        // 使用 per-file 独立防抖
        Debounce(newPath, source, ChangeType.Renamed);

        _logger?.LogInformation("文件重命名: {OldName} -> {NewName}", Path.GetFileName(oldPath), Path.GetFileName(newPath));
    }

    private void UpdateStatus(object? state)
    {
        var status = GetStatus();

        if (status.ChangesDetected > 0)
        {
            _logger?.LogInformation("状态: 监控 {WatchedDirectories} 个目录, {TotalFiles} 个文件, {ChangesDetected} 次变动",
                status.WatchedDirectories, status.TotalFiles, status.ChangesDetected);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopAsync().GetAwaiter().GetResult();
            
            // 清理所有 per-file 防抖定时器
            foreach (var timer in _debounceTimers.Values)
            {
                timer.Dispose();
            }
            _debounceTimers.Clear();
            
            _statusTimer?.Dispose();
            _idleScanner?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// 并行分区扫描：利用多核加速大目录扫描
    /// </summary>
    public static IEnumerable<string> ParallelScanFiles(string root, string pattern = "*.md")
    {
        if (!Directory.Exists(root))
            return Enumerable.Empty<string>();
        
        var topDirs = Directory.GetDirectories(root);
        
        if (topDirs.Length == 0)
        {
            // 无子目录，直接扫描
            return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly);
        }
        
        // 并行扫描第一层子目录，限制并发度避免线程爆炸
        return topDirs
            .AsParallel()
            .WithDegreeOfParallelism(Math.Min(Environment.ProcessorCount, 8))
            .SelectMany(dir => Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).AsParallel())
            .Distinct();
    }
}

/// <summary>
/// 自动索引进度事件参数
/// </summary>
public class AutoIndexProgressEventArgs : EventArgs
{
    public string FilePath { get; set; } = string.Empty;
    public ChangeType ChangeType { get; set; }
    public int ChunksIndexed { get; set; }
    public int VectorsGenerated { get; set; }
    public long DurationMs { get; set; }
}
