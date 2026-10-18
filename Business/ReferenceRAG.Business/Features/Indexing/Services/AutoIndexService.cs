using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Services;

/// <summary>
/// 自动索引服务 — 监听 FileMonitorService 事件，通过 FileIndexPipeline 处理单文件变更。
/// 支持自适应静默窗口防抖与突发聚批处理（Adaptive Burst-Throttle Strategy），
/// 避免高频连续保存反复打断 30 秒空闲显存释放倒计时。
/// </summary>
public class AutoIndexService : IHostedService, IDisposable
{
    private readonly IFileMonitorService _fileMonitor;
    private readonly IFileIndexPipeline _pipeline;
    private readonly IFileProcessingGuard _guard;
    private readonly ConfigManager _configManager;
    private readonly ILogger<AutoIndexService>? _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, PendingChange> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _processTimer;
    private int _isProcessing;
    private bool _disposed;

    /// <summary>
    /// 静默窗口：文件修改后需等待无新写入的时间（默认 2 秒）
    /// </summary>
    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 最大延迟：持续写入时保证最终被处理的最长等待时限（默认 10 秒）
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(10);

    public int PendingCount
    {
        get
        {
            lock (_lock) return _pendingChanges.Count;
        }
    }

    public AutoIndexService(
        IFileMonitorService fileMonitor,
        IFileIndexPipeline pipeline,
        IFileProcessingGuard guard,
        ConfigManager configManager,
        ILogger<AutoIndexService>? logger = null)
    {
        _fileMonitor = fileMonitor;
        _pipeline = pipeline;
        _guard = guard;
        _configManager = configManager;
        _logger = logger;
        _processTimer = new Timer(ProcessQueue, null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _fileMonitor.FileChanged += OnFileChanged;

        var config = _configManager.Load();
        if (!config.Indexing.AutoIndexEnabled)
        {
            _logger?.LogInformation("自动索引已在配置中禁用");
            return Task.CompletedTask;
        }
        foreach (var source in config.Sources.Where(s => s.Enabled))
            _fileMonitor.AddSource(source.Path, source.Name, source.FilePatterns);

        _ = _fileMonitor.StartAsync(cancellationToken);
        _processTimer.Change(500, 500);

        _logger?.LogInformation("自动索引服务已启动，监控 {Count} 个源", config.Sources.Count(s => s.Enabled));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _processTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _fileMonitor.FileChanged -= OnFileChanged;
        await _fileMonitor.StopAsync();
        _logger?.LogInformation("自动索引服务已停止");
    }

    internal void OnFileChanged(object? sender, FileChangeEventArgs e)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var path = e.FilePath;

            // 1. 特殊处理重命名：如果存在旧路径的待处理任务，清理并继承原始链路
            if (e.ChangeType == ChangeType.Renamed && !string.IsNullOrEmpty(e.OldFilePath))
            {
                if (_pendingChanges.TryGetValue(e.OldFilePath, out var oldPending))
                {
                    _pendingChanges.Remove(e.OldFilePath);
                    if (!string.IsNullOrEmpty(oldPending.Change.OldFilePath))
                    {
                        e = new FileChangeEventArgs
                        {
                            FilePath = e.FilePath,
                            OldFilePath = oldPending.Change.OldFilePath,
                            ChangeType = ChangeType.Renamed,
                            Timestamp = e.Timestamp,
                            Source = e.Source
                        };
                    }
                }
            }

            // 2. 特殊处理删除：如果之前刚 Created 尚未落地索引，直接对冲抹平
            if (e.ChangeType == ChangeType.Deleted)
            {
                if (_pendingChanges.TryGetValue(path, out var existing) && existing.Change.ChangeType == ChangeType.Created)
                {
                    _pendingChanges.Remove(path);
                    _logger?.LogDebug("新建文件未及索引即被删除，已对冲消除事件: {FileName}", Path.GetFileName(path));
                    return;
                }
            }

            // 3. 聚批与状态演化
            if (_pendingChanges.TryGetValue(path, out var pending))
            {
                if (pending.Change.ChangeType == ChangeType.Renamed && e.ChangeType == ChangeType.Modified)
                {
                    e = new FileChangeEventArgs
                    {
                        FilePath = e.FilePath,
                        OldFilePath = pending.Change.OldFilePath,
                        ChangeType = ChangeType.Renamed,
                        Timestamp = e.Timestamp,
                        Source = e.Source
                    };
                }

                pending.Change = e;
                pending.LastSeenUtc = now;
                _logger?.LogDebug("静默防抖更新: {FileName} ({ChangeType})", Path.GetFileName(path), e.ChangeType);
            }
            else
            {
                _pendingChanges[path] = new PendingChange
                {
                    Change = e,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                };
                _logger?.LogDebug("挂起变更入池: {FileName} ({ChangeType})", Path.GetFileName(path), e.ChangeType);
            }
        }
    }

    private async void ProcessQueue(object? state)
    {
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0) return;

        try
        {
            await ProcessReadyChangesInternalAsync(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "AutoIndexService 队列处理发生未捕获异常");
        }
        finally
        {
            Volatile.Write(ref _isProcessing, 0);
        }
    }

    internal async Task<int> ProcessReadyChangesInternalAsync(DateTime now)
    {
        List<FileChangeEventArgs> readyChanges;
        lock (_lock)
        {
            if (_pendingChanges.Count == 0) return 0;

            var readyKeys = new List<string>();
            readyChanges = new List<FileChangeEventArgs>();
            foreach (var kvp in _pendingChanges)
            {
                var pending = kvp.Value;
                if (now - pending.LastSeenUtc >= QuietPeriod || now - pending.FirstSeenUtc >= MaxDelay)
                {
                    readyChanges.Add(pending.Change);
                    readyKeys.Add(kvp.Key);
                }
            }

            foreach (var key in readyKeys)
            {
                _pendingChanges.Remove(key);
            }
        }

        if (readyChanges.Count == 0) return 0;

        _logger?.LogDebug("自适应静默防抖就绪，开始突发批次处理 {Count} 个文件", readyChanges.Count);

        // 突发批次连续处理全部就绪文件，完成后 GPU 立即进入连续静默倒计时，显存能够正常释放
        foreach (var change in readyChanges)
        {
            try
            {
                await ProcessChangeAsync(change);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "处理文件变更失败: {FilePath}", change.FilePath);
            }
        }

        return readyChanges.Count;
    }

    private async Task ProcessChangeAsync(FileChangeEventArgs change)
    {
        _logger?.LogInformation("开始处理: {FileName} ({ChangeType})",
            Path.GetFileName(change.FilePath), change.ChangeType);

        if (change.ChangeType == ChangeType.Deleted)
        {
            if (!_guard.TryAcquire(change.FilePath))
            {
                Requeue(change);
                _logger?.LogDebug("文件正在处理中，删除事件已重新入队: {FileName}", Path.GetFileName(change.FilePath));
                return;
            }
            try
            {
                var deleted = await _pipeline.DeleteFileByPathAsync(change.FilePath);
                if (deleted)
                    _logger?.LogInformation("已删除索引: {FileName}", Path.GetFileName(change.FilePath));
            }
            finally { _guard.Release(change.FilePath); }
            return;
        }

        // Modified / Created / Renamed
        var targetPath = change.FilePath;
        if (!_guard.TryAcquire(targetPath))
        {
            Requeue(change);
            _logger?.LogDebug("文件正在处理中，变更事件已重新入队: {FileName}", Path.GetFileName(targetPath));
            return;
        }

        try
        {
            var config = _configManager.Load();
            var sources = config.Sources.Where(s => s.Enabled).ToList();

            var indexed = await _pipeline.IndexSingleAsync(
                targetPath,
                sources,
                oldFilePath: change.OldFilePath,   // Bug B: 重命名时传旧路径，pipeline 内部清理
                ct: CancellationToken.None);

            if (indexed)
                _logger?.LogInformation("自动索引完成: {FileName}", Path.GetFileName(targetPath));
        }
        finally
        {
            _guard.Release(targetPath);
        }
    }

    private void Requeue(FileChangeEventArgs change)
    {
        lock (_lock)
        {
            if (!_pendingChanges.ContainsKey(change.FilePath))
            {
                _pendingChanges[change.FilePath] = new PendingChange
                {
                    Change = change,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow
                };
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _processTimer?.Dispose();
            _disposed = true;
        }
    }

    private sealed class PendingChange
    {
        public FileChangeEventArgs Change { get; set; } = null!;
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }
}

public class AutoIndexProgressEventArgs : EventArgs
{
    public string FilePath { get; set; } = string.Empty;
    public ChangeType ChangeType { get; set; }
    public int ChunksIndexed { get; set; }
    public int VectorsGenerated { get; set; }
    public long DurationMs { get; set; }
}
