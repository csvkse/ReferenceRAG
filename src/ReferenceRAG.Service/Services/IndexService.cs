using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Graph;
using ReferenceRAG.Service.Hubs;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ReferenceRAG.Service.Services;

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
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCancellationTokens = new();

    // 已完成任务记录（最多保留20条）
    private readonly ConcurrentQueue<IndexJob> _completedJobs = new();
    private const int MaxCompletedJobs = 20;

    private readonly IBM25Store _bm25Store;

    public IndexService(
        IServiceProvider serviceProvider,
        IHubContext<IndexHub> hubContext,
        ConfigManager configManager,
        IBM25Store bm25Store,
        ILogger<IndexService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _configManager = configManager;
        _bm25Store = bm25Store;
        _logger = logger;
    }

    /// <summary>
    /// 当前活跃的索引任务
    /// </summary>
    public IReadOnlyDictionary<string, IndexJob> ActiveJobs => _activeJobs;

    /// <summary>
    /// 已完成/已取消的索引任务历史（最多20条）
    /// </summary>
    public IReadOnlyCollection<IndexJob> CompletedJobs => _completedJobs;

    /// <summary>
    /// 清空已完成/已取消的索引任务历史
    /// </summary>
    public void ClearCompletedJobs()
    {
        while (_completedJobs.TryDequeue(out _)) { }
        _logger.LogInformation("已清空所有已完成的索引任务记录");
    }

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

        // 创建可取消的 CancellationTokenSource
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _jobCancellationTokens[indexId] = cts;
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
                var errors = new ConcurrentBag<string>();
                var processedCount = 0;
                var errorsCount = 0;

                // ── Phase 1: CPU 并行 ── 读文件、hash 检测、分块（无 GPU 调用）
                const int maxPrepParallelism = 8;
                using var prepSemaphore = new SemaphoreSlim(maxPrepParallelism);

                var prepTasks = allFiles.Select(async file =>
                {
                    if (cts.Token.IsCancellationRequested) return null;
                    await prepSemaphore.WaitAsync(cts.Token);
                    try
                    {
                        return await PrepareFileAsync(file, chunker, vectorStore, sources, request.Force, cts.Token);
                    }
                    catch (OperationCanceledException) { return null; }
                    catch (Exception ex)
                    {
                        errors.Add($"{file}: {ex.Message}");
                        Interlocked.Increment(ref errorsCount);
                        _logger.LogWarning(ex, "Failed to prepare file: {File}", file);
                        return null;
                    }
                    finally { prepSemaphore.Release(); }
                }).ToList();

                var prepResults = await Task.WhenAll(prepTasks);
                var contexts = prepResults.Where(c => c != null).ToList()!;

                // ── Phase 2: GPU 统一大批次推理 ── 所有文件 chunks 合并，一次性过 GPU
                var allChunks = contexts.SelectMany(c => c!.Chunks).ToList();
                if (allChunks.Count > 0)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    using var pipeline = new IndexingPipeline(
                        embeddingService, vectorStore,
                        batchSize: config.Embedding.BatchSize);
                    await pipeline.ExecuteAsync(allChunks, "batch", cts.Token);
                }

                // ── Phase 3: CPU 并行 ── BM25 + 知识图谱后处理
                // filename→fullNodeId 映射：必须用全量文件（含本次未变化的文件），
                // 否则增量索引时本批次文件指向旧文件的 wiki-link 无法解析
                var allIndexedFiles = await vectorStore.GetAllFilesAsync(cts.Token);
                var filenameMap = GraphIndexingService.BuildFilenameMap(allIndexedFiles);

                const int maxFinalizeParallelism = 4;
                using var finSemaphore = new SemaphoreSlim(maxFinalizeParallelism);

                var finalizeTasks = contexts.Select(async ctx =>
                {
                    if (cts.Token.IsCancellationRequested) return;
                    await finSemaphore.WaitAsync(cts.Token);
                    try
                    {
                        await FinalizeFileAsync(ctx!, _bm25Store, filenameMap, cts.Token);
                        var count = Interlocked.Increment(ref processedCount);
                        await IndexHub.BroadcastIndexProgress(_hubContext, new IndexProgressEvent
                        {
                            IndexId = indexId,
                            ProcessedFiles = count,
                            TotalFiles = job.TotalFiles,
                            CurrentFile = ctx!.FileRecord.FileName,
                            Timestamp = DateTime.UtcNow
                        });
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        errors.Add($"{ctx!.FileRecord.Path}: {ex.Message}");
                        Interlocked.Increment(ref errorsCount);
                        _logger.LogWarning(ex, "Failed to finalize file: {File}", ctx!.FileRecord.Path);
                    }
                    finally { finSemaphore.Release(); }
                }).ToList();

                await Task.WhenAll(finalizeTasks);

                // 更新最终状态
                job.ProcessedFiles = processedCount;
                job.Errors = errorsCount;

                sw.Stop();

                // 根据是否被取消设置最终状态
                if (cts.Token.IsCancellationRequested)
                {
                    job.Status = IndexStatus.Cancelled;
                    _logger.LogInformation("索引任务 {IndexId} 已取消，已处理 {ProcessedFiles}/{TotalFiles} 文件",
                        indexId, processedCount, job.TotalFiles);
                }
                else
                {
                    job.Status = IndexStatus.Completed;
                }

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
                    Errors = errors.ToList()
                });
            }
            catch (OperationCanceledException)
            {
                job.Status = IndexStatus.Cancelled;
                job.EndTime = DateTime.UtcNow;
                _logger.LogInformation("索引任务 {IndexId} 已取消", indexId);
            }
            catch (Exception ex)
            {
                job.Status = IndexStatus.Failed;
                job.ErrorMessage = ex.Message;
                job.EndTime = DateTime.UtcNow;
                _logger.LogError(ex, "Index job {IndexId} failed", indexId);
            }
            finally
            {
                // 清理 CancellationTokenSource
                if (_jobCancellationTokens.TryRemove(indexId, out var cts))
                {
                    cts.Dispose();
                }

                // 将已完成/已取消的任务添加到历史队列
                if (job.Status == IndexStatus.Completed || job.Status == IndexStatus.Cancelled)
                {
                    _completedJobs.Enqueue(job);

                    // 保持历史记录不超过最大数量
                    while (_completedJobs.Count > MaxCompletedJobs)
                    {
                        _completedJobs.TryDequeue(out _);
                    }
                }

                // 从活跃任务中移除
                _activeJobs.TryRemove(indexId, out var _);
            }
        }, cts.Token);

        return job;
    }

    /// <summary>
    /// 停止索引任务
    /// </summary>
    public Task<bool> StopIndexAsync(string indexId)
    {
        if (_activeJobs.TryGetValue(indexId, out var job) &&
            (job.Status == IndexStatus.Running || job.Status == IndexStatus.Pending))
        {
            _logger.LogInformation("请求停止索引任务 {IndexId}", indexId);

            // 取消 CancellationToken
            if (_jobCancellationTokens.TryGetValue(indexId, out var cts))
            {
                cts.Cancel();
            }

            job.Status = IndexStatus.Cancelled;
            return Task.FromResult(true);
        }

        _logger.LogWarning("无法停止索引任务 {IndexId}：任务不存在或状态不允许 (当前状态: {Status})",
            indexId, job?.Status.ToString() ?? "null");
        return Task.FromResult(false);
    }

    /// <summary>
    /// 获取索引状态
    /// </summary>
    public IndexJob? GetStatus(string indexId)
    {
        return _activeJobs.TryGetValue(indexId, out var job) ? job : null;
    }

    /// <summary>
    /// Phase 1: 读文件、hash 检测、分块 — 纯 CPU，无 GPU 调用。
    /// 返回 null 表示文件无需重建。
    /// </summary>
    private async Task<FileProcessContext?> PrepareFileAsync(
        string filePath,
        IMarkdownChunker chunker,
        IVectorStore vectorStore,
        List<SourceFolder> sources,
        bool forceReindex,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);

        var sortedSources = sources.OrderByDescending(s => s.Path.Length).ToList();
        var source = sortedSources.FirstOrDefault(s =>
            filePath.StartsWith(s.Path + Path.DirectorySeparatorChar) ||
            filePath.Equals(s.Path) ||
            filePath.StartsWith(PathUtility.NormalizePath(s.Path) + Path.DirectorySeparatorChar) ||
            filePath.Equals(PathUtility.NormalizePath(s.Path)));

        var contentHash = ComputeHash(content);
        var existingFile = await vectorStore.GetFileByPathAsync(filePath, cancellationToken);
        var fileId = existingFile?.Id ?? Guid.NewGuid().ToString();

        if (!forceReindex && existingFile != null && existingFile.ContentHash == contentHash)
        {
            _logger.LogDebug("内容未变化，跳过: {FileName}", Path.GetFileName(filePath));
            return null;
        }

        var chunks = chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 512,
            MinTokens = 50,
            OverlapTokens = 50
        });
        if (chunks.Count == 0) return null;

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

        await vectorStore.UpsertFileAsync(fileRecord, cancellationToken);

        var oldChunks = await vectorStore.GetChunksByFileAsync(fileId, cancellationToken);
        var oldChunkIds = oldChunks.Select(c => c.Id).ToList();

        await vectorStore.DeleteChunksByFileAsync(fileId, cancellationToken);

        foreach (var chunk in chunks)
        {
            chunk.FileId = fileId;
            chunk.Id = Guid.NewGuid().ToString();
            chunk.Source = source?.Name ?? "unknown";
        }

        return new FileProcessContext
        {
            FileRecord = fileRecord,
            Content = content,
            Chunks = chunks,
            OldChunkIds = oldChunkIds
        };
    }

    /// <summary>
    /// Phase 3: BM25 索引 + 知识图谱更新 — GPU 推理已完成，纯 CPU 后处理。
    /// </summary>
    private async Task FinalizeFileAsync(
        FileProcessContext ctx,
        IBM25Store? bm25Store,
        IReadOnlyDictionary<string, string> filenameMap,
        CancellationToken cancellationToken)
    {
        if (bm25Store != null && ctx.OldChunkIds.Count > 0)
            await bm25Store.DeleteDocumentsByIdsAsync(ctx.OldChunkIds);

        if (bm25Store != null)
        {
            var bm25Docs = ctx.Chunks.Select(c => (c.Id, c.Content));
            await bm25Store.IndexBatchAsync(bm25Docs);
        }

        var graphIndexing = _serviceProvider.GetService<GraphIndexingService>();
        if (graphIndexing != null)
        {
            Func<string, string?> resolver = shortId =>
                filenameMap.TryGetValue(shortId, out var full) ? full : null;
            await graphIndexing.UpdateGraphAsync(ctx.FileRecord, ctx.Content, ctx.Chunks, cancellationToken, resolver);
        }
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

/// <summary>
/// Phase 1 → Phase 2 → Phase 3 之间传递的文件上下文
/// </summary>
internal class FileProcessContext
{
    public FileRecord FileRecord { get; set; } = null!;
    public string Content { get; set; } = "";
    public List<ChunkRecord> Chunks { get; set; } = new();
    public List<string> OldChunkIds { get; set; } = new();
}
