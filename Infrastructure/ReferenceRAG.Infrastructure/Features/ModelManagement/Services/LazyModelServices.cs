using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Features.ModelManagement.StateMachine;
using ReferenceRAG.Core.Features.ModelManagement.Strategies;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services.Rerank;
using ReferenceRAG.Core.Tracing;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 状态机驱动的高性能多后端延迟加载向量嵌入服务（适配器 + 策略 + 状态机）
/// </summary>
internal sealed class LazyEmbeddingService : IEmbeddingService, IModelRuntimeTelemetryProvider, IModelRetentionPolicyTarget, IDisposable
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("LazyEmbeddingService");

    private readonly EmbeddingOptions _options;
    private readonly IGpuMemoryManager? _memoryManager;
    private readonly Func<EmbeddingService>? _legacyFactory;
    private readonly IWindowStateService? _windowStateService;
    private readonly ConfigManager? _configManager;
    private readonly ModelLifecycleStateMachine<IEmbeddingBackendAdapter> _stateMachine;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private ModelRetentionStrategy _retentionStrategy = ModelRetentionStrategy.SlidingExpiration;
    private int _idleTimeoutSeconds = 300;
    private bool _unloadOnlyWhenMinimizedToTray = true;
    private string _modelPath;
    private string _modelName;

    public LazyEmbeddingService(
        EmbeddingOptions options,
        IGpuMemoryManager? memoryManager,
        Func<EmbeddingService>? factory = null,
        IWindowStateService? windowStateService = null,
        ConfigManager? configManager = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _memoryManager = memoryManager;
        _legacyFactory = factory;
        _windowStateService = windowStateService;
        _configManager = configManager;
        _modelPath = options.ModelPath;
        _modelName = options.ModelName;

        if (_configManager != null)
        {
            var cfg = _configManager.Load();
            _retentionStrategy = cfg.ModelRetentionStrategy;
            _idleTimeoutSeconds = cfg.ModelIdleTimeoutSeconds;
            _unloadOnlyWhenMinimizedToTray = cfg.UnloadOnlyWhenMinimizedToTray;
        }
        else
        {
            _idleTimeoutSeconds = options.IdleTimeoutSeconds;
        }

        var idleTimeout = _retentionStrategy == ModelRetentionStrategy.SlidingExpiration && _idleTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(_idleTimeoutSeconds)
            : (TimeSpan?)null;

        _stateMachine = new ModelLifecycleStateMachine<IEmbeddingBackendAdapter>(
            "EmbeddingService",
            memoryManager,
            idleTimeout,
            CanUnload);

        if (_windowStateService != null)
        {
            _windowStateService.MinimizedToTrayChanged += OnWindowStateChanged;
        }

        Dimension = ReadConfiguredDimension(options.ModelPath);

        if (options.UseCuda && memoryManager is not null)
        {
            memoryManager.Register(
                "EmbeddingService",
                () => null,
                options.CudaDeviceId,
                () => _stateMachine.UnloadAsync(),
                () => _stateMachine.IsReady);
        }
    }

    internal bool IsModelLoaded => _stateMachine.IsReady;
    public string ModelName => _modelName;
    public int Dimension { get; private set; }
    public bool IsSimulationMode => CurrentAdapter?.IsSimulationMode ?? false;
    public bool SupportsAsymmetricEncoding => CurrentAdapter?.SupportsAsymmetricEncoding ?? (_options.AsymmetricEncoding != null);

    public IEmbeddingBackendAdapter? CurrentAdapter => _stateMachine.CurrentAdapter;
    public EmbeddingOptions CurrentOptions => _options;

    public ModelLifecycleSnapshot GetLifecycleSnapshot()
    {
        var state = _stateMachine.State;
        return new ModelLifecycleSnapshot
        {
            State = state,
            StateDescription = GetStateDescription(state),
            ActiveLeases = _stateMachine.ActiveLeases,
            IsReady = _stateMachine.IsReady,
            LastError = _stateMachine.LastError?.Message
        };
    }

    internal static string GetStateDescription(ModelState state) => state switch
    {
        ModelState.Unloaded => "未加载（内存无模型权重）",
        ModelState.Loading => "正在初始化权重与分配显存...",
        ModelState.Ready => "就绪（可随时响应推理请求）",
        ModelState.Inferring => "推理中（活跃执行前向计算）",
        ModelState.Unloading => "卸载排空中（安全释放非托管资源）",
        ModelState.Faulted => "故障异常（请查看错误详情）",
        ModelState.Disposed => "已销毁",
        _ => state.ToString()
    };

    private async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_stateMachine.IsReady && _stateMachine.CurrentAdapter != null) return;

        var sw = Stopwatch.StartNew();
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_stateMachine.IsReady && _stateMachine.CurrentAdapter != null) return;

            var success = await _stateMachine.TwoPhaseReloadAsync(async () =>
            {
                // 如果传入了旧版 Legacy Factory 且未命中 GGUF 或 OpenAI，保留回退兼容
                if (_legacyFactory != null &&
                    !_modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
                    !Directory.Exists(_modelPath) ||
                    (_legacyFactory != null && File.Exists(_modelPath) && _modelPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)))
                {
                    var legacyService = _legacyFactory();
                    var adapter = new OnnxEmbeddingAdapter(_options, _memoryManager);
                    await adapter.InitializeAsync(cancellationToken);
                    return adapter;
                }

                var embeddingConfig = new EmbeddingConfig
                {
                    ModelPath = _modelPath,
                    ModelName = _modelName,
                    MaxSequenceLength = _options.MaxSequenceLength,
                    BatchSize = _options.BatchSize,
                    UseCuda = _options.UseCuda,
                    CudaDeviceId = _options.CudaDeviceId,
                    CudaLibraryPath = _options.CudaLibraryPath
                };

                var strategy = ModelBackendStrategyFactory.ResolveStrategy(_modelPath);
                return await strategy.CreateEmbeddingAdapterAsync(_modelPath, _modelName, embeddingConfig, _memoryManager, cancellationToken);
            }, cancellationToken);

            if (success)
            {
                Dimension = _stateMachine.CurrentAdapter?.Dimension ?? Dimension;
            }
        }
        finally
        {
            _initLock.Release();
            sw.Stop();
            SearchTraceContext.RecordModelLoad(sw.ElapsedMilliseconds);
        }
    }

    private static int ReadConfiguredDimension(string modelPath)
    {
        try
        {
            if (string.IsNullOrEmpty(modelPath)) return 0;
            var directory = Directory.Exists(modelPath) ? modelPath : Path.GetDirectoryName(modelPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return 0;

            foreach (var path in new[] { Path.Combine(directory, "1_Pooling", "config.json"), Path.Combine(directory, "config.json") })
            {
                if (!File.Exists(path)) continue;
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.TryGetProperty("word_embedding_dimension", out var pooling)) return pooling.GetInt32();
                if (json.RootElement.TryGetProperty("hidden_size", out var hidden)) return hidden.GetInt32();
            }
        }
        catch { }
        return 0;
    }

    public async Task<bool> ReloadModelAsync(string modelPath, string modelName, int? maxSequenceLength = null)
    {
        _logger.LogInformation("[LazyEmbedding] 触发模型重载: {Path} ({Name})", modelPath, modelName);

        var embeddingConfig = new EmbeddingConfig
        {
            ModelPath = modelPath,
            ModelName = modelName,
            MaxSequenceLength = maxSequenceLength ?? _options.MaxSequenceLength,
            BatchSize = _options.BatchSize,
            UseCuda = _options.UseCuda,
            CudaDeviceId = _options.CudaDeviceId,
            CudaLibraryPath = _options.CudaLibraryPath
        };

        var success = await _stateMachine.TwoPhaseReloadAsync(async () =>
        {
            var strategy = ModelBackendStrategyFactory.ResolveStrategy(modelPath);
            return await strategy.CreateEmbeddingAdapterAsync(modelPath, modelName, embeddingConfig, _memoryManager);
        });

        if (success)
        {
            _modelPath = modelPath;
            _modelName = modelName;
            _options.ModelPath = modelPath;
            _options.ModelName = modelName;
            if (maxSequenceLength.HasValue) _options.MaxSequenceLength = maxSequenceLength.Value;

            Dimension = _stateMachine.CurrentAdapter?.Dimension ?? Dimension;
            return true;
        }

        return false;
    }

    public void UnloadModel()
    {
        _stateMachine.UnloadAsync().GetAwaiter().GetResult();
    }

    public async Task<float[]> EncodeAsync(string text, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        var sw = Stopwatch.StartNew();
        var res = await _stateMachine.ExecuteInferenceAsync(adapter => adapter.EncodeAsync(text, null, cancellationToken), cancellationToken);
        sw.Stop();
        SearchTraceContext.RecordEmbed(sw.ElapsedMilliseconds);
        return res;
    }

    public async Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        var list = texts as IReadOnlyList<string> ?? texts.ToList();
        var sw = Stopwatch.StartNew();
        var res = await _stateMachine.ExecuteInferenceAsync(adapter => adapter.EncodeBatchAsync(list, null, cancellationToken), cancellationToken);
        sw.Stop();
        SearchTraceContext.RecordEmbed(sw.ElapsedMilliseconds);
        return res;
    }

    public async Task<float[]> EncodeAsync(string text, EmbeddingMode mode, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        var prefix = ResolvePrefix(mode);
        var sw = Stopwatch.StartNew();
        var res = await _stateMachine.ExecuteInferenceAsync(adapter => adapter.EncodeAsync(text, prefix, cancellationToken), cancellationToken);
        sw.Stop();
        SearchTraceContext.RecordEmbed(sw.ElapsedMilliseconds);
        return res;
    }

    public async Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, EmbeddingMode mode, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        var prefix = ResolvePrefix(mode);
        var list = texts as IReadOnlyList<string> ?? texts.ToList();
        var sw = Stopwatch.StartNew();
        var res = await _stateMachine.ExecuteInferenceAsync(adapter => adapter.EncodeBatchAsync(list, prefix, cancellationToken), cancellationToken);
        sw.Stop();
        SearchTraceContext.RecordEmbed(sw.ElapsedMilliseconds);
        return res;
    }

    private string? ResolvePrefix(EmbeddingMode mode)
    {
        if (_options.AsymmetricEncoding == null) return null;
        return mode switch
        {
            EmbeddingMode.Query => _options.AsymmetricEncoding.QueryPrefix,
            EmbeddingMode.Document => _options.AsymmetricEncoding.DocumentPrefix,
            _ => null
        };
    }

    public float[] Normalize(float[] vector)
    {
        var sum = 0f;
        for (var i = 0; i < vector.Length; i++) sum += vector[i] * vector[i];
        var norm = MathF.Sqrt(sum);
        if (norm < 1e-10f) return vector;
        for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
        return vector;
    }

    public float Similarity(float[] a, float[] b)
    {
        var dot = 0f;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    private bool CanUnload()
    {
        if (_retentionStrategy == ModelRetentionStrategy.Permanent)
            return false;

        if (_idleTimeoutSeconds <= 0)
            return false;

        if (_unloadOnlyWhenMinimizedToTray)
        {
            return _windowStateService == null || _windowStateService.IsMinimizedToTray;
        }

        return true;
    }

    private void OnWindowStateChanged(object? sender, bool isMinimized)
    {
        _logger.LogInformation("[LazyEmbedding] 托盘状态变更: IsMinimized = {IsMinimized}", isMinimized);
        if (isMinimized)
        {
            if (_retentionStrategy == ModelRetentionStrategy.SlidingExpiration && _idleTimeoutSeconds > 0)
            {
                _stateMachine.ResetIdleTimer();
            }
        }
        else
        {
            _stateMachine.CancelIdleTimer();
        }
    }

    public void UpdateRetentionPolicy(ModelRetentionStrategy strategy, int idleTimeoutSeconds, bool unloadOnlyWhenMinimizedToTray)
    {
        _retentionStrategy = strategy;
        _idleTimeoutSeconds = idleTimeoutSeconds;
        _unloadOnlyWhenMinimizedToTray = unloadOnlyWhenMinimizedToTray;

        var timeout = strategy == ModelRetentionStrategy.SlidingExpiration && idleTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(idleTimeoutSeconds)
            : (TimeSpan?)null;

        _logger.LogInformation("[LazyEmbedding] 驻留策略更新: Strategy={Strategy}, Timeout={Timeout}s, OnlyTray={OnlyTray}",
            strategy, idleTimeoutSeconds, unloadOnlyWhenMinimizedToTray);

        _stateMachine.UpdateRetentionPolicy(timeout, CanUnload);
    }

    public void UpdateRetentionPolicy(ObsidianRagConfig config)
    {
        if (config == null) return;
        UpdateRetentionPolicy(config.ModelRetentionStrategy, config.ModelIdleTimeoutSeconds, config.UnloadOnlyWhenMinimizedToTray);
    }

    public void Dispose()
    {
        if (_windowStateService != null)
        {
            _windowStateService.MinimizedToTrayChanged -= OnWindowStateChanged;
        }
        UnloadModel();
        if (_options.UseCuda)
        {
            _memoryManager?.Unregister("EmbeddingService");
        }
        _stateMachine.Dispose();
        _initLock.Dispose();
    }
}

