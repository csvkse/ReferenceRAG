using System.Security.Cryptography;
using System.Text;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 文件变动检测服务 - FileSystemWatcher + 防抖
/// </summary>
public class FileChangeDetector : IFileChangeDetector, IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly Dictionary<string, DateTime> _pendingChanges;
    private readonly Timer _debounceTimer;
    private readonly int _debounceMs;
    private readonly object _lock = new();
    private bool _disposed;

    public event EventHandler<FileChangeEventArgs>? FileChanged;
    public event EventHandler<FileChangeEventArgs>? FileDeleted;
    public event EventHandler<FileChangeEventArgs>? FileRenamed;

    public FileChangeDetector(string watchPath, int debounceMs = 500)
    {
        _pendingChanges = new Dictionary<string, DateTime>();
        _debounceMs = debounceMs;
        _debounceTimer = new Timer(ProcessPendingChanges, null, debounceMs, debounceMs);

        // 转换路径格式（WSL 环境下将 Windows 路径转为 /mnt/x/ 格式）
        var normalizedPath = PathUtility.NormalizePath(watchPath);
        if (Directory.Exists(normalizedPath))
        {
            _watcher = new FileSystemWatcher(normalizedPath)
            {
                Filter = "*.md",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileCreated;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnWatcherError;

            _watcher.EnableRaisingEvents = true;
        }
        else
        {
            Console.WriteLine($"[FileChangeDetector] 目录不存在或无法访问: {watchPath} (normalized: {normalizedPath})");
        }
    }

    /// <summary>
    /// 计算文件内容哈希
    /// </summary>
    public async Task<string> ComputeHashAsync(string filePath)
    {
        return await ComputeContentHashAsync(filePath);
    }

    /// <summary>
    /// 获取文件变动列表
    /// </summary>
    public async Task<List<FileChangeEventArgs>> GetChangesAsync(string directory, DateTime since)
    {
        await Task.CompletedTask; // 避免编译器警告
        var changes = new List<FileChangeEventArgs>();

        var normalizedPath = PathUtility.NormalizePath(directory);
        if (!Directory.Exists(normalizedPath))
            return changes;

        var files = Directory.EnumerateFiles(normalizedPath, "*.md", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var lastWrite = File.GetLastWriteTime(file);
            if (lastWrite > since)
            {
                changes.Add(new FileChangeEventArgs
                {
                    FilePath = file,
                    ChangeType = ChangeType.Modified,
                    Timestamp = lastWrite
                });
            }
        }

        return changes;
    }

    /// <summary>
    /// 计算文件内容哈希
    /// </summary>
    public async Task<string> ComputeContentHashAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return string.Empty;

        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// 检测文件是否实际改变（通过内容哈希）
    /// </summary>
    public async Task<bool> HasContentChangedAsync(string filePath, string oldHash)
    {
        var newHash = await ComputeContentHashAsync(filePath);
        return !string.Equals(oldHash, newHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 扫描目录获取所有 Markdown 文件
    /// </summary>
    public IEnumerable<string> ScanMarkdownFiles(string directory)
    {
        var normalizedPath = PathUtility.NormalizePath(directory);
        if (!Directory.Exists(normalizedPath))
            return Enumerable.Empty<string>();

        return Directory.EnumerateFiles(normalizedPath, "*.md", SearchOption.AllDirectories);
    }

    /// <summary>
    /// 检测移动/重命名（通过内容哈希匹配）
    /// </summary>
    public async Task<FileMoveResult?> DetectMoveAsync(
        string oldPath, 
        string oldHash,
        IEnumerable<string> candidatePaths)
    {
        foreach (var candidatePath in candidatePaths)
        {
            if (!File.Exists(candidatePath)) continue;
            if (candidatePath == oldPath) continue;

            var candidateHash = await ComputeContentHashAsync(candidatePath);
            if (string.Equals(oldHash, candidateHash, StringComparison.OrdinalIgnoreCase))
            {
                return new FileMoveResult
                {
                    OldPath = oldPath,
                    NewPath = candidatePath,
                    ContentHash = candidateHash
                };
            }
        }

        return null;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        QueueChange(e.FullPath, ChangeType.Modified);
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        QueueChange(e.FullPath, ChangeType.Created);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        FileDeleted?.Invoke(this, new FileChangeEventArgs
        {
            FilePath = e.FullPath,
            ChangeType = ChangeType.Deleted
        });
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        FileRenamed?.Invoke(this, new FileChangeEventArgs
        {
            FilePath = e.FullPath,
            OldFilePath = e.OldFullPath,
            ChangeType = ChangeType.Renamed
        });
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // 记录错误，可以触发重新扫描
        Console.Error.WriteLine($"FileSystemWatcher error: {e.GetException()?.Message}");
    }

    private void QueueChange(string filePath, ChangeType changeType)
    {
        lock (_lock)
        {
            _pendingChanges[filePath] = DateTime.UtcNow;
        }
    }

    private void ProcessPendingChanges(object? state)
    {
        List<KeyValuePair<string, DateTime>> changesToProcess;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            changesToProcess = _pendingChanges
                .Where(kvp => (now - kvp.Value).TotalMilliseconds >= _debounceMs)
                .ToList();

            foreach (var kvp in changesToProcess)
            {
                _pendingChanges.Remove(kvp.Key);
            }
        }

        foreach (var kvp in changesToProcess)
        {
            if (File.Exists(kvp.Key))
            {
                FileChanged?.Invoke(this, new FileChangeEventArgs
                {
                    FilePath = kvp.Key,
                    ChangeType = ChangeType.Modified
                });
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _debounceTimer?.Dispose();
            _watcher?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// 文件移动结果
/// </summary>
public class FileMoveResult
{
    public string OldPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
}
