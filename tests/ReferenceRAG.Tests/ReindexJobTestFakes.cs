using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Features.ModelManagement.Services;

namespace ReferenceRAG.Tests;

/// <summary>
/// 重构索引门禁测试共享桩：真实非零单位向量 + 可配置维度/模拟模式/延迟/失败注入。
/// 解析器复刻 SpecEmbeddingAdapterResolver 的硬门禁（模拟模式/维度不符一律抛异常，禁止静默回退）。
/// </summary>
internal sealed class FakeEmbeddingAdapterOptions
{
    /// <summary>0 = 跟随目标规格维度；>0 = 强制适配器输出维度（用于维度不匹配门禁）</summary>
    public int DimensionOverride { get; set; }

    public bool SimulationMode { get; set; }

    public TimeSpan BatchDelay { get; set; } = TimeSpan.Zero;

    /// <summary>首批编码前阻塞等待此闸门（用于验证入队响应先于任何编码发生）</summary>
    public TaskCompletionSource? FirstBatchGate { get; set; }

    public Exception? EncodeFailure { get; set; }
}

internal sealed class FakeEmbeddingAdapterStats
{
    private int _createCalls;
    private int _batchCalls;
    private int _encodedTexts;

    public int CreateCalls => Volatile.Read(ref _createCalls);
    public int BatchCalls => Volatile.Read(ref _batchCalls);
    public int EncodedTexts => Volatile.Read(ref _encodedTexts);

    internal void OnCreate() => Interlocked.Increment(ref _createCalls);
    internal void OnBatch(int textCount)
    {
        Interlocked.Increment(ref _batchCalls);
        Interlocked.Add(ref _encodedTexts, textCount);
    }
}

internal sealed class FakeEmbeddingAdapter : IEmbeddingBackendAdapter
{
    private readonly EmbeddingSpec _spec;
    private readonly FakeEmbeddingAdapterOptions _options;
    private readonly FakeEmbeddingAdapterStats _stats;
    private int _localBatchCalls;

    public FakeEmbeddingAdapter(EmbeddingSpec spec, FakeEmbeddingAdapterOptions options, FakeEmbeddingAdapterStats stats)
    {
        _spec = spec;
        _options = options;
        _stats = stats;
    }

    public string BackendType => "fake";
    public string ModelPath => "fake://reindex-gate";
    public string ModelName => _spec.ModelName;
    public int Dimension => _options.DimensionOverride > 0 ? _options.DimensionOverride : _spec.Dimension;
    public int MaxSequenceLength => _spec.MaxTokens > 0 ? _spec.MaxTokens : 8192;
    public bool SupportsAsymmetricEncoding => true;
    public bool IsSimulationMode => _options.SimulationMode;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<float[]> EncodeAsync(string text, string? prefix = null, CancellationToken cancellationToken = default)
    {
        var batch = await EncodeBatchAsync(new[] { text }, prefix, cancellationToken);
        return batch[0];
    }

    public async Task<float[][]> EncodeBatchAsync(IReadOnlyList<string> texts, string? prefix = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Increment(ref _localBatchCalls) == 1 && _options.FirstBatchGate != null)
        {
            await _options.FirstBatchGate.Task.WaitAsync(cancellationToken);
        }

        if (_options.BatchDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.BatchDelay, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_options.EncodeFailure != null)
        {
            throw _options.EncodeFailure;
        }

        _stats.OnBatch(texts.Count);
        var result = new float[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
        {
            result[i] = EncodeDeterministicUnitVector((prefix ?? "") + texts[i], Dimension);
        }
        return result;
    }

    public int CountTokens(string text) => text?.Length ?? 0;

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>文本哈希种子的确定性非零单位向量（同文本同向量，跨文本可区分）</summary>
    internal static float[] EncodeDeterministicUnitVector(string text, int dimension)
    {
        var vector = new float[dimension];
        int seed = 17;
        unchecked
        {
            foreach (var ch in text)
            {
                seed = seed * 31 + ch;
            }
        }
        var rng = new Random(seed);
        double sumSquares = 0;
        for (int i = 0; i < dimension; i++)
        {
            double x = rng.NextDouble() * 2 - 1;
            vector[i] = (float)x;
            sumSquares += x * x;
        }
        double norm = Math.Sqrt(sumSquares);
        if (norm < 1e-9)
        {
            vector[0] = 1f;
            return vector;
        }
        for (int i = 0; i < dimension; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }
        return vector;
    }
}

internal sealed class FakeEmbeddingAdapterResolver : IEmbeddingAdapterResolver
{
    private readonly FakeEmbeddingAdapterOptions _options;
    private readonly List<FakeEmbeddingAdapter> _adapters = new();
    private readonly object _lock = new();

    public FakeEmbeddingAdapterResolver(FakeEmbeddingAdapterOptions? options = null)
    {
        _options = options ?? new FakeEmbeddingAdapterOptions();
    }

    public FakeEmbeddingAdapterStats Stats { get; } = new();

    public IReadOnlyList<FakeEmbeddingAdapter> Adapters
    {
        get { lock (_lock) return _adapters.ToArray(); }
    }

    public Task<IEmbeddingBackendAdapter> CreateForSpecAsync(EmbeddingSpec spec, CancellationToken ct = default)
    {
        if (_options.SimulationMode)
        {
            throw new InvalidOperationException($"目标模型「{spec.ModelName}」处于模拟降级模式，禁止用于重建索引");
        }

        var adapter = new FakeEmbeddingAdapter(spec, _options, Stats);
        if (spec.Dimension > 0 && adapter.Dimension > 0 && adapter.Dimension != spec.Dimension)
        {
            throw new InvalidOperationException(
                $"目标模型维度不匹配: 适配器输出 {adapter.Dimension} 维，规范声明 {spec.Dimension} 维 ({spec.Id})");
        }

        Stats.OnCreate();
        lock (_lock) _adapters.Add(adapter);
        return Task.FromResult<IEmbeddingBackendAdapter>(adapter);
    }
}

internal static class ReindexJobTestHelpers
{
    public static bool IsTerminal(JobStatus status) =>
        status is JobStatus.Succeeded or JobStatus.PartiallySucceeded or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>轮询 catalog 直到作业到达终态（后台 Task.Run 工作器与测试线程解耦）</summary>
    public static async Task<JobDefinition> AwaitTerminalAsync(ICatalogStore catalog, string jobId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        JobDefinition? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await catalog.GetJobAsync(jobId);
            if (last != null && IsTerminal(last.Status))
            {
                return last;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"重构作业 {jobId} 未在超时内到达终态（最后状态: {last?.Status.ToString() ?? "null"}）");
    }
}