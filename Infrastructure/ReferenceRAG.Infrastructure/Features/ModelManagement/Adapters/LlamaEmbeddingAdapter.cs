using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;

namespace ReferenceRAG.Core.Features.ModelManagement.Adapters;

/// <summary>
/// 基于 LLamaSharp (llama.cpp) 的高性能 GGUF 向量嵌入适配器。
/// 采用常驻上下文 (Persistent LLamaContext) 与原生多序列批处理 (Native Multi-Sequence Batching)，
/// 彻底消除显存频繁分配释放带来的锯齿震荡，大幅提升 GPU GEMM 吞吐与核心利用率。
/// </summary>
public sealed class LlamaEmbeddingAdapter : IEmbeddingBackendAdapter
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("LlamaEmbeddingAdapter");

    private readonly string _modelPath;
    private readonly string _modelName;
    private readonly LLamaPoolingType _poolingType;
    private readonly int _gpuLayers;
    private readonly int _contextSize;
    private readonly SemaphoreSlim _inferenceSemaphore = new(1, 1);

    private LLamaWeights? _weights;
    private LLamaContext? _context;
    private uint _batchTokenCapacity = 512;
    private uint _seqMax = 16;
    private bool _disposed;

    public string BackendType => "llama";
    public string ModelPath => _modelPath;
    public string ModelName => _modelName;
    public int Dimension { get; private set; }
    public int MaxSequenceLength => _contextSize;
    public bool SupportsAsymmetricEncoding => true;
    public bool IsSimulationMode { get; private set; }
    public int GpuLayers => _gpuLayers;
    public LLamaPoolingType PoolingType => _poolingType;
    public uint BatchTokenCapacity => _batchTokenCapacity;
    public uint SeqMax => _seqMax;

    public LlamaEmbeddingAdapter(
        string modelPath,
        string modelName,
        LLamaPoolingType poolingType = LLamaPoolingType.Mean,
        int gpuLayers = 0,
        int contextSize = 8192)
    {
        _modelPath = modelPath;
        _modelName = modelName;
        _poolingType = poolingType;
        _gpuLayers = gpuLayers;
        _contextSize = contextSize > 0 ? contextSize : 8192;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_modelPath))
        {
            _logger.LogWarning("[LlamaEmbedding] GGUF 模型文件不存在: {Path}，启用模拟模式", _modelPath);
            Dimension = 384;
            IsSimulationMode = true;
            return;
        }

        try
        {
            if (_gpuLayers != 0)
            {
                ReferenceRAG.Core.Features.HardwareAcceleration.LlamaNativeInitializer.EnsureInitialized(preferCuda: true);
            }

            // 针对嵌入模型精准优化上下文与批处理大小，避免超大上下文导致显存逼近上限触发 WDDM 置换
            var effectiveCtx = (uint)Math.Clamp(_contextSize, 1024, 8192);
            _batchTokenCapacity = Math.Min(effectiveCtx, 512u);
            _seqMax = 16u;

            _logger.LogInformation(
                "[LlamaEmbedding] 正在加载 GGUF 权重: {Path} (GpuLayers={Layers}, ContextSize={Ctx}, BatchSize={Batch}, SeqMax={SeqMax})",
                _modelPath, _gpuLayers, effectiveCtx, _batchTokenCapacity, _seqMax);

            // 在后台任务中加载模型权重并构建常驻推理上下文，支持 GPU 失败平滑降级至 CPU
            try
            {
                var parameters = new ModelParams(_modelPath)
                {
                    Embeddings = true,
                    PoolingType = _poolingType,
                    GpuLayerCount = _gpuLayers,
                    ContextSize = effectiveCtx,
                    BatchSize = _batchTokenCapacity,
                    UBatchSize = _batchTokenCapacity,
                    SeqMax = _seqMax
                };

                await Task.Run(() =>
                {
                    _weights = LLamaWeights.LoadFromFile(parameters);
                    _context = _weights.CreateContext(parameters);
                }, cancellationToken);
            }
            catch (Exception ex) when (_gpuLayers != 0)
            {
                _logger.LogWarning(ex, "[LlamaEmbedding] GPU Offload (GpuLayers={Layers}) 加载异常，平滑降级至 CPU 重新加载", _gpuLayers);
                var cpuParameters = new ModelParams(_modelPath)
                {
                    Embeddings = true,
                    PoolingType = _poolingType,
                    GpuLayerCount = 0,
                    ContextSize = effectiveCtx,
                    BatchSize = _batchTokenCapacity,
                    UBatchSize = _batchTokenCapacity,
                    SeqMax = _seqMax
                };

                await Task.Run(() =>
                {
                    _weights = LLamaWeights.LoadFromFile(cpuParameters);
                    _context = _weights.CreateContext(cpuParameters);
                }, cancellationToken);
            }

            // 自省读取 GGUF 元数据中的嵌入维度（支持通用键名以及具体架构前缀如 qwen2.embedding_length）
            if (_weights!.Metadata.TryGetValue("embedding_length", out var dimStr) && int.TryParse(dimStr, out var dim))
            {
                Dimension = dim;
            }
            else
            {
                var archKey = _weights.Metadata.Keys.FirstOrDefault(k => k.EndsWith("embedding_length", StringComparison.OrdinalIgnoreCase));
                if (archKey != null && int.TryParse(_weights.Metadata[archKey], out var archDim))
                {
                    Dimension = archDim;
                }
                else
                {
                    // 若元数据键名有差异，通过单次微型原生批次探针确定准确向量长度
                    var probeBatch = new LLamaBatch();
                    var probeTokens = _context!.Tokenize("probe", true);
                    probeBatch.AddRange(probeTokens, 0, (LLamaSeqId)0, true);
                    _context.NativeHandle.Decode(probeBatch);
                    var probeVec = _context.NativeHandle.GetEmbeddingsSeq((LLamaSeqId)0);
                    Dimension = probeVec.Length;
                    _context.NativeHandle.MemoryClear(true);
                }
            }

            _logger.LogInformation("[LlamaEmbedding] 常驻上下文加载就绪: {Name}, 向量维度={Dimension}", _modelName, Dimension);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LlamaEmbedding] 加载 GGUF 模型失败: {Path}", _modelPath);
            Dispose();
            throw;
        }
    }

    public async Task<float[]> EncodeAsync(string text, string? prefix = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsSimulationMode)
        {
            return GenerateSimulatedVector(text);
        }

        var results = await EncodeBatchAsync(new[] { text }, prefix, cancellationToken);
        if (results == null || results.Length == 0)
        {
            throw new InvalidOperationException($"未能从输入提取到有效向量 (Model: {_modelName})");
        }

        return results[0];
    }

    public async Task<float[][]> EncodeBatchAsync(IReadOnlyList<string> texts, string? prefix = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (texts.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        if (IsSimulationMode)
        {
            var simOutput = new float[texts.Count][];
            for (int i = 0; i < texts.Count; i++)
            {
                simOutput[i] = GenerateSimulatedVector(texts[i]);
            }
            return simOutput;
        }

        if (_context == null)
        {
            throw new InvalidOperationException("LLamaContext 尚未初始化");
        }

        var output = new float[texts.Count][];
        await _inferenceSemaphore.WaitAsync(cancellationToken);
        try
        {
            var subBatch = new LLamaBatch();
            var currentSeqs = new List<(int TextIndex, int SeqId)>();

            for (int i = 0; i < texts.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var text = texts[i];
                var finalText = string.IsNullOrEmpty(prefix) ? text : $"{prefix}{text}";
                var tokens = _context.Tokenize(finalText, true);

                // 若单条文本超出批次最大 token 预算，进行安全截断
                if (tokens.Length > _batchTokenCapacity)
                {
                    tokens = tokens[..(int)_batchTokenCapacity];
                }

                var nextSeqId = currentSeqs.Count;

                // 若添加该序列会超出批次 token 上限或超出多序列最大槽位数 (SeqMax)，先提交解码当前子批次
                if (subBatch.TokenCount + tokens.Length > _batchTokenCapacity || nextSeqId >= _seqMax)
                {
                    FlushSubBatch(subBatch, currentSeqs, output);
                    subBatch.Clear();
                    currentSeqs.Clear();
                    nextSeqId = 0;
                }

                subBatch.AddRange(tokens, 0, (LLamaSeqId)nextSeqId, true);
                currentSeqs.Add((i, nextSeqId));
            }

            // 提交并解码剩余子批次
            if (currentSeqs.Count > 0)
            {
                FlushSubBatch(subBatch, currentSeqs, output);
                subBatch.Clear();
                currentSeqs.Clear();
            }
        }
        finally
        {
            _inferenceSemaphore.Release();
        }

        return output;
    }

    private void FlushSubBatch(LLamaBatch subBatch, List<(int TextIndex, int SeqId)> currentSeqs, float[][] output)
    {
        if (currentSeqs.Count == 0) return;

        // 原生批量前向传播，GPU Tensor Core 一次性并行执行多序列 GEMM 计算
        var decodeResult = _context!.NativeHandle.Decode(subBatch);
        if (decodeResult != DecodeResult.Ok)
        {
            _context.NativeHandle.MemoryClear(true);
            throw new InvalidOperationException($"LLama 解码批次失败: {decodeResult} (Model: {_modelName})");
        }

        // 提取每条序列对应的池化嵌入向量并进行单位归一化
        foreach (var (textIndex, seqId) in currentSeqs)
        {
            var raw = _context.NativeHandle.GetEmbeddingsSeq((LLamaSeqId)seqId);
            var vec = new float[raw.Length];
            var sumSquares = 0f;

            for (int j = 0; j < raw.Length; j++)
            {
                var val = raw[j];
                vec[j] = val;
                sumSquares += val * val;
            }

            // L2 归一化 (Unit Normalization)
            var norm = MathF.Sqrt(sumSquares);
            if (norm > 1e-10f)
            {
                var invNorm = 1.0f / norm;
                for (int j = 0; j < vec.Length; j++)
                {
                    vec[j] *= invNorm;
                }
            }

            output[textIndex] = vec;
        }

        // 清空 KV 缓存为下一批次腾出空间，上下文常驻在显存中零释放与零重新分配
        _context.NativeHandle.MemoryClear(true);
    }

    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (_context != null)
        {
            try
            {
                return _context.Tokenize(text, false).Length;
            }
            catch
            {
                // fallback
            }
        }
        return Math.Max(1, text.Length / 4);
    }

    private float[] GenerateSimulatedVector(string text)
    {
        var hash = (uint)text.GetHashCode();
        var rng = new Random((int)hash);
        var vec = new float[Dimension];
        var sumSquares = 0f;
        for (int i = 0; i < Dimension; i++)
        {
            vec[i] = (float)(rng.NextDouble() * 2 - 1);
            sumSquares += vec[i] * vec[i];
        }
        var norm = MathF.Sqrt(sumSquares);
        if (norm > 1e-10f)
        {
            for (int i = 0; i < Dimension; i++)
            {
                vec[i] /= norm;
            }
        }
        return vec;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _inferenceSemaphore.Wait(1000);
        try
        {
            _context?.Dispose();
            _context = null;
            _weights?.Dispose();
            _weights = null;
        }
        finally
        {
            _inferenceSemaphore.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
