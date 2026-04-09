using Microsoft.Extensions.Logging;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services;

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
    private readonly Dictionary<string, FileSystemWatcher> _watchers;
    private readonly Dictionary<string, string> _sources;
    private readonly Dictionary<string, PendingChange> _pendingChanges;
    private readonly Timer _debounceTimer;
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
        _pendingChanges = new Dictionary<string, PendingChange>();
        _debounceMs = debounceMs;
        _logger = logger;

        _debounceTimer = new Timer(ProcessPendingChanges, null, debounceMs, debounceMs);
        _statusTimer = new Timer(UpdateStatus, null, 5000, 5000);
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
        return Task.CompletedTask;
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
                IndexingQueueSize = _pendingChanges.Count
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

    private void OnFileChanged(string filePath, string source, ChangeType changeType)
    {
        lock (_lock)
        {
            _pendingChanges[filePath] = new PendingChange
            {
                FilePath = filePath,
                ChangeType = changeType,
                Timestamp = DateTime.UtcNow,
                Source = source
            };
            _changesDetected++;
            _lastChange = DateTime.UtcNow;
        }

        _logger?.LogDebug("文件变动: {FileName} ({ChangeType})", Path.GetFileName(filePath), changeType);
    }

    private void OnFileRenamed(string oldPath, string newPath, string source)
    {
        lock (_lock)
        {
            // 移除旧路径的待处理变更（如果存在）
            _pendingChanges.Remove(oldPath);

            // 添加重命名事件
            _pendingChanges[newPath] = new PendingChange
            {
                FilePath = newPath,
                OldFilePath = oldPath,
                ChangeType = ChangeType.Renamed,
                Timestamp = DateTime.UtcNow,
                Source = source
            };
            _changesDetected++;
            _lastChange = DateTime.UtcNow;
        }

        _logger?.LogInformation("文件重命名: {OldName} -> {NewName}", Path.GetFileName(oldPath), Path.GetFileName(newPath));
    }

    private void ProcessPendingChanges(object? state)
    {
        List<PendingChange> changesToProcess;

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            changesToProcess = _pendingChanges
                .Where(kvp => (now - kvp.Value.Timestamp).TotalMilliseconds >= _debounceMs)
                .Select(kvp => kvp.Value)
                .ToList();

            foreach (var change in changesToProcess)
            {
                _pendingChanges.Remove(change.FilePath);
            }
        }

        foreach (var change in changesToProcess)
        {
            // 重命名事件：保持原始 ChangeType，不重新判定
            if (change.ChangeType == ChangeType.Renamed)
            {
                FileChanged?.Invoke(this, new FileChangeEventArgs
                {
                    FilePath = change.FilePath,
                    OldFilePath = change.OldFilePath,
                    Source = change.Source ?? "Unknown",
                    ChangeType = ChangeType.Renamed,
                    Timestamp = change.Timestamp
                });
                continue;
            }

            // 其他事件：根据文件存在状态判定最终类型
            var finalChangeType = File.Exists(change.FilePath)
                ? ChangeType.Modified
                : ChangeType.Deleted;

            FileChanged?.Invoke(this, new FileChangeEventArgs
            {
                FilePath = change.FilePath,
                Source = change.Source ?? "Unknown",
                ChangeType = finalChangeType,
                Timestamp = change.Timestamp
            });
        }
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
            _debounceTimer?.Dispose();
            _statusTimer?.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// 自动索引服务
/// </summary>
public class AutoIndexService : IDisposable
{
    private readonly IFileMonitorService _fileMonitor;
    private readonly IIndexingService _indexingService;
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly MarkdownChunker _chunker;
    private readonly ContentHashDetector _hashDetector;
    private readonly ILogger<AutoIndexService>? _logger;
    private readonly Queue<FileChangeEventArgs> _indexQueue;
    private readonly Timer _processTimer;
    private bool _isProcessing;
    private bool _disposed;

    public event EventHandler<AutoIndexProgressEventArgs>? Progress;

    public AutoIndexService(
        IFileMonitorService fileMonitor,
        IIndexingService indexingService,
        IVectorStore vectorStore,
        IEmbeddingService embeddingService,
        MarkdownChunker chunker,
        ContentHashDetector hashDetector,
        ILogger<AutoIndexService>? logger = null)
    {
        _fileMonitor = fileMonitor;
        _indexingService = indexingService;
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _chunker = chunker;
        _hashDetector = hashDetector;
        _logger = logger;
        _indexQueue = new Queue<FileChangeEventArgs>();

        _processTimer = new Timer(ProcessQueue, null, 1000, 1000);

        _fileMonitor.FileChanged += OnFileChanged;
    }

    private void OnFileChanged(object? sender, FileChangeEventArgs e)
    {
        lock (_indexQueue)
        {
            _indexQueue.Enqueue(e);
        }

        _logger?.LogDebug("队列添加: {FileName} ({ChangeType})", Path.GetFileName(e.FilePath), e.ChangeType);
    }

