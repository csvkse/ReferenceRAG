using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Business.Features.Indexing.StateMachine;
using ReferenceRAG.Business.Features.Indexing.Strategies;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Hubs;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

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
    private const int MaxCompletedJobs = 20;

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

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var vectorStore = scope.ServiceProvider.GetRequiredService<IVectorStore>();
                var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();

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
                    Logger = _logger
                };

                // 策略模式：根据请求分发执行策略（Incremental / VectorOnly / Rebuild）
                var strategy = _strategyFactory.GetStrategy(request);
                _logger.LogInformation("任务 {IndexId} 选定执行策略: {Strategy}", indexId, strategy.Name);

                var result = await strategy.ExecuteAsync(executionContext, stateMachine, cts.Token);

                if (!result.Success)
                {
                    throw new InvalidOperationException(result.ErrorMessage ?? "索引执行失败");
                }
            }
            catch (OperationCanceledException)
            {
                stateMachine.ReportCancelled();
                _logger.LogInformation("索引任务 {IndexId} 已取消", indexId);
            }
            catch (Exception ex)
            {
                stateMachine.ReportFaulted(ex);
                _logger.LogError(ex, "索引任务 {IndexId} 执行异常失败", indexId);
            }
            finally
            {
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
