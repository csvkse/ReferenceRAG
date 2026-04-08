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
/// 文件监控服务实现
/// </summary>
public class FileMonitorService : IFileMonitorService, IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _watchers;
    private readonly Dictionary<string, string> _sources;
    private readonly Dictionary<string, DateTime> _pendingChanges;
    private readonly Timer _debounceTimer;
    private readonly Timer _statusTimer;
    private readonly int _debounceMs;
    private readonly object _lock = new();
    private bool _disposed;
    private bool _isRunning;
    private int _changesDetected;
    private DateTime? _lastChange;

    public event EventHandler<FileChangeEventArgs>? FileChanged;

    public FileMonitorService(int debounceMs = 500)
    {
        _watchers = new Dictionary<string, FileSystemWatcher>();
        _sources = new Dictionary<string, string>();
        _pendingChanges = new Dictionary<string, DateTime>();
        _debounceMs = debounceMs;
        
        _debounceTimer = new Timer(ProcessPendingChanges, null, debounceMs, debounceMs);
        _statusTimer = new Timer(UpdateStatus, null, 5000, 5000);
    }

    public void AddSource(string path, string name)
    {
        lock (_lock)
        {
            _sources[name] = path;
            
            if (_isRunning)
            {
                CreateWatcher(path, name);
            }
        }
        
        Console.WriteLine($"[FileMonitor] 添加监控源: {name} ({path})");
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
        
        Console.WriteLine($"[FileMonitor] 移除监控源: {name}");
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                Console.WriteLine("[FileMonitor] 监控已在运行");
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

        Console.WriteLine($"[FileMonitor] 监控已启动，监控 {_watchers.Count} 个目录");
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

        Console.WriteLine("[FileMonitor] 监控已停止");
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
            Console.WriteLine($"[FileMonitor] 目录不存在: {path}");
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
        watcher.Error += (s, e) => Console.WriteLine($"[FileMonitor] 错误: {e.GetException()?.Message}");

        watcher.EnableRaisingEvents = true;
        _watchers[name] = watcher;

        Console.WriteLine($"[FileMonitor] 开始监控: {path}");
    }

    private void OnFileChanged(string filePath, string source, ChangeType changeType)
    {
        lock (_lock)
        {
            _pendingChanges[filePath] = DateTime.UtcNow;
            _changesDetected++;
            _lastChange = DateTime.UtcNow;
        }

#if DEBUG
        Console.WriteLine($"[FileMonitor] 文件变动: {Path.GetFileName(filePath)} ({changeType})");
#endif
    }

    private void OnFileRenamed(string oldPath, string newPath, string source)
    {
        lock (_lock)
        {
            _pendingChanges[newPath] = DateTime.UtcNow;
            _changesDetected++;
            _lastChange = DateTime.UtcNow;
        }

        Console.WriteLine($"[FileMonitor] 文件重命名: {Path.GetFileName(oldPath)} -> {Path.GetFileName(newPath)}");
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

        foreach (var (filePath, timestamp) in changesToProcess)
        {
            var changeType = File.Exists(filePath) ? ChangeType.Modified : ChangeType.Deleted;
            
            // 查找对应的源
            string? source = null;
            lock (_lock)
            {
                foreach (var (name, path) in _sources)
                {
                    if (filePath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                    {
                        source = name;
                        break;
                    }
                }
            }

            FileChanged?.Invoke(this, new FileChangeEventArgs
            {
                FilePath = filePath,
                Source = source ?? "Unknown",
                ChangeType = changeType,
                Timestamp = timestamp
            });
        }
    }

    private void UpdateStatus(object? state)
    {
        var status = GetStatus();
        
        if (status.ChangesDetected > 0)
        {
            Console.WriteLine($"[FileMonitor] 状态: 监控 {status.WatchedDirectories} 个目录, {status.TotalFiles} 个文件, {status.ChangesDetected} 次变动");
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
        ContentHashDetector hashDetector)
    {
        _fileMonitor = fileMonitor;
        _indexingService = indexingService;
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _chunker = chunker;
        _hashDetector = hashDetector;
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

        Console.WriteLine($"[AutoIndex] 队列添加: {Path.GetFileName(e.FilePath)} ({e.ChangeType})");
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
            Console.WriteLine($"[AutoIndex] 处理失败: {ex.Message}");
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async Task ProcessChangeAsync(FileChangeEventArgs change)
    {
        Console.WriteLine($"[AutoIndex] 开始处理: {Path.GetFileName(change.FilePath)}");

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
                    Console.WriteLine($"[AutoIndex] 已删除索引: {Path.GetFileName(change.FilePath)}");
                }
            }
            else
            {
                // 检查内容是否变化
                var newHash = await _hashDetector.ComputeFileFingerprintAsync(change.FilePath);
                var existingFile = await _vectorStore.GetFileByPathAsync(change.FilePath);

                if (existingFile != null && existingFile.ContentHash == newHash)
                {
                    Console.WriteLine($"[AutoIndex] 内容未变化，跳过: {Path.GetFileName(change.FilePath)}");
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
                    var embeddings = await _embeddingService.EncodeBatchAsync(batch.Select(c => c.Content));

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

                Console.WriteLine($"[AutoIndex] 索引完成: {Path.GetFileName(change.FilePath)} " +
                    $"({chunks.Count} 分段, {vectors.Count} 向量, {sw.ElapsedMilliseconds}ms)");

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
            Console.WriteLine($"[AutoIndex] 索引失败: {Path.GetFileName(change.FilePath)} - {ex.Message}");
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
