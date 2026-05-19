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

namespace ReferenceRAG.Service.Services;

/// <summary>
/// 批量索引任务调度器。负责 job 队列管理、进度广播、并行协调。
/// 单文件索引逻辑统一由 IFileIndexPipeline 执行。
/// </summary>
public class IndexService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IHubContext<IndexHub> _hubContext;
    private readonly ConfigManager _configManager;
    private readonly IFileIndexPipeline _pipeline;
    private readonly FileProcessingGuard _guard;
    private readonly ILogger<IndexService> _logger;

    private readonly ConcurrentDictionary<string, IndexJob> _activeJobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCancellationTokens = new();
    private readonly ConcurrentQueue<IndexJob> _completedJobs = new();
    private const int MaxCompletedJobs = 20;

    public IndexService(
        IServiceProvider serviceProvider,
        IHubContext<IndexHub> hubContext,
        ConfigManager configManager,
        IFileIndexPipeline pipeline,
        FileProcessingGuard guard,
        ILogger<IndexService> logger)
    {
        _serviceProvider = serviceProvider;
        _hubContext = hubContext;
        _configManager = configManager;
        _pipeline = pipeline;
        _guard = guard;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, IndexJob> ActiveJobs => _activeJobs;
    public IReadOnlyCollection<IndexJob> CompletedJobs => _completedJobs;

    public void ClearCompletedJobs()
    {
        while (_completedJobs.TryDequeue(out _)) { }
        _logger.LogInformation("已清空所有已完成的索引任务记录");
    }

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

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _jobCancellationTokens[indexId] = cts;
        _activeJobs[indexId] = job;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
                var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

                job.Status = IndexStatus.Running;

                await IndexHub.BroadcastIndexStarted(_hubContext, new IndexStartedEvent
                {
                    IndexId = indexId, TotalFiles = 0, StartTime = job.StartTime
                });

                var config = _configManager.Load();
                var sources = request.Sources?.Count > 0
                    ? config.Sources.Where(s => request.Sources.Contains(s.Name)).ToList()
                    : config.Sources.Where(s => s.Enabled).ToList();

                var allFiles = new List<string>();
                foreach (var source in sources)
                {
                    var normalizedPath = PathUtility.NormalizePath(source.Path);
                    if (!Directory.Exists(normalizedPath))
                    {
                        _logger.LogWarning("源目录不存在: {Path}", source.Path);
                        continue;
                    }
                    allFiles.AddRange(
                        Directory.GetFiles(normalizedPath, "*.*", SearchOption.AllDirectories)
                            .Where(f => source.FilePatterns.Any(p => MatchesPattern(f, p)))
                            .Where(f => !source.ExcludeDirs.Any(d => f.Contains(d))));
                }

                allFiles = allFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                job.TotalFiles = allFiles.Count;

                await IndexHub.BroadcastIndexStarted(_hubContext, new IndexStartedEvent
                {
                    IndexId = indexId, TotalFiles = job.TotalFiles, StartTime = job.StartTime
                });

                var sw = Stopwatch.StartNew();
                var errors = new ConcurrentBag<string>();
                var processedCount = 0;
                var errorsCount = 0;

                // ── Phase 1: CPU 并行 ── 读文件、hash检测、分块、清旧数据
                const int maxPrepParallelism = 8;
                using var prepSemaphore = new SemaphoreSlim(maxPrepParallelism);

                var prepTasks = allFiles.Select(async file =>
                {
                    if (cts.Token.IsCancellationRequested) return null;
                    if (!_guard.TryAcquire(file)) return null;
                    await prepSemaphore.WaitAsync(cts.Token);
                    try
                    {
                        return request.VectorOnly
                            ? await _pipeline.PrepareVectorOnlyAsync(file, cts.Token)
                            : await _pipeline.PrepareAsync(file, sources, request.Force, cts.Token);
                    }
                    catch (OperationCanceledException) { return null; }
                    catch (Exception ex)
                    {
                        errors.Add($"{file}: {ex.Message}");
                        Interlocked.Increment(ref errorsCount);
                        _logger.LogWarning(ex, "Phase1 失败: {File}", file);
                        return null;
                    }
                    finally
                    {
                        prepSemaphore.Release();
                        _guard.Release(file);
                    }
                }).ToList();

                var prepResults = await Task.WhenAll(prepTasks);
                var contexts = prepResults.Where(c => c != null).ToList()!;

                // ── 清理磁盘已删文件（Bug A）──
                if (!request.VectorOnly && !cts.Token.IsCancellationRequested)
                {
                    var allFilesSet = new HashSet<string>(allFiles, StringComparer.OrdinalIgnoreCase);
                    var sourceNames = sources.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var dbFiles = (await vectorStore.GetAllFilesAsync(cts.Token))
                        .Where(f => sourceNames.Contains(f.Source));

                    foreach (var dbFile in dbFiles)
                    {
                        if (cts.Token.IsCancellationRequested) break;
                        if (!allFilesSet.Contains(dbFile.Path))
                        {
                            try
                            {
                                await _pipeline.DeleteFileAsync(dbFile.Id, dbFile.Path, cts.Token);
                                _logger.LogInformation("清理已删除文件: {Path}", dbFile.Path);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "清理已删除文件失败: {Path}", dbFile.Path);
                            }
                        }
                    }
                }

                // ── Phase 2: GPU 统一大批次推理 ──
                var allChunks = contexts.SelectMany(c => c!.Chunks).ToList();
                if (allChunks.Count > 0)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    using var indexingPipeline = new IndexingPipeline(
                        embeddingService,
                        vectorStore,
                        batchSize: config.Embedding.BatchSize);
                    await indexingPipeline.ExecuteAsync(allChunks, "batch", cts.Token);
                }

                // ── Phase 3: BM25 + 图谱后处理 ──
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
                        if (!request.VectorOnly)
                            await _pipeline.FinalizeAsync(ctx!, filenameMap, cts.Token);

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
                        _logger.LogWarning(ex, "Phase3 失败: {File}", ctx!.FileRecord.Path);
                    }
                    finally { finSemaphore.Release(); }
                }).ToList();

                await Task.WhenAll(finalizeTasks);

                job.ProcessedFiles = processedCount;
                job.Errors = errorsCount;
                sw.Stop();

                job.Status = cts.Token.IsCancellationRequested ? IndexStatus.Cancelled : IndexStatus.Completed;
                job.EndTime = DateTime.UtcNow;
                job.Duration = sw.Elapsed;

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
                if (_jobCancellationTokens.TryRemove(indexId, out var removedCts))
                    removedCts.Dispose();
                if (job.Status is IndexStatus.Completed or IndexStatus.Cancelled)
                {
                    _completedJobs.Enqueue(job);
                    while (_completedJobs.Count > MaxCompletedJobs)
                        _completedJobs.TryDequeue(out _);
                }
                _activeJobs.TryRemove(indexId, out _);
            }
        }, cts.Token);

        return job;
    }

    public Task<bool> StopIndexAsync(string indexId)
    {
        if (_activeJobs.TryGetValue(indexId, out var job) &&
            job.Status is IndexStatus.Running or IndexStatus.Pending)
        {
            if (_jobCancellationTokens.TryGetValue(indexId, out var cts))
                cts.Cancel();
            job.Status = IndexStatus.Cancelled;
            return Task.FromResult(true);
        }
        _logger.LogWarning("无法停止 {IndexId}：任务不存在或状态不允许 (当前: {Status})",
            indexId, _activeJobs.TryGetValue(indexId, out var j) ? j.Status.ToString() : "null");
        return Task.FromResult(false);
    }

    public IndexJob? GetStatus(string indexId) =>
        _activeJobs.TryGetValue(indexId, out var job) ? job : null;

    private static bool MatchesPattern(string filePath, string pattern)
    {
        if (pattern.StartsWith("*."))
            return filePath.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
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

public class IndexRequest
{
    public List<string>? Sources { get; set; }
    public bool Force { get; set; }
    /// <summary>仅重建向量（跳过分块/BM25/图谱），用于切换嵌入模型后重新推理</summary>
    public bool VectorOnly { get; set; }
}

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

public enum IndexStatus
{
    Pending, Running, Completed, Failed, Cancelled
}
