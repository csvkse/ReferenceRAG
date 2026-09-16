using ReferenceRAG.Business.Features.Indexing.Contracts;
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
    private readonly IIndexEventPublisher _events;
    private readonly ConfigManager _configManager;
    private readonly IFileIndexPipeline _pipeline;
    private readonly IFileProcessingGuard _guard;
    private readonly ILogger<IndexService> _logger;

    private readonly ConcurrentDictionary<string, IndexJob> _activeJobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCancellationTokens = new();
    private readonly ConcurrentQueue<IndexJob> _completedJobs = new();
    private readonly ConcurrentDictionary<string, Task> _runningJobs = new();
    // jobId → 正在处理中的文件集合（Phase1 加入、Phase3 完成移除）
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _activeFiles = new();
    private readonly CancellationTokenSource _stopping = new();
    private const int MaxCompletedJobs = 20;

    public IndexService(
        IServiceProvider serviceProvider,
        IIndexEventPublisher events,
        ConfigManager configManager,
        IFileIndexPipeline pipeline,
        IFileProcessingGuard guard,
        ILogger<IndexService> logger)
    {
        _serviceProvider = serviceProvider;
        _events = events;
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

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        _jobCancellationTokens[indexId] = cts;
        _activeJobs[indexId] = job;

        var work = Task.Run(async () =>
        {
            var heldFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
                var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

                job.Status = IndexStatus.Running;

                await _events.PublishAsync("IndexStarted", new IndexStartedEvent
                {
                    IndexId = indexId, TotalFiles = 0, StartTime = job.StartTime
                });

                var config = _configManager.Load();
                var sources = request.Sources?.Count > 0
                    ? config.Sources.Where(s => request.Sources.Contains(s.Name)).ToList()
                    : config.Sources.Where(s => s.Enabled).ToList();

                var allFiles = request.Files?.ToList() ?? new List<string>();
                if (request.Files is null)
                {
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
                }

                allFiles = allFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                job.TotalFiles = allFiles.Count;

                await _events.PublishAsync("IndexStarted", new IndexStartedEvent
                {
                    IndexId = indexId, TotalFiles = job.TotalFiles, StartTime = job.StartTime
                });

                var sw = Stopwatch.StartNew();
                var errors = new ConcurrentBag<string>();
                var processedCount = 0;
                var skippedCount = 0;
                var errorsCount = 0;
                // job 级 active files（Phase1 加入、Phase3 完成移除），支持并发查看"正在处理"
                var activeFiles = _activeFiles.GetOrAdd(indexId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));

                // ── Phase 1: CPU 并行 ── 读文件、hash检测、分块、清旧数据
                const int maxPrepParallelism = 8;
                using var prepSemaphore = new SemaphoreSlim(maxPrepParallelism);

                var prepTasks = allFiles.Select(async file =>
                {
                    if (cts.Token.IsCancellationRequested) return null;
                    if (!_guard.TryAcquire(file))
                    {
                        Interlocked.Increment(ref skippedCount);   // 已被其它任务处理，跳过
                        return null;
                    }
                    FileProcessContext? prepared = null;
                    var enteredSemaphore = false;
                    try
                    {
                        await prepSemaphore.WaitAsync(cts.Token);
                        enteredSemaphore = true;
                        activeFiles.TryAdd(file, 0);
                        prepared = request.VectorOnly
                            ? await _pipeline.PrepareVectorOnlyAsync(file, cts.Token)
                            : await _pipeline.PrepareAsync(file, sources, request.Force, cts.Token);
                        if (prepared is not null)
                            heldFiles.TryAdd(file, 0);
                        else
                            Interlocked.Increment(ref skippedCount);   // 内容未变化/无 chunk，跳过
                        return prepared;
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref skippedCount);
                        return null;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{file}: {ex.Message}");
                        Interlocked.Increment(ref errorsCount);
                        _logger.LogWarning(ex, "Phase1 失败: {File}", file);
                        return null;
                    }
                    finally
                    {
                        if (enteredSemaphore)
                            prepSemaphore.Release();
                        activeFiles.TryRemove(file, out _);
                        if (prepared is null)
                            _guard.Release(file);
                    }
                }).ToList();

                var prepResults = await Task.WhenAll(prepTasks);
                var contexts = prepResults.Where(c => c != null).ToList()!;

                // ── 清理磁盘已删文件（Bug A）──
                if (request.Files is null && !request.VectorOnly && !cts.Token.IsCancellationRequested)
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
                var totalVectorsCount = 0;
                var totalChunksCount = allChunks.Count;
                if (allChunks.Count > 0)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    using var indexingPipeline = new IndexingPipeline(
                        embeddingService,
                        vectorStore,
                        batchSize: config.Embedding.BatchSize);
                    // VectorOnly 等场景的临时嵌入输入（未持久化的截断文本）随批次传入
                    var embeddingInputs = contexts
                        .Where(c => c!.EmbeddingInputs != null)
                        .SelectMany(c => c!.EmbeddingInputs!)
                        .ToDictionary(kv => kv.Key, kv => kv.Value);
                    var pipelineResult = await indexingPipeline.ExecuteAsync(
                        allChunks,
                        "batch",
                        cts.Token,
                        embeddingInputs: embeddingInputs.Count > 0 ? embeddingInputs : null);
                    if (!pipelineResult.Success)
                        throw new InvalidOperationException($"向量管道失败: {pipelineResult.ErrorMessage}");
                    totalVectorsCount = pipelineResult.TotalVectors;
                }

                // ── Phase 3: BM25 + 图谱后处理 ──
                var allIndexedFiles = await vectorStore.GetAllFilesAsync(cts.Token);
                var filenameMap = GraphFilenameMapper.BuildFilenameMap(allIndexedFiles);

                const int maxFinalizeParallelism = 4;
                using var finSemaphore = new SemaphoreSlim(maxFinalizeParallelism);

                // Phase3 期间记录最近处理的文件（用于 job.CurrentFile 展示）
                var currentFileName = "";

                var finalizeTasks = contexts.Select(async ctx =>
                {
                    if (cts.Token.IsCancellationRequested) return;
                    await finSemaphore.WaitAsync(cts.Token);
                    try
                    {
                        currentFileName = ctx!.FileRecord.FileName;
                        // VectorOnly 时跳过图谱与 BM25 重写（仅重推向量）
                        await _pipeline.FinalizeAsync(
                            ctx!,
                            filenameMap,
                            cts.Token,
                            updateGraph: !request.VectorOnly,
                            updateBm25: !request.VectorOnly);

                        var count = Interlocked.Increment(ref processedCount);
                        await _events.PublishAsync("IndexProgress", new IndexProgressEvent
                        {
                            IndexId = indexId,
                            ProcessedFiles = count,
                            TotalFiles = job.TotalFiles,
                            CurrentFile = currentFileName,
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
                    finally
                    {
                        finSemaphore.Release();
                        _guard.Release(ctx!.FileRecord.Path);
                        heldFiles.TryRemove(ctx.FileRecord.Path, out _);
                    }
                }).ToList();

                await Task.WhenAll(finalizeTasks);

                // 聚合 job 状态：currentFile 用最近完成的一个，currentFiles 为仍在处理的集合
                job.CurrentFile = currentFileName;
                job.CurrentFiles = activeFiles.Keys.ToList();
                job.ProcessedFiles = processedCount;
                job.SkippedFiles = skippedCount;
                job.Errors = errorsCount;
                job.TotalChunks = totalChunksCount;
                job.TotalVectors = totalVectorsCount;
                sw.Stop();

                job.Status = cts.Token.IsCancellationRequested ? IndexStatus.Cancelled : IndexStatus.Completed;
                job.EndTime = DateTime.UtcNow;
                job.Duration = sw.Elapsed;

                await _events.PublishAsync("IndexCompleted", new IndexCompletedEvent
                {
                    IndexId = indexId,
                    TotalFiles = job.TotalFiles,
                    TotalChunks = job.TotalChunks,
                    TotalVectors = job.TotalVectors,
                    Duration = sw.Elapsed,
                    CompletedAt = job.EndTime.Value,
                    Errors = errors.ToList()
                });
            }
            catch (OperationCanceledException)
            {
                job.Status = IndexStatus.Cancelled;
                job.EndTime = DateTime.UtcNow;
                job.Duration = job.EndTime.Value - job.StartTime;
                _logger.LogInformation("索引任务 {IndexId} 已取消", indexId);
                // 失败/取消也必须广播完成事件，否则前端 isIndexing 永远不会复位
                await _events.PublishAsync("IndexCompleted", new IndexCompletedEvent
                {
                    IndexId = indexId,
                    TotalFiles = job.TotalFiles,
                    TotalChunks = job.TotalChunks,
                    TotalVectors = job.TotalVectors,
                    Duration = job.Duration,
                    CompletedAt = job.EndTime.Value
                });
            }
            catch (Exception ex)
            {
                job.Status = IndexStatus.Failed;
                job.ErrorMessage = ex.Message;
                job.EndTime = DateTime.UtcNow;
                job.Duration = job.EndTime.Value - job.StartTime;
                _logger.LogError(ex, "Index job {IndexId} failed", indexId);
                await _events.PublishAsync("IndexCompleted", new IndexCompletedEvent
                {
                    IndexId = indexId,
                    TotalFiles = job.TotalFiles,
                    TotalChunks = job.TotalChunks,
                    TotalVectors = job.TotalVectors,
                    Duration = job.Duration,
                    CompletedAt = job.EndTime.Value,
                    Errors = new List<string> { ex.Message }
                });
            }
            finally
            {
                foreach (var file in heldFiles.Keys)
                    _guard.Release(file);
                _activeFiles.TryRemove(indexId, out _);
                if (_jobCancellationTokens.TryRemove(indexId, out var removedCts))
                    removedCts.Dispose();
                if (job.Status is IndexStatus.Completed or IndexStatus.Failed or IndexStatus.Cancelled)
                {
                    _completedJobs.Enqueue(job);
                    while (_completedJobs.Count > MaxCompletedJobs)
                        _completedJobs.TryDequeue(out _);
                }
                _activeJobs.TryRemove(indexId, out _);
            }
        });
        _runningJobs[indexId] = work;
        _ = work.ContinueWith(completed => _runningJobs.TryRemove(indexId, out var ignored), TaskScheduler.Default);

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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        await Task.WhenAll(_runningJobs.Values).WaitAsync(cancellationToken);
        _logger.LogInformation("Index service stopped");
    }
}

public class IndexRequest
{
    public List<string>? Sources { get; set; }
    internal List<string>? Files { get; set; }
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
    public int SkippedFiles { get; set; }
    public int Errors { get; set; }
    public int TotalChunks { get; set; }
    public int TotalVectors { get; set; }
    public string CurrentFile { get; set; } = string.Empty;

    /// <summary>
    /// 正在处理中的文件（Phase1 加入、Phase3 完成移除），供 /api/index/jobs 展示
    /// </summary>
    public List<string> CurrentFiles { get; set; } = new();

    /// <summary>
    /// 跳过计数包含：内容未变化、无 chunk、文件被占用、取消等未进入向量阶段的部分
    /// </summary>
    public string? ErrorMessage { get; set; }
    public double ProgressPercent => TotalFiles > 0 ? (double)(ProcessedFiles + SkippedFiles) / TotalFiles * 100 : 0;
}

public enum IndexStatus
{
    Pending, Running, Completed, Failed, Cancelled
}
