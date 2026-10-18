using System.Diagnostics;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;

namespace ReferenceRAG.Core.Features.ModelManagement.Adapters;

/// <summary>
/// 基于 LLamaSharp 的 GGUF 交叉编码重排模型适配器
/// </summary>
public sealed class LlamaRerankAdapter : IRerankBackendAdapter
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("LlamaRerankAdapter");

    private readonly string _modelPath;
    private readonly string _modelName;
    private readonly int _gpuLayers;
    private readonly int _contextSize;
    private readonly SemaphoreSlim _inferenceSemaphore = new(1, 1);

    private LLamaWeights? _weights;
    private LLamaEmbedder? _embedder;
    private bool _disposed;

    public string BackendType => "llama";
    public string ModelPath => _modelPath;
    public string ModelName => _modelName;
    public bool IsLoaded => _weights != null && _embedder != null;
    public int GpuLayers => _gpuLayers;

    public LlamaRerankAdapter(
        string modelPath,
        string modelName,
        int gpuLayers = 0,
        int contextSize = 4096)
    {
        _modelPath = modelPath;
        _modelName = modelName;
        _gpuLayers = gpuLayers;
        _contextSize = contextSize > 0 ? contextSize : 4096;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_modelPath))
        {
            throw new FileNotFoundException($"GGUF 重排模型文件不存在: {_modelPath}");
        }

        try
        {
            if (_gpuLayers != 0)
            {
                ReferenceRAG.Core.Features.HardwareAcceleration.LlamaNativeInitializer.EnsureInitialized(preferCuda: true);
            }

            _logger.LogInformation("[LlamaRerank] 正在加载 GGUF 重排模型: {Path} (GpuLayers={Layers})", _modelPath, _gpuLayers);

            try
            {
                var parameters = new ModelParams(_modelPath)
                {
                    Embeddings = true,
                    PoolingType = LLamaPoolingType.Rank,
                    GpuLayerCount = _gpuLayers,
                    ContextSize = (uint)_contextSize
                };

                await Task.Run(() =>
                {
                    _weights = LLamaWeights.LoadFromFile(parameters);
                    _embedder = new LLamaEmbedder(_weights, parameters);
                }, cancellationToken);
            }
            catch (Exception ex) when (_gpuLayers != 0)
            {
                _logger.LogWarning(ex, "[LlamaRerank] GPU Offload (GpuLayers={Layers}) 加载异常，平滑降级至 CPU 重新加载", _gpuLayers);
                var cpuParameters = new ModelParams(_modelPath)
                {
                    Embeddings = true,
                    PoolingType = LLamaPoolingType.Rank,
                    GpuLayerCount = 0,
                    ContextSize = (uint)_contextSize
                };

                await Task.Run(() =>
                {
                    _weights = LLamaWeights.LoadFromFile(cpuParameters);
                    _embedder = new LLamaEmbedder(_weights, cpuParameters);
                }, cancellationToken);
            }

            _logger.LogInformation("[LlamaRerank] GGUF 重排模型加载就绪: {Name}", _modelName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LlamaRerank] 加载 GGUF 重排模型失败: {Path}", _modelPath);
            Dispose();
            throw;
        }
    }

    public async Task<double> RerankAsync(string query, string document, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_embedder == null) throw new InvalidOperationException("重排适配器未就绪");

        // 拼接查询与文档为交叉编码格式
        var prompt = FormatCrossEncoderPrompt(query, document);

        IReadOnlyList<float[]> results;
        await _inferenceSemaphore.WaitAsync(cancellationToken);
        try
        {
            results = await _embedder.GetEmbeddings(prompt, cancellationToken);
        }
        finally
        {
            _inferenceSemaphore.Release();
        }

        if (results == null || results.Count == 0 || results[0].Length == 0)
        {
            return 0.5;
        }

        var rawScore = results[0][0];
        var isPseudoZeroVector = results[0].Length > 1 && results[0][0] == 0f && results[0][1] == 0f && results[0][^1] == 0f;
        if (float.IsNaN(rawScore) || float.IsInfinity(rawScore) || isPseudoZeroVector)
        {
            rawScore = float.NaN;
        }
        return NormalizeScore(rawScore, 0, 1);
    }

    public async Task<RerankResult> RerankBatchAsync(string query, IEnumerable<string> documents, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var docList = documents.ToList();
        var result = new RerankResult
        {
            Query = query,
            Documents = new List<RerankDocument>(docList.Count)
        };

        if (docList.Count == 0)
        {
            result.DurationMs = sw.ElapsedMilliseconds;
            return result;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_embedder == null) throw new InvalidOperationException("重排适配器未就绪");

        await _inferenceSemaphore.WaitAsync(cancellationToken);
        try
        {
            var nanCount = 0;
            var nanDetected = false;
            for (int i = 0; i < docList.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 若连续 2 条输出 NaN，判定当前 GGUF 模型缺乏 rank 分类头，快速早停止损，避免无谓消耗数秒算力
                if (nanCount >= 2)
                {
                    nanDetected = true;
                    for (int rem = i; rem < docList.Count; rem++)
                    {
                        result.Documents.Add(new RerankDocument
                        {
                            Index = rem,
                            RelevanceScore = NormalizeScore(float.NaN, rem, docList.Count),
                            Document = docList[rem]
                        });
                    }
                    break;
                }

                var prompt = FormatCrossEncoderPrompt(query, docList[i]);
                var results = await _embedder.GetEmbeddings(prompt, cancellationToken);
                var rawScore = (results != null && results.Count > 0 && results[0] != null && results[0].Length > 0) ? results[0][0] : 0f;

                var isPseudoZeroVector = results != null && results.Count > 0 && results[0] != null && results[0].Length > 1 && results[0][0] == 0f && results[0][1] == 0f && results[0][^1] == 0f;
                if (float.IsNaN(rawScore) || float.IsInfinity(rawScore) || isPseudoZeroVector)
                {
                    nanCount++;
                    nanDetected = true;
                    rawScore = float.NaN;
                }
                else
                {
                    nanCount = 0;
                }

                var normalizedScore = NormalizeScore(rawScore, i, docList.Count);

                result.Documents.Add(new RerankDocument
                {
                    Index = i,
                    RelevanceScore = normalizedScore,
                    Document = docList[i]
                });
            }

            if (nanDetected)
            {
                _logger.LogWarning("[LlamaRerank] 检测到 GGUF 重排模型输出 NaN，已平滑降级为保底加权分并早停。强烈建议在模型管理中选用 ONNX 格式标准 Cross-Encoder 模型（如 bge-reranker-base）以获得真正的毫秒级批处理与排序精度");
            }
        }
        finally
        {
            _inferenceSemaphore.Release();
        }

        result.Documents = result.Documents
            .OrderByDescending(d => d.RelevanceScore)
            .ToList();

        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;
        return result;
    }

    private static double NormalizeScore(float rawScore, int index, int totalCount)
    {
        if (float.IsNaN(rawScore) || float.IsInfinity(rawScore))
        {
            // 模型无 Rank 分类头或全 0 模长导致 0/0 NaN 时，采用平滑降级保底（按召回先后保留相对序）
            var step = totalCount > 1 ? 0.35 / totalCount : 0.0;
            return Math.Clamp(0.85 - (index * step), 0.10, 0.99);
        }

        // Rank 模式通常输出标量相关度得分（原始 Logit），通过 Sigmoid 映射到 [0, 1]
        var normalizedScore = 1.0 / (1.0 + Math.Exp(-rawScore));
        if (double.IsNaN(normalizedScore) || double.IsInfinity(normalizedScore))
        {
            var step = totalCount > 1 ? 0.35 / totalCount : 0.0;
            return Math.Clamp(0.85 - (index * step), 0.10, 0.99);
        }

        return Math.Clamp(normalizedScore, 0.0, 1.0);
    }

    private static string FormatCrossEncoderPrompt(string query, string document)
    {
        // 限制文档参与重排的长度，避免超长文档引发注意力机制 O(L^2) 二次计算灾难
        const int maxDocChars = 512;
        var truncatedDoc = document.Length > maxDocChars ? document[..maxDocChars] : document;
        return $"[CLS] {query} [SEP] {truncatedDoc} [SEP]";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // 最多等待 15 秒，确保正在执行的 native 推理安全退出，杜绝野指针访问违规 (0xC0000005)
            _inferenceSemaphore.Wait(TimeSpan.FromSeconds(15));
        }
        catch { }

        try
        {
            _embedder?.Dispose();
            _embedder = null;
            _weights?.Dispose();
            _weights = null;
        }
        finally
        {
            try { _inferenceSemaphore.Release(); } catch { }
            _inferenceSemaphore.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
