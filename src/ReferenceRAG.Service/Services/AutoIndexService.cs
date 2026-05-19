using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Services;

/// <summary>
/// 自动索引服务 — 监听 FileMonitorService 事件，通过 FileIndexPipeline 处理单文件变更。
/// 修复: 重命名旧路径清理（Bug B）、跨服务互斥（Bug D）。
/// </summary>
public class AutoIndexService : IHostedService, IDisposable
{
    private readonly IFileMonitorService _fileMonitor;
    private readonly IFileIndexPipeline _pipeline;
    private readonly FileProcessingGuard _guard;
    private readonly ConfigManager _configManager;
    private readonly ILogger<AutoIndexService>? _logger;

    private readonly Queue<FileChangeEventArgs> _indexQueue = new();
    private readonly Timer _processTimer;
    private bool _isProcessing;
    private bool _disposed;

    public AutoIndexService(
        IFileMonitorService fileMonitor,
        IFileIndexPipeline pipeline,
        FileProcessingGuard guard,
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
        foreach (var source in config.Sources.Where(s => s.Enabled))
            _fileMonitor.AddSource(source.Path, source.Name, source.FilePatterns);

        _ = _fileMonitor.StartAsync(cancellationToken);
        _processTimer.Change(1000, 1000);

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

    private void OnFileChanged(object? sender, FileChangeEventArgs e)
    {
        lock (_indexQueue)
            _indexQueue.Enqueue(e);
        _logger?.LogDebug("队列添加: {FileName} ({ChangeType})", Path.GetFileName(e.FilePath), e.ChangeType);
    }

    private async void ProcessQueue(object? state)
    {
        if (_isProcessing) return;

        FileChangeEventArgs? change;
        lock (_indexQueue)
        {
            if (_indexQueue.Count == 0) return;
            change = _indexQueue.Dequeue();
        }

        _isProcessing = true;
        try
        {
            await ProcessChangeAsync(change);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "处理文件变更失败: {FilePath}", change.FilePath);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async Task ProcessChangeAsync(FileChangeEventArgs change)
    {
        _logger?.LogInformation("开始处理: {FileName} ({ChangeType})",
            Path.GetFileName(change.FilePath), change.ChangeType);

        if (change.ChangeType == ChangeType.Deleted)
        {
            if (!_guard.TryAcquire(change.FilePath))
            {
                _logger?.LogDebug("文件正在处理中，跳过删除: {FileName}", Path.GetFileName(change.FilePath));
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
            _logger?.LogDebug("文件正在处理中，跳过: {FileName}", Path.GetFileName(targetPath));
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _processTimer?.Dispose();
            _disposed = true;
        }
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
