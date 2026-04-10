using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using ObsidianRAG.Core.Services;
using ObsidianRAG.Service.Hubs;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ObsidianRAG.Service.Services;

/// <summary>
/// 索引服务 - 协调文件索引流程并广播进度
/// </summary>
public class IndexService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<IndexHub> _hubContext;
    private readonly ConfigManager _configManager;
    private readonly ILogger<IndexService> _logger;

    private readonly ConcurrentDictionary<string, IndexJob> _activeJobs = new();

    public IndexService(
        IServiceProvider serviceProvider,
        IHubContext<IndexHub> hubContext,
        ConfigManager configManager,
        ILogger<IndexService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _configManager = configManager;
        _logger = logger;
    }

    /// <summary>
    /// 当前活跃的索引任务
    /// </summary>
    public IReadOnlyDictionary<string, IndexJob> ActiveJobs => _activeJobs;

    /// <summary>
    /// 启动索引任务
    /// </summary>
    public async Task<IndexJob> StartIndexAsync(IndexRequest request, CancellationToken cancellationToken = default)
    {
        var indexId = Guid.NewGuid().ToString("N")[..8];
        var job = new IndexJob
        {
            Id = indexId,
            Status = IndexStatus.Pending,
            Request = request,
            StartTime = DateTime.UtcNow
        };

        _activeJobs[indexId] = job;

        // 后台执行索引
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
                var chunker = scope.ServiceProvider.GetRequiredService<IMarkdownChunker>();
                var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
                var fileDetector = scope.ServiceProvider.GetRequiredService<IFileChangeDetector>();

                job.Status = IndexStatus.Running;

                // 广播开始事件
                await IndexHub.BroadcastIndexStarted(_hubContext, new IndexStartedEvent
                {
                    IndexId = indexId,
                    TotalFiles = 0,
                    StartTime = job.StartTime
                });

                var config = _configManager.Load();
                var sources = request.Sources?.Count > 0
                    ? config.Sources.Where(s => request.Sources.Contains(s.Name)).ToList()
                    : config.Sources.Where(s => s.Enabled).ToList();

                var allFiles = new List<string>();
                foreach (var source in sources)
                {
                    // 转换路径格式（WSL 环境下将 Windows 路径转为 /mnt/x/ 格式）
                    var normalizedPath = PathUtility.NormalizePath(source.Path);
                    if (!Directory.Exists(normalizedPath))
                    {
                        _logger.LogWarning("源目录不存在或无法访问: {Path} (normalized: {NormalizedPath})", source.Path, normalizedPath);
                        continue;
                    }

                    var files = Directory.GetFiles(normalizedPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => source.FilePatterns.Any(p => MatchesPattern(f, p)))
                        .Where(f => !source.ExcludeDirs.Any(d => f.Contains(d)));

                    allFiles.AddRange(files);
                }

                job.TotalFiles = allFiles.Count;

                // 更新开始事件的总文件数
                await IndexHub.BroadcastIndexStarted(_hubContext, new IndexStartedEvent
                {
                    IndexId = indexId,
                    TotalFiles = job.TotalFiles,
                    StartTime = job.StartTime
                });

                var sw = Stopwatch.StartNew();
                var errors = new List<string>();
                var processedCount = 0;
                var errorsCount = 0;
                var currentFileLock = new object();
                string currentFile = "";

                // 使用 SemaphoreSlim 限制并发文件处理数量 (避免同时打开过多文件/连接)
                const int maxDegreeOfParallelism = 4;
                using var semaphore = new SemaphoreSlim(maxDegreeOfParallelism);

                // 并行处理文件
                var tasks = allFiles.Select(async file =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        var fileName = Path.GetFileName(file);

                        // 线程安全地更新当前文件
                        lock (currentFileLock)
                        {
                            currentFile = fileName;
                        }

                        await ProcessFileAsync(file, embeddingService, chunker, vectorStore, sources);

                        // 线程安全地递增计数器
                        var count = Interlocked.Increment(ref processedCount);

                        // 广播进度
                        await IndexHub.BroadcastIndexProgress(_hubContext, new IndexProgressEvent
                        {
                            IndexId = indexId,
                            ProcessedFiles = count,
                            TotalFiles = job.TotalFiles,
                            CurrentFile = fileName,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{file}: {ex.Message}");
                        Interlocked.Increment(ref errorsCount);
                        _logger.LogWarning(ex, "Failed to index file: {File}", file);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks);

                // 更新最终状态
                job.ProcessedFiles = processedCount;
                job.Errors = errorsCount;

                sw.Stop();

                job.Status = IndexStatus.Completed;
                job.EndTime = DateTime.UtcNow;
                job.Duration = sw.Elapsed;

                // 广播完成事件
                await IndexHub.BroadcastIndexCompleted(_hubContext, new IndexCompletedEvent
                {
                    IndexId = indexId,
                    TotalFiles = job.TotalFiles,
                    TotalChunks = job.ProcessedFiles,
                    TotalVectors = job.ProcessedFiles,
                    Duration = sw.Elapsed,
                    CompletedAt = job.EndTime.Value,
                    Errors = errors
                });
            }
            catch (Exception ex)
            {
                job.Status = IndexStatus.Failed;
                job.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Index job {IndexId} failed", indexId);
            }
            finally
            {
                // 延迟移除，允许客户端查询结果
                _ = Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ =>
                {
                    _activeJobs.TryRemove(indexId, out var _);
                });
            }
        }, cancellationToken);

        return job;
    }

    /// <summary>
    /// 停止索引任务
    /// </summary>
    public Task<bool> StopIndexAsync(string indexId)
    {
        if (_activeJobs.TryGetValue(indexId, out var job) && job.Status == IndexStatus.Running)
        {
            job.Status = IndexStatus.Cancelled;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    /// <summary>
    /// 获取索引状态
    /// </summary>
    public IndexJob? GetStatus(string indexId)
    {
        return _activeJobs.TryGetValue(indexId, out var job) ? job : null;
    }

    private async Task ProcessFileAsync(
        string filePath,
        IEmbeddingService embeddingService,
        IMarkdownChunker chunker,
        IVectorStore vectorStore,
        List<SourceFolder> sources)
    {
        var content = await File.ReadAllTextAsync(filePath);

        // 按路径长度降序排序，确保更长的路径（更精确的匹配）优先匹配
        // 这样 test-vault-english 会先于 test-vault 匹配
        var sortedSources = sources
            .OrderByDescending(s => s.Path.Length)
            .ToList();

        // 尝试用原始路径和标准化路径匹配 source，确保匹配的是完整目录
        var source = sortedSources.FirstOrDefault(s =>
            filePath.StartsWith(s.Path + Path.DirectorySeparatorChar) ||
            filePath.Equals(s.Path) ||
            filePath.StartsWith(PathUtility.NormalizePath(s.Path) + Path.DirectorySeparatorChar) ||
            filePath.Equals(PathUtility.NormalizePath(s.Path)));

        // 使用标准化路径计算相对路径
        var sourcePathForRelative = source != null
            ? (filePath.StartsWith(PathUtility.NormalizePath(source.Path))
                ? PathUtility.NormalizePath(source.Path)
                : source.Path)
            : filePath;
        var relativePath = source != null ? Path.GetRelativePath(sourcePathForRelative, filePath) : Path.GetFileName(filePath);

        // 分段
        var chunks = chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 512,
            MinTokens = 50,
            OverlapTokens = 50
        });

        if (chunks.Count == 0) return;

        // 计算内容哈希
        var contentHash = ComputeHash(content);

        // 检查文件是否已存在（通过路径或哈希）
        var existingFile = await vectorStore.GetFileByPathAsync(filePath);
        var fileId = existingFile?.Id ?? Guid.NewGuid().ToString();

        // 创建或更新文件记录
        var fileRecord = new FileRecord
        {
            Id = fileId,
            Path = filePath,
            FileName = Path.GetFileName(filePath),
            ParentFolder = Path.GetDirectoryName(filePath),
            Source = source?.Name ?? "unknown",
            ContentHash = contentHash,
            ContentLength = content.Length,
            Title = Path.GetFileNameWithoutExtension(filePath),
            ModifiedAt = File.GetLastWriteTime(filePath),
            ChunkCount = chunks.Count,
            IndexedAt = DateTime.UtcNow
        };

        await vectorStore.UpsertFileAsync(fileRecord);

        // 删除该文件关联的旧chunks和vectors（防止重复索引）
        await vectorStore.DeleteChunksByFileAsync(fileId);

        // 为每个 chunk 设置文件信息
        foreach (var chunk in chunks)
        {
            chunk.FileId = fileId;
            chunk.Id = Guid.NewGuid().ToString();
        }

        // 使用 Pipeline 进行向量化和存储（从配置读取 BatchSize）
        var config = _configManager.Load();
        using var pipeline = new IndexingPipeline(embeddingService, vectorStore, batchSize: config.Embedding.BatchSize);
        await pipeline.ExecuteAsync(chunks, source?.Name ?? "unknown");
    }

    private static string ComputeHash(string content)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private static bool MatchesPattern(string filePath, string pattern)
    {
        if (pattern.StartsWith("*."))
        {
            return filePath.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }
        return filePath.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Index service started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Index service stopped");
        return Task.CompletedTask;
    }
}

/// <summary>
/// 索引请求
/// </summary>
public class IndexRequest
{
    public List<string>? Sources { get; set; }
    public bool Force { get; set; }
}

/// <summary>
/// 索引任务
/// </summary>
public class IndexJob
{
    public string Id { get; set; } = string.Empty;
    public IndexStatus Status { get; set; }
    public IndexRequest Request { get; set; } = new();
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int Errors { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }

    public double ProgressPercent => TotalFiles > 0 ? (double)ProcessedFiles / TotalFiles * 100 : 0;
}

/// <summary>
/// 索引状态
/// </summary>
public enum IndexStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}