    private async void ProcessQueue(object? state)
    {
        if (_isProcessing) return;

        FileChangeEventArgs? change = null;
        lock (_indexQueue)
        {
            if (_indexQueue.Count > 0)
            {
                change = _indexQueue.Dequeue();
            }
        }

        if (change == null) return;

        _isProcessing = true;
        try
        {
            await ProcessChangeAsync(change);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "处理失败");
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async Task ProcessChangeAsync(FileChangeEventArgs change)
    {
        _logger?.LogInformation("开始处理: {FileName}", Path.GetFileName(change.FilePath));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (change.ChangeType == ChangeType.Deleted)
            {
                // 删除索引
                var file = await _vectorStore.GetFileByPathAsync(change.FilePath);
                if (file != null)
                {
                    await _vectorStore.DeleteFileAsync(file.Id);
                    _logger?.LogInformation("已删除索引: {FileName}", Path.GetFileName(change.FilePath));
                }
            }
            else
            {
                // 检查内容是否变化
                var newHash = await _hashDetector.ComputeFileFingerprintAsync(change.FilePath);
                var existingFile = await _vectorStore.GetFileByPathAsync(change.FilePath);

                if (existingFile != null && existingFile.ContentHash == newHash)
                {
                    _logger?.LogDebug("内容未变化，跳过: {FileName}", Path.GetFileName(change.FilePath));
                    return;
                }

                // 读取文件内容
                var content = await File.ReadAllTextAsync(change.FilePath);

                // 创建文件记录
                var fileRecord = new FileRecord
                {
                    Id = existingFile?.Id ?? Guid.NewGuid().ToString(),
                    Path = change.FilePath,
                    FileName = Path.GetFileName(change.FilePath),
                    ParentFolder = Path.GetDirectoryName(change.FilePath),
                    Source = change.Source ?? "Unknown",
                    ContentHash = newHash,
                    ContentLength = content.Length,
                    Title = ExtractTitle(content) ?? Path.GetFileNameWithoutExtension(change.FilePath),
                    ChunkCount = 0,
                    TotalTokens = 0,
                    ModifiedAt = File.GetLastWriteTime(change.FilePath)
                };

                // 分段
                var chunks = _chunker.Chunk(content, fileRecord);

                // 设置 chunk 的 FileId 和 Source
                foreach (var chunk in chunks)
                {
                    chunk.FileId = fileRecord.Id;
                    chunk.Source = fileRecord.Source;
                }

                fileRecord.ChunkCount = chunks.Count;
                fileRecord.TotalTokens = chunks.Sum(c => c.TokenCount);

                // 生成向量
                var vectors = new List<VectorRecord>();
                var batchSize = 16;

                for (int i = 0; i < chunks.Count; i += batchSize)
                {
                    var batch = chunks.Skip(i).Take(batchSize).ToList();
                    var embeddings = await _embeddingService.EncodeBatchAsync(batch.Select(c => c.Content), EmbeddingMode.Document);

                    for (int j = 0; j < batch.Count; j++)
                    {
                        vectors.Add(new VectorRecord
                        {
                            Id = Guid.NewGuid().ToString(),
                            ChunkId = batch[j].Id,
                            FileId = batch[j].FileId,
                            Vector = embeddings[j],
                            Dimension = embeddings[j].Length,
                            Source = batch[j].Source,
                            ModelName = _embeddingService.ModelName
                        });
                    }
                }

                // 保存到存储
                await _vectorStore.UpsertFileAsync(fileRecord);
                await _vectorStore.UpsertChunksAsync(chunks);
                await _vectorStore.UpsertVectorsAsync(vectors);

                sw.Stop();

                _logger?.LogInformation("索引完成: {FileName} ({ChunksCount} 分段, {VectorsCount} 向量, {ElapsedMilliseconds}ms)",
                    Path.GetFileName(change.FilePath), chunks.Count, vectors.Count, sw.ElapsedMilliseconds);

                Progress?.Invoke(this, new AutoIndexProgressEventArgs
                {
                    FilePath = change.FilePath,
                    ChangeType = change.ChangeType,
                    ChunksIndexed = chunks.Count,
                    VectorsGenerated = vectors.Count,
                    DurationMs = sw.ElapsedMilliseconds
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "索引失败: {FileName}", Path.GetFileName(change.FilePath));
        }
    }

    private string? ExtractTitle(string content)
    {
        var lines = content.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# "))
            {
                return trimmed[2..].Trim();
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _fileMonitor.FileChanged -= OnFileChanged;
            _processTimer?.Dispose();
            _disposed = true;
        }
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

/// <summary>
/// 索引服务接口
/// </summary>
public interface IIndexingService
{
    Task<IndexingResult> IndexAsync(string source, bool force = false, CancellationToken cancellationToken = default);
}
