using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Service.Services;

/// <summary>
/// 自动索引服务 - 监听文件变更并自动更新向量索引
/// </summary>
public class AutoIndexService : IHostedService, IDisposable
{
    private readonly IFileMonitorService _fileMonitor;
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingService _embeddingService;
    private readonly IMarkdownChunker _chunker;
    private readonly ContentHashDetector _hashDetector;
    private readonly ConfigManager _configManager;
    private readonly ILogger<AutoIndexService>? _logger;
    private readonly Queue<FileChangeEventArgs> _indexQueue;
    private readonly Timer _processTimer;
    private bool _isProcessing;
    private bool _disposed;

    public event EventHandler<AutoIndexProgressEventArgs>? Progress;

    public AutoIndexService(
        IFileMonitorService fileMonitor,
        IVectorStore vectorStore,
        IEmbeddingService embeddingService,
        IMarkdownChunker chunker,
        ContentHashDetector hashDetector,
        ConfigManager configManager,
        ILogger<AutoIndexService>? logger = null)
    {
        _fileMonitor = fileMonitor;
        _vectorStore = vectorStore;
        _embeddingService = embeddingService;
        _chunker = chunker;
        _hashDetector = hashDetector;
        _configManager = configManager;
        _logger = logger;
        _indexQueue = new Queue<FileChangeEventArgs>();

        _processTimer = new Timer(ProcessQueue, null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("自动索引服务启动中...");

        // 订阅文件变更事件
        _fileMonitor.FileChanged += OnFileChanged;

        // 从配置加载监控源
        var config = _configManager.Load();
        foreach (var source in config.Sources.Where(s => s.Enabled))
        {
            _fileMonitor.AddSource(source.Path, source.Name);
            _logger?.LogInformation("添加监控源: {Name} ({Path})", source.Name, source.Path);
        }

        // 启动文件监控
        _ = _fileMonitor.StartAsync(cancellationToken);

        // 启动队列处理定时器
        _processTimer.Change(1000, 1000);

        _logger?.LogInformation("自动索引服务已启动，监控 {Count} 个源", config.Sources.Count(s => s.Enabled));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.LogInformation("自动索引服务停止中...");

        // 停止定时器
        _processTimer.Change(Timeout.Infinite, Timeout.Infinite);

        // 取消订阅
        _fileMonitor.FileChanged -= OnFileChanged;

        // 停止文件监控
        await _fileMonitor.StopAsync();

        _logger?.LogInformation("自动索引服务已停止");
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
            _logger?.LogError(ex, "处理文件变更失败: {FilePath}", change.FilePath);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async Task ProcessChangeAsync(FileChangeEventArgs change)
    {
        _logger?.LogInformation("开始处理: {FileName} ({ChangeType})", Path.GetFileName(change.FilePath), change.ChangeType);

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
                // 检查文件是否存在
                if (!File.Exists(change.FilePath))
                {
                    _logger?.LogDebug("文件不存在，跳过: {FileName}", Path.GetFileName(change.FilePath));
                    return;
                }

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
                    ModifiedAt = File.GetLastWriteTime(change.FilePath),
                    IndexedAt = DateTime.UtcNow
                };

                // 分段
                var chunks = _chunker.Chunk(content, new ChunkingOptions
                {
                    MaxTokens = 512,
                    MinTokens = 50,
                    OverlapTokens = 50
                });

                if (chunks.Count == 0)
                {
                    _logger?.LogDebug("文件内容为空或无法分段，跳过: {FileName}", Path.GetFileName(change.FilePath));
                    return;
                }

                // 设置 chunk 的 FileId 和 Source
                foreach (var chunk in chunks)
                {
                    chunk.FileId = fileRecord.Id;
                    chunk.Source = fileRecord.Source;
                    chunk.Id = Guid.NewGuid().ToString();
                }

                fileRecord.ChunkCount = chunks.Count;
                fileRecord.TotalTokens = chunks.Sum(c => c.TokenCount);

                // 生成向量
                var vectors = new List<VectorRecord>();
                var config = _configManager.Load();
                var batchSize = config.Embedding.BatchSize;

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
                await _vectorStore.DeleteChunksByFileAsync(fileRecord.Id);
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
            _processTimer?.Dispose();
            _disposed = true;
        }
    }
}
