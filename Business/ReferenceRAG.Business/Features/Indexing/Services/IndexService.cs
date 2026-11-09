using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Business.Features.Indexing.StateMachine;
using ReferenceRAG.Business.Features.Indexing.Strategies;
using ReferenceRAG.Business.Features.Indexing.Services;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Hubs;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Service.Services;

/// <summary>
/// 批量索引任务调度器（适配器 + 策略模式 + 状态机架构）。
/// 负责任务生命周期管理、策略解析调度、细粒度状态机推进、外键自愈防护与全阶段平滑进度广播。
/// </summary>
public class IndexService : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IIndexEventPublisher _events;
    private readonly ConfigManager _configManager;
    private readonly IFileIndexPipeline _pipeline;
    private readonly IFileProcessingGuard _guard;
    private readonly ILogger<IndexService> _logger;
    private readonly IIndexStorageAdapter? _storageAdapter;
    private readonly IndexStrategyFactory _strategyFactory;

    private readonly ConcurrentDictionary<string, IndexJob> _activeJobs = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCancellationTokens = new();
    private readonly ConcurrentQueue<IndexJob> _completedJobs = new();
    private readonly ConcurrentDictionary<string, Task> _runningJobs = new();
    // jobId → 正在处理中的文件集合（Phase1 加入、Phase3 完成移除）
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _activeFiles = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _jobLock = new(1, 1);
    private const int MaxCompletedJobs = 20;

    internal static Func<Task>? AfterRecoveryLeaseAcquiredHook { get; set; }
    internal static Func<Task>? BeforeCommitVerifiedHook { get; set; }

    public IndexService(
        IServiceProvider serviceProvider,
        IIndexEventPublisher events,
        ConfigManager configManager,
        IFileIndexPipeline pipeline,
        IFileProcessingGuard guard,
        ILogger<IndexService> logger,
        IIndexStorageAdapter? storageAdapter = null,
        IndexStrategyFactory? strategyFactory = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storageAdapter = storageAdapter;
        _strategyFactory = strategyFactory ?? new IndexStrategyFactory();
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

        var stateMachine = new IndexJobStateMachine(job, _events, _logger);
        job.StateMachine = stateMachine;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        _jobCancellationTokens[indexId] = cts;
        _activeJobs[indexId] = job;

        var work = Task.Run(async () =>
        {
            var heldFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var activeFiles = _activeFiles.GetOrAdd(indexId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            LexicalIndexCoordinator.VectorRecoveryLease? recovery = null;

            await _jobLock.WaitAsync(cts.Token);
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var config = _configManager.Load();
                if (!config.Indexing.EmbeddingEnabled)
                {
                    if (!config.Search.EnableLexical)
                        throw new InvalidOperationException("禁用向量索引前必须启用词法检索。");
                    if (request.VectorOnly)
                        throw new InvalidOperationException("当前已禁用 embedding，不能执行仅向量重建。");
                    await RunLexicalIndexAsync(request, config, scope.ServiceProvider, stateMachine, cts.Token);
                    return;
                }
                var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
                var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
                var recoveryRequired = request.Force && !request.VectorOnly && !request.Rechunk && request.Files == null &&
                    IsFullSourceSelection(config, request.Sources) &&
                    LexicalIndexCoordinator.HasPersistedStaleVectors(Path.Combine(config.DataPath ?? "data", "vectors.db"));
                string? recoveryFingerprint = null;
                IBM25Store? recoveryBm25 = null;
                string[] recoveryVerifiedSources = Array.Empty<string>();
                if (recoveryRequired)
                {
                    recoveryFingerprint = VectorCoverageVerifier.Fingerprint(config);
                    recoveryVerifiedSources = config.Sources.Where(s => s.Enabled).Select(s => s.Name).ToArray();
                    var coordinator = scope.ServiceProvider.GetRequiredService<LexicalIndexCoordinator>();
                    var oldFiles = await vectorStore.GetAllFilesAsync(cts.Token);
                    var sourceNames = config.Sources.Select(s => s.Name)
                        .Concat(oldFiles.Select(f => f.Source).Where(s => !string.IsNullOrWhiteSpace(s))!);
                    recovery = await coordinator.BeginVectorRecoveryAsync(sourceNames!, cts.Token);
                    if (AfterRecoveryLeaseAcquiredHook != null)
                        await AfterRecoveryLeaseAcquiredHook();
                    recoveryBm25 = scope.ServiceProvider.GetRequiredService<IBM25Store>();
                    var graph = scope.ServiceProvider.GetRequiredService<IGraphStore>();
                    await recoveryBm25.ClearIndexAsync();
                    await graph.ClearAllAsync(cts.Token);
                }

                // 解析存储适配器：优先使用 DI 或传入的适配器，兜底使用基于当前作用域构建的适配器
                var storageAdapter = scope.ServiceProvider.GetService<IIndexStorageAdapter>()
                    ?? _storageAdapter
                    ?? new SqliteIndexStorageAdapter(
                        vectorStore,
                        _pipeline,
                        _guard,
                        scope.ServiceProvider.GetService<ILogger<SqliteIndexStorageAdapter>>() ?? NullLogger<SqliteIndexStorageAdapter>.Instance);

                await _events.PublishAsync("IndexStarted", new IndexStartedEvent
                {
                    IndexId = indexId,
                    TotalFiles = 0,
                    StartTime = job.StartTime,
                    TargetResource = stateMachine.TargetResource,
                    JobType = stateMachine.JobType
                });

                var executionContext = new IndexExecutionContext
                {
                    Request = request,
                    StorageAdapter = storageAdapter,
                    Pipeline = _pipeline,
                    VectorStore = vectorStore,
                    EmbeddingService = embeddingService,
                    ConfigManager = _configManager,
                    Guard = _guard,
                    ActiveFiles = activeFiles,
                    HeldFiles = heldFiles,
                    Logger = _logger,
                    StrictRecovery = recoveryRequired
                };

                // 策略模式：根据请求分发执行策略（Incremental / VectorOnly / Rebuild）
                var strategy = _strategyFactory.GetStrategy(request);
                _logger.LogInformation("任务 {IndexId} 选定执行策略: {Strategy}", indexId, strategy.Name);

                var result = await strategy.ExecuteAsync(executionContext, stateMachine, cts.Token);

                if (!result.Success)
                {
                    throw new InvalidOperationException(result.ErrorMessage ?? "索引执行失败");
                }
                if (recovery != null)
                {
                    if (VectorCoverageVerifier.Fingerprint(_configManager.Load()) != recoveryFingerprint)
                        throw new InvalidOperationException("向量重建期间配置发生变化，请重新执行完整重建。");
                    var sources = config.Sources.Where(s => s.Enabled).ToArray();
                    var diskBeforeCommit = VectorCoverageVerifier.ScanSources(sources, cts.Token);
                    var verifier = new VectorCoverageVerifier(vectorStore, recoveryBm25!, embeddingService, _guard, _configManager.Load);
                    await verifier.VerifyAndCommitAsync(config, async () =>
                    {
                        if (BeforeCommitVerifiedHook != null)
                            await BeforeCommitVerifiedHook();

                        if (VectorCoverageVerifier.Fingerprint(_configManager.Load()) != recoveryFingerprint)
                            throw new InvalidOperationException("提交覆盖状态前配置发生变化，请重新执行完整重建。");

                        var diskNow = VectorCoverageVerifier.ScanSources(sources, cts.Token);
                        if (!VectorCoverageVerifier.SameInventory(diskBeforeCommit, diskNow))
                            throw new InvalidOperationException("提交覆盖状态前磁盘文件发生变化，请重新执行完整重建。");

                        await recovery.CompleteVerifiedAsync(recoveryVerifiedSources, cts.Token);
                    }, cts.Token);
                    stateMachine.ReportCompleted(DateTime.UtcNow - job.StartTime);
                }
            }
            catch (OperationCanceledException)
            {
                if (recovery?.IsCompleted != true)
                {
                    stateMachine.ReportCancelled();
                    _logger.LogInformation("索引任务 {IndexId} 已取消", indexId);
                }
            }
            catch (Exception ex)
            {
                stateMachine.ReportFaulted(ex);
                _logger.LogError(ex, "索引任务 {IndexId} 执行异常失败", indexId);
            }
            finally
            {
                recovery?.Dispose();
                _jobLock.Release();
                foreach (var file in heldFiles.Keys)
                {
                    _guard.Release(file);
                }

                _activeFiles.TryRemove(indexId, out _);

                if (_jobCancellationTokens.TryRemove(indexId, out var removedCts))
                {
                    removedCts.Dispose();
                }

                if (job.Status is IndexStatus.Completed or IndexStatus.Failed or IndexStatus.Cancelled)
                {
                    _completedJobs.Enqueue(job);
                    while (_completedJobs.Count > MaxCompletedJobs)
                    {
                        _completedJobs.TryDequeue(out _);
                    }
                }

                _activeJobs.TryRemove(indexId, out _);
            }
        });

        _runningJobs[indexId] = work;
        _ = work.ContinueWith(_ => { _runningJobs.TryRemove(indexId, out Task? _); }, TaskScheduler.Default);

        return job;
    }

    private async Task RunLexicalIndexAsync(IndexRequest request, ObsidianRagConfig config,
        IServiceProvider services, IndexJobStateMachine state, CancellationToken ct)
    {
        var indexer = services.GetRequiredService<LexicalFileIndexer>();
        var coordinator = services.GetRequiredService<LexicalIndexCoordinator>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        state.TransitionTo(IndexJobPhase.Scanning, "扫描词法索引文件");
        await _events.PublishAsync("IndexStarted", new IndexStartedEvent
        {
            IndexId = state.JobId, TotalFiles = 0, StartTime = state.StartTime,
            TargetResource = state.TargetResource, JobType = state.JobType
        }, ct);

        var sources = config.Sources.Where(s => s.Enabled &&
            (request.Sources is not { Count: > 0 } || request.Sources.Contains(s.Name, StringComparer.OrdinalIgnoreCase))).ToList();
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (request.Files != null)
        {
            foreach (var path in request.Files)
            {
                var fullPath = Path.GetFullPath(path);
                var source = FindLexicalSource(fullPath, sources);
                if (source == null || !IsLexicalFileAllowed(fullPath, source))
                    throw new InvalidOperationException("索引文件不在启用的源目录中，或被源配置排除。");
                paths.Add(fullPath);
            }
        }
        else
        {
            foreach (var source in sources)
                foreach (var path in EnumerateLexicalFiles(source, ct)) paths.Add(path);
        }

        state.TotalFiles = paths.Count;
        state.TransitionTo(IndexJobPhase.Finalizing, "建立词法索引");
        var processed = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            var source = FindLexicalSource(path, sources) ?? throw new InvalidOperationException("索引源已改变。");
            if (!_guard.TryAcquire(path)) throw new InvalidOperationException($"文件正在索引: {path}");
            try
            {
                var file = await indexer.IndexAsync(path, source.Name, request.Force || request.Rechunk, ct);
                state.TotalChunks += file.ChunkCount;
                state.ReportFinalizingProgress(path, ++processed, paths.Count);
            }
            finally { _guard.Release(path); }
        }

        // Only a complete, successful source scan may remove vanished files.
        if (request.Files == null)
        {
            var names = sources.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in await coordinator.GetActiveFilesAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                if (file.Source == null || !names.Contains(file.Source) || paths.Contains(file.Path) || File.Exists(file.Path)) continue;
                if (!_guard.TryAcquire(file.Path))
                    throw new InvalidOperationException($"已删除文件仍在处理，无法完成索引清理: {file.Path}");
                try
                {
                    if (!File.Exists(file.Path)) await indexer.DeleteAsync(file.Path, ct);
                }
                finally { _guard.Release(file.Path); }
            }
        }
        state.ProcessedFiles = processed;
        state.TotalVectors = 0;
        state.ReportCompleted(watch.Elapsed);
    }

    internal static SourceFolder? FindLexicalSource(string path, IEnumerable<SourceFolder> sources)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return sources.Where(source =>
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path));
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return path.Equals(root, comparison) || path.StartsWith(prefix, comparison);
        }).OrderByDescending(source => Path.GetFullPath(source.Path).Length).FirstOrDefault();
    }

    public static bool IsFullSourceSelection(ObsidianRagConfig config, IReadOnlyCollection<string>? requested)
    {
        if (requested is not { Count: > 0 }) return true;
        var enabled = config.Sources.Where(s => s.Enabled).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return enabled.Count > 0 && enabled.SetEquals(requested);
    }

    internal static bool IsLexicalFileAllowed(string path, SourceFolder source)
    {
        if (!IsLexicalPathAllowed(path, source)) return false;
        var root = Path.GetFullPath(source.Path);
        var relative = Path.GetRelativePath(root, path);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = root;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return false;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }

    internal static bool IsLexicalPathAllowed(string path, SourceFolder source)
    {
        var root = Path.GetFullPath(source.Path);
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length == 0 || parts.Take(parts.Length - 1).Any(p => source.ExcludeDirs?.Contains(p, StringComparer.OrdinalIgnoreCase) == true)) return false;
        var fileName = parts[^1];
        var patterns = source.FilePatterns is { Count: > 0 } ? source.FilePatterns : new List<string> { "*.md", "*.txt" };
        if (!patterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true))) return false;
        if (source.ExcludeFiles?.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true)) == true) return false;
        return true;
    }

    internal static IEnumerable<string> EnumerateLexicalFiles(SourceFolder source, CancellationToken ct)
    {
        var root = Path.GetFullPath(source.Path);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"索引源不可访问: {root}");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"索引源不能是重解析点: {root}");
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var name = Path.GetFileName(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!(source.ExcludeDirs?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false)) pending.Push(entry);
                    continue;
                }
                var patterns = source.FilePatterns is { Count: > 0 } ? source.FilePatterns : new List<string> { "*.md", "*.txt" };
                if (!patterns.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true))) continue;
                if (source.ExcludeFiles?.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)) == true) continue;
                yield return Path.GetFullPath(entry);
            }
        }
    }

    public Task<bool> StopIndexAsync(string indexId)
    {
        if (_activeJobs.TryGetValue(indexId, out var job) &&
            job.Status is IndexStatus.Running or IndexStatus.Pending)
        {
            if (_jobCancellationTokens.TryGetValue(indexId, out var cts))
            {
                cts.Cancel();
            }

            job.Status = IndexStatus.Cancelled;
            job.StateMachine?.ReportCancelled();
            return Task.FromResult(true);
        }

        _logger.LogWarning("无法停止 {IndexId}：任务不存在或状态不允许 (当前: {Status})",
            indexId, _activeJobs.TryGetValue(indexId, out var j) ? j.Status.ToString() : "null");
        return Task.FromResult(false);
    }

    public IndexJob? GetStatus(string indexId) =>
        _activeJobs.TryGetValue(indexId, out var job) ? job : null;

    public async Task<IndexJob> WaitForCompletionAsync(IndexJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (_runningJobs.TryGetValue(job.Id, out var task))
            await task.WaitAsync(cancellationToken);
        if (job.Status is IndexStatus.Pending or IndexStatus.Running)
            throw new InvalidOperationException("索引任务尚未结束且无法跟踪其运行状态。");
        return job;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Index service started (Adapter + Strategy + StateMachine enabled)");
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
    /// <summary>重建切片：按当前最新分块配置重新切分，原子替换旧切片并重算向量与检索索引</summary>
    public bool Rechunk { get; set; }
}

public class IndexJob
{
    private string _currentFile = string.Empty;

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

    public string CurrentFile
    {
        get => StateMachine?.CurrentFile ?? _currentFile;
        set => _currentFile = value;
    }

    /// <summary>
    /// 正在处理中的文件集合
    /// </summary>
    public List<string> CurrentFiles { get; set; } = new();

    public string? ErrorMessage { get; set; }

    [JsonIgnore]
    public IndexJobStateMachine? StateMachine { get; set; }

    /// <summary>
    /// 平滑综合进度计算：优先从状态机动态获取全阶段（扫描/分块/GPU推理/收尾）推进百分比
    /// </summary>
    public double ProgressPercent => StateMachine != null
        ? StateMachine.CalculateProgress()
        : (TotalFiles > 0 ? (double)(ProcessedFiles + SkippedFiles) / TotalFiles * 100 : 0);
}

public enum IndexStatus
{
    Pending, Running, Completed, Failed, Cancelled
}