/// <summary>
/// 状态机驱动的高性能多后端延迟加载重排服务（适配器 + 策略 + 状态机）
/// </summary>
internal sealed class LazyRerankService : IRerankService, IModelRetentionPolicyTarget, IRerankRuntimeTelemetryProvider, IDisposable
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("LazyRerankService");

    private readonly Func<OnnxRerankService>? _legacyFactory;
    private readonly IGpuMemoryManager? _memoryManager;
    private readonly bool _useCuda;
    private readonly int _deviceId;
    private readonly IWindowStateService? _windowStateService;
    private readonly ConfigManager? _configManager;
    private readonly ModelLifecycleStateMachine<IRerankBackendAdapter> _stateMachine;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private ModelRetentionStrategy _retentionStrategy = ModelRetentionStrategy.SlidingExpiration;
    private int _idleTimeoutSeconds = 300;
    private bool _unloadOnlyWhenMinimizedToTray = true;
    private string _modelName;
    private string _modelPath;

    public LazyRerankService(
        string modelName,
        Func<OnnxRerankService>? factory = null,
        IGpuMemoryManager? memoryManager = null,
        bool useCuda = false,
        int deviceId = 0,
        string modelPath = "",
        int idleTimeoutSeconds = 30,
        IWindowStateService? windowStateService = null,
        ConfigManager? configManager = null)
    {
        _modelName = modelName;
        _modelPath = modelPath;
        _legacyFactory = factory;
        _memoryManager = memoryManager;
        _useCuda = useCuda;
        _deviceId = deviceId;
        _windowStateService = windowStateService;
        _configManager = configManager;

        if (_configManager != null)
        {
            var cfg = _configManager.Load();
            _retentionStrategy = cfg.ModelRetentionStrategy;
            _idleTimeoutSeconds = cfg.ModelIdleTimeoutSeconds;
            _unloadOnlyWhenMinimizedToTray = cfg.UnloadOnlyWhenMinimizedToTray;
        }
        else
        {
            _idleTimeoutSeconds = idleTimeoutSeconds;
        }

        var timeout = _retentionStrategy == ModelRetentionStrategy.SlidingExpiration && _idleTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(_idleTimeoutSeconds)
            : (TimeSpan?)null;

        _stateMachine = new ModelLifecycleStateMachine<IRerankBackendAdapter>(
            "OnnxRerankService",
            memoryManager,
            timeout,
            CanUnload);

        if (_windowStateService != null)
        {
            _windowStateService.MinimizedToTrayChanged += OnWindowStateChanged;
        }

        if (useCuda && memoryManager is not null)
        {
            memoryManager.Register(
                "OnnxRerankService",
                () => null,
                deviceId,
                () => _stateMachine.UnloadAsync(),
                () => _stateMachine.IsReady);
        }
    }

    internal bool IsModelLoaded => _stateMachine.IsReady;
    public string ModelName => _modelName;
    public bool IsLoaded => _stateMachine.IsReady;
    public string ModelPath => _modelPath;
    public bool UseCuda => _useCuda;
    public int DeviceId => _deviceId;
    public IRerankBackendAdapter? CurrentAdapter => _stateMachine.CurrentAdapter;

    /// <summary>
    /// 获取当前重排模型的状态机生命周期快照
    /// </summary>
    public ModelLifecycleSnapshot GetLifecycleSnapshot()
    {
        var state = _stateMachine.State;
        return new ModelLifecycleSnapshot
        {
            State = state,
            StateDescription = LazyEmbeddingService.GetStateDescription(state),
            ActiveLeases = _stateMachine.ActiveLeases,
            IsReady = _stateMachine.IsReady,
            LastError = _stateMachine.LastError?.Message
        };
    }

    private async Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_stateMachine.IsReady && _stateMachine.CurrentAdapter != null) return;

        var sw = Stopwatch.StartNew();
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_stateMachine.IsReady && _stateMachine.CurrentAdapter != null) return;

            var success = await _stateMachine.TwoPhaseReloadAsync(async () =>
            {
                if (_legacyFactory != null &&
                    !_modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                {
                    var legacyService = _legacyFactory();
                    var cfg = _configManager?.Load();
                    var options = new RerankOptions
                    {
                        ModelPath = _modelPath,
                        ModelName = _modelName,
                        UseCuda = _useCuda,
                        CudaDeviceId = _deviceId,
                        CudaLibraryPath = cfg?.Embedding?.CudaLibraryPath
                    };
                    var adapter = new OnnxRerankAdapter(options, _memoryManager);
                    await adapter.InitializeAsync(cancellationToken);
                    return adapter;
                }

                var config = new RerankConfig
                {
                    ModelName = _modelName,
                    ModelPath = _modelPath,
                    UseCuda = _useCuda,
                    CudaDeviceId = _deviceId
                };

                var strategy = ModelBackendStrategyFactory.ResolveStrategy(_modelPath);
                return await strategy.CreateRerankAdapterAsync(_modelPath, _modelName, config, _memoryManager, cancellationToken);
            }, cancellationToken);
        }
        finally
        {
            _initLock.Release();
            sw.Stop();
            SearchTraceContext.RecordModelLoad(sw.ElapsedMilliseconds);
        }
    }

    public async Task<double> RerankAsync(string query, string document, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        var sw = Stopwatch.StartNew();
        var res = await _stateMachine.ExecuteInferenceAsync(adapter => adapter.RerankAsync(query, document, cancellationToken), cancellationToken);
        sw.Stop();
        SearchTraceContext.RecordRerank(sw.ElapsedMilliseconds);
        return res;
    }

    public async Task<RerankResult> RerankBatchAsync(string query, IEnumerable<string> documents, CancellationToken cancellationToken = default)
    {
        await EnsureReadyAsync(cancellationToken);
        var sw = Stopwatch.StartNew();
        var res = await _stateMachine.ExecuteInferenceAsync(adapter => adapter.RerankBatchAsync(query, documents, cancellationToken), cancellationToken);
        sw.Stop();
        SearchTraceContext.RecordRerank(sw.ElapsedMilliseconds);
        return res;
    }

    public async Task<bool> ReloadModelAsync(string path, string name)
    {
        _logger.LogInformation("[LazyRerank] 触发重排模型重载: {Path} ({Name})", path, name);

        var config = new RerankConfig
        {
            ModelName = name,
            ModelPath = path,
            UseCuda = _useCuda,
            CudaDeviceId = _deviceId
        };

        var success = await _stateMachine.TwoPhaseReloadAsync(async () =>
        {
            var strategy = ModelBackendStrategyFactory.ResolveStrategy(path);
            return await strategy.CreateRerankAdapterAsync(path, name, config, _memoryManager);
        });

        if (success)
        {
            _modelPath = path;
            _modelName = name;
            return true;
        }

        return false;
    }

    public void UnloadModel()
    {
        _stateMachine.UnloadAsync().GetAwaiter().GetResult();
    }

    private bool CanUnload()
    {
        if (_retentionStrategy == ModelRetentionStrategy.Permanent)
            return false;

        if (_idleTimeoutSeconds <= 0)
            return false;

        if (_unloadOnlyWhenMinimizedToTray)
        {
            return _windowStateService == null || _windowStateService.IsMinimizedToTray;
        }

        return true;
    }

    private void OnWindowStateChanged(object? sender, bool isMinimized)
    {
        _logger.LogInformation("[LazyRerank] 托盘状态变更: IsMinimized = {IsMinimized}", isMinimized);
        if (isMinimized)
        {
            if (_retentionStrategy == ModelRetentionStrategy.SlidingExpiration && _idleTimeoutSeconds > 0)
            {
                _stateMachine.ResetIdleTimer();
            }
        }
        else
        {
            _stateMachine.CancelIdleTimer();
        }
    }

    public void UpdateRetentionPolicy(ModelRetentionStrategy strategy, int idleTimeoutSeconds, bool unloadOnlyWhenMinimizedToTray)
    {
        _retentionStrategy = strategy;
        _idleTimeoutSeconds = idleTimeoutSeconds;
        _unloadOnlyWhenMinimizedToTray = unloadOnlyWhenMinimizedToTray;

        var timeout = strategy == ModelRetentionStrategy.SlidingExpiration && idleTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(idleTimeoutSeconds)
            : (TimeSpan?)null;

        _logger.LogInformation("[LazyRerank] 驻留策略更新: Strategy={Strategy}, Timeout={Timeout}s, OnlyTray={OnlyTray}",
            strategy, idleTimeoutSeconds, unloadOnlyWhenMinimizedToTray);

        _stateMachine.UpdateRetentionPolicy(timeout, CanUnload);
    }

    public void UpdateRetentionPolicy(ObsidianRagConfig config)
    {
        if (config == null) return;
        UpdateRetentionPolicy(config.ModelRetentionStrategy, config.ModelIdleTimeoutSeconds, config.UnloadOnlyWhenMinimizedToTray);
    }

    public void Dispose()
    {
        if (_windowStateService != null)
        {
            _windowStateService.MinimizedToTrayChanged -= OnWindowStateChanged;
        }
        UnloadModel();
        if (_useCuda)
        {
            _memoryManager?.Unregister("OnnxRerankService");
        }
        _stateMachine.Dispose();
        _initLock.Dispose();
    }
}
