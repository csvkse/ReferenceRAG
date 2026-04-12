using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using ObsidianRAG.Core.Services.Tokenizers;
using System.Diagnostics;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 向量编码服务 - ONNX + 归一化
/// 支持 ITextTokenizer 接口，可切换不同分词器实现
/// </summary>
public class EmbeddingService : IEmbeddingService, IDisposable
{
    private readonly EmbeddingOptions _options;
    private InferenceSession? _session;
    private ITextTokenizer _tokenizer;
    private bool _simulationMode;
    private bool _isEmbeddedFormat;
    private readonly object _lock = new();
    private bool _disposed;

    public string ModelName => _options.ModelName;
    public int Dimension { get; private set; }
    public bool IsSimulationMode => _simulationMode;
    public bool SupportsAsymmetricEncoding { get; private set; }

    public EmbeddingService(EmbeddingOptions options)
    {
        _options = options;
        _tokenizer = new FallbackTokenizer();

        LoadModel(options.ModelPath, options.ModelName);
    }

    /// <summary>
    /// 加载模型
    /// </summary>
    private void LoadModel(string modelPath, string modelName)
    {
        // 设置 CUDA/TensorRT 库路径（必须在加载 ONNX Runtime 之前）
        if (_options.UseCuda && !string.IsNullOrEmpty(_options.CudaLibraryPath))
        {
            var cudaPath = _options.CudaLibraryPath;
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath.Contains(cudaPath))
            {
                Environment.SetEnvironmentVariable("PATH", $"{cudaPath};{currentPath}");
                Console.WriteLine($"[EmbeddingService] 已添加库路径到 PATH: {cudaPath}");
            }
        }

        // 检查模型文件是否存在
        if (!File.Exists(modelPath))
        {
            Console.WriteLine($"[EmbeddingService] 模型文件不存在: {modelPath}");
            Console.WriteLine("[EmbeddingService] 使用模拟模式（返回随机向量，搜索结果将无意义）");
            Dimension = 384;
            _tokenizer = new FallbackTokenizer();
            _simulationMode = true;
            SupportsAsymmetricEncoding = _options.AsymmetricEncoding != null;
            return;
        }

        try
        {
            // 释放旧的 session
            var oldSession = _session;
            _session = null;
            oldSession?.Dispose();

            // 加载 ONNX 模型
            var sessionOptions = new SessionOptions();
            sessionOptions.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;

            if (_options.UseCuda)
            {
                try
                {
                    // CUDA 执行提供程序在动态 batch size 时有缓冲区重用问题
                    // 嵌入式格式模型关闭内存模式以支持动态 batch
                    var modelDir2 = Path.GetDirectoryName(modelPath);
                    var isEmbedded = modelDir2 != null && !File.Exists(Path.Combine(modelDir2, "model.onnx.data"));
                    if (isEmbedded)
                    {
                        sessionOptions.EnableMemoryPattern = false;
                    }
                    sessionOptions.AppendExecutionProvider_CUDA(_options.CudaDeviceId);
                    sessionOptions.AppendExecutionProvider_CPU();
                    Console.WriteLine($"[EmbeddingService] 使用 CUDA GPU: {_options.CudaDeviceId} (动态batch: {isEmbedded})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EmbeddingService] CUDA 不可用，回退到 CPU: {ex.Message}");
                    sessionOptions.AppendExecutionProvider_CPU();
                }
            }
            else
            {
                sessionOptions.AppendExecutionProvider_CPU();
            }

            _session = new InferenceSession(modelPath, sessionOptions);

            // 获取输出形状信息
            var outputMeta = _session.OutputMetadata;
            var outputInfo = outputMeta.First();
            var outputShape = outputInfo.Value.Dimensions;
            Dimension = outputShape[^1];
            var isPooled = outputShape.Length == 2; // 2D = 已 pooling, 3D = 需要 mean pooling

            _options.ModelPath = modelPath;
            _options.ModelName = modelName;

            Console.WriteLine($"[EmbeddingService] 模型加载成功: {modelName}");
            Console.WriteLine($"[EmbeddingService] 向量维度: {Dimension}, 输出形状: [{string.Join(", ", outputShape)}] ({(isPooled ? "已内置pooling" : "需要mean pooling")})");

            // 加载分词器（优先使用 Microsoft.ML.Tokenizers）
            _tokenizer = LoadTokenizer(modelPath);
            Console.WriteLine($"[EmbeddingService] 分词器: {_tokenizer.Name}, 词汇表大小: {_tokenizer.VocabSize}");

            // 从声明式配置检测非对称编码支持
            SupportsAsymmetricEncoding = _options.AsymmetricEncoding != null;
            if (SupportsAsymmetricEncoding)
            {
                Console.WriteLine($"[EmbeddingService] 启用非对称编码支持 (query: \"{_options.AsymmetricEncoding!.QueryPrefix}\" / passage: \"{_options.AsymmetricEncoding.DocumentPrefix}\")");
            }

            _simulationMode = false;

            // 检测 ONNX 格式：存在 .data 文件为外部格式，否则为嵌入式
            var modelDir = Path.GetDirectoryName(modelPath);
            _isEmbeddedFormat = modelDir != null && !File.Exists(Path.Combine(modelDir, "model.onnx.data"));
            var formatLabel = _isEmbeddedFormat ? "embedded" : "external";
            Console.WriteLine($"[EmbeddingService] ONNX 格式: {formatLabel}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmbeddingService] 模型加载失败: {ex.Message}");
            Dimension = 384;
            _tokenizer = new FallbackTokenizer();
            _simulationMode = true;
            SupportsAsymmetricEncoding = _options.AsymmetricEncoding != null;
        }
    }

    /// <summary>
    /// 重新加载模型
    /// </summary>
    public Task<bool> ReloadModelAsync(string modelPath, string modelName)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    Console.WriteLine($"[EmbeddingService] 正在切换模型: {modelName}");
                    LoadModel(modelPath, modelName);
                    Console.WriteLine($"[EmbeddingService] 模型切换完成: {modelName}, 维度: {Dimension}");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EmbeddingService] 模型切换失败: {ex.Message}");
                    return false;
                }
            }
        });
    }

    /// <summary>
    /// 卸载模型（释放 ONNX session）
    /// </summary>
    public void UnloadModel()
    {
        lock (_lock)
        {
            var oldSession = _session;
            _session = null;
            oldSession?.Dispose();
            Console.WriteLine("[EmbeddingService] 模型已卸载");
        }
    }

    /// <summary>
    /// 加载分词器（优先级：HuggingFace 完整管线 > 自定义 BertTokenizer > Microsoft.ML.Tokenizers > 回退分词器）
    /// </summary>
    private static ITextTokenizer LoadTokenizer(string modelPath)
    {
        var modelDir = Path.GetDirectoryName(modelPath) ?? "";
        var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");

        // 1. 优先使用 HuggingFace 完整分词器（tokenizers-rust 原生库，加载完整管线）
        if (File.Exists(tokenizerPath))
        {
            try
            {
                var hfTokenizer = new HuggingFaceTokenizer(tokenizerPath);
                Console.WriteLine("[EmbeddingService] 使用 HuggingFace 完整分词器（tokenizer.json 全管线）");
                return hfTokenizer;
            }
            catch (DllNotFoundException ex)
            {
                Console.WriteLine($"[EmbeddingService] HuggingFace 原生库加载失败，降级到自定义分词器: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmbeddingService] HuggingFace 分词器初始化失败，降级到自定义分词器: {ex.Message}");
            }

            // 2. HuggingFace 失败时，降级到自定义 BertTokenizer（仅读取 vocab）
            try
            {
                Console.WriteLine("[EmbeddingService] 降级使用自定义 BertTokenizer（tokenizer.json 仅 vocab）");
                return new BertTokenizer(tokenizerPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EmbeddingService] 自定义 BertTokenizer 初始化失败: {ex.Message}");
            }
        }

        // 3. 回退到 Microsoft.ML.Tokenizers（基于 vocab.txt）
        var mlTokenizer = MLBertTokenizer.CreateFromDirectory(modelDir);
        if (mlTokenizer != null)
        {
            Console.WriteLine("[EmbeddingService] 使用 Microsoft.ML.Tokenizers");
            return mlTokenizer;
        }

        // 4. 最后回退到简单分词器
        Console.WriteLine("[EmbeddingService] 使用简单分词器（精度较低）");
        return new FallbackTokenizer();
    }

    /// <summary>
    /// 编码单个文本
    /// </summary>
    public async Task<float[]> EncodeAsync(string text, CancellationToken cancellationToken = default)
    {
        var results = await EncodeBatchAsync(new[] { text }, cancellationToken);
        return results[0];
    }

    /// <summary>
    /// 按模式编码单个文本（支持非对称编码）
    /// </summary>
    public Task<float[]> EncodeAsync(string text, EmbeddingMode mode, CancellationToken cancellationToken = default)
    {
        var prefixedText = ApplyModePrefix(text, mode);
        return EncodeAsync(prefixedText, cancellationToken);
    }

    /// <summary>
    /// 批量编码
    /// </summary>
    public async Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0) return Array.Empty<float[]>();

        // 嵌入式格式模型使用动态 batch 推理（走下方通用路径）
        if (_options.UseCuda && !_isEmbeddedFormat)
        {
            return await Task.Run(() =>
            {
                // CUDA 路径同样需要 null guard
                if (_session == null)
                {
                    Console.WriteLine("[EmbeddingService] 警告：运行在模拟模式，返回随机向量。请检查模型文件路径是否正确。");
                    return textList.Select(_ => CreateRandomVector(Dimension)).ToArray();
                }

                return textList.Select(text => EncodeSingleInternal(text)).ToArray();
            }, cancellationToken);
        }

        // 非 CUDA 外部格式 路径：包在 Task.Run 中避免阻塞 async 调用方
        return await Task.Run(() =>
        {
            if (_session == null)
            {
                Console.WriteLine("[EmbeddingService] 警告：运行在模拟模式，返回随机向量。请检查模型文件路径是否正确。");
                return textList.Select(_ => CreateRandomVector(Dimension)).ToArray();
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Tokenize
            var tokens = _tokenizer.Tokenize(textList, _options.MaxSequenceLength);
            var tokenizeMs = sw.ElapsedMilliseconds;
            sw.Restart();

            // ONNX 推理
            var inputNames = _session.InputNames;
            var hasTokenTypeIds = inputNames.Contains("token_type_ids");

            NamedOnnxValue[] inputs = hasTokenTypeIds
                ? new NamedOnnxValue[3]
                : new NamedOnnxValue[2];

            inputs[0] = NamedOnnxValue.CreateFromTensor("input_ids", tokens.InputIds);
            inputs[1] = NamedOnnxValue.CreateFromTensor("attention_mask", tokens.AttentionMask);
            if (hasTokenTypeIds)
            {
                inputs[2] = NamedOnnxValue.CreateFromTensor("token_type_ids", tokens.TokenTypeIds);
            }

            using var results = _session.Run(inputs);
            var outputTensor = results.First().AsTensor<float>();
            var outputShape = outputTensor.Dimensions;

            var inferenceMs = sw.ElapsedMilliseconds;
            sw.Restart();

            var batchSize = textList.Count;
            var batchEmbeddings = new float[batchSize][];

            if (outputShape.Length == 2)
            {
                // 模型已内置 pooling，直接按 batch 维度切片
                Debug.Assert(outputShape[1] == Dimension,
                    $"输出维度不匹配：期望 {Dimension}，实际 {outputShape[1]}");

                for (int i = 0; i < batchSize; i++)
                {
                    batchEmbeddings[i] = new float[Dimension];
                    for (int j = 0; j < Dimension; j++)
                    {
                        batchEmbeddings[i][j] = outputTensor[i, j];
                    }
                    Normalize(batchEmbeddings[i]);
                }
            }
            else if (outputShape.Length == 3)
            {
                // 原始 last_hidden_state [batch, seq_len, dim] — 需要 mean pooling
                var seqLen = outputShape[1];
                Debug.Assert(outputShape[2] == Dimension,
                    $"隐藏层维度不匹配：期望 {Dimension}，实际 {outputShape[2]}");

                var attentionMask = tokens.AttentionMask;

                for (int i = 0; i < batchSize; i++)
                {
                    var embedding = new float[Dimension];
                    float maskSum = 0;

                    for (int s = 0; s < seqLen; s++)
                    {
                        // AttentionMask 通常是 DenseTensor<long>，显式转换为 float
                        var maskVal = (float)attentionMask[i, s];
                        if (maskVal == 0) continue;
                        maskSum += maskVal;

                        for (int d = 0; d < Dimension; d++)
                        {
                            embedding[d] += outputTensor[i, s, d] * maskVal;
                        }
                    }

                    if (maskSum > 0)
                    {
                        for (int d = 0; d < Dimension; d++)
                        {
                            embedding[d] /= maskSum;
                        }
                    }
                    Normalize(embedding);
                    batchEmbeddings[i] = embedding;
                }
            }
            else
            {
                throw new InvalidOperationException($"不支持的 ONNX 输出维度: {outputShape.Length}D");
            }

            var postProcessMs = sw.ElapsedMilliseconds;

#if DEBUG
            if (textList.Count >= 4)
            {
                Console.WriteLine($"[EmbeddingService] Batch {textList.Count}: Tokenize={tokenizeMs}ms, Inference={inferenceMs}ms, PostProcess={postProcessMs}ms");
            }
#endif

            return batchEmbeddings;

        }, cancellationToken);
    }

    /// <summary>
    /// 按模式批量编码（支持非对称编码）
    /// </summary>
    public Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, EmbeddingMode mode, CancellationToken cancellationToken = default)
    {
        var prefixedTexts = texts.Select(t => ApplyModePrefix(t, mode));
        return EncodeBatchAsync(prefixedTexts, cancellationToken);
    }

    /// <summary>
    /// 应用编码模式前缀（BGE 非对称编码）
    /// </summary>
    private string ApplyModePrefix(string text, EmbeddingMode mode)
    {
        if (mode == EmbeddingMode.Symmetric || _options.AsymmetricEncoding == null)
        {
            return text;
        }

        var config = _options.AsymmetricEncoding;
        return mode switch
        {
            EmbeddingMode.Query => $"{config.QueryPrefix}{text}",
            EmbeddingMode.Document => $"{config.DocumentPrefix}{text}",
            _ => text
        };
    }

    /// <summary>
    /// 单条文本推理（CUDA 模式使用，避免动态 batch size 问题）
    /// </summary>
    private float[] EncodeSingleInternal(string text)
    {
        if (_session == null || _simulationMode)
        {
            return CreateRandomVector(Dimension);
        }

        // Tokenize 单条文本
        var tokens = _tokenizer.Tokenize(new List<string> { text }, _options.MaxSequenceLength);

        // ONNX 推理
        var inputNames = _session.InputNames;
        var hasTokenTypeIds = inputNames.Contains("token_type_ids");

        NamedOnnxValue[] inputs = hasTokenTypeIds
            ? new NamedOnnxValue[3]
            : new NamedOnnxValue[2];

        inputs[0] = NamedOnnxValue.CreateFromTensor("input_ids", tokens.InputIds);
        inputs[1] = NamedOnnxValue.CreateFromTensor("attention_mask", tokens.AttentionMask);
        if (hasTokenTypeIds)
        {
            inputs[2] = NamedOnnxValue.CreateFromTensor("token_type_ids", tokens.TokenTypeIds);
        }

        using var results = _session.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();
        var outputShape = outputTensor.Dimensions;

        var embedding = new float[Dimension];

        if (outputShape.Length == 2)
        {
            // 2D [1, dim] - 已内置 pooling
            for (int j = 0; j < Dimension; j++)
            {
                embedding[j] = outputTensor[0, j];
            }
            Normalize(embedding);
        }
        else if (outputShape.Length == 3)
        {
            // 3D [1, seq_len, dim] - 需要 mean pooling
            var seqLen = outputShape[1];
            var attentionMask = tokens.AttentionMask;
            float maskSum = 0;

            for (int s = 0; s < seqLen; s++)
            {
                var maskVal = attentionMask[0, s];
                if (maskVal == 0) continue;
                maskSum += maskVal;

                for (int d = 0; d < Dimension; d++)
                {
                    embedding[d] += outputTensor[0, s, d] * maskVal;
                }
            }

            if (maskSum > 0)
            {
                for (int d = 0; d < Dimension; d++)
                {
                    embedding[d] /= maskSum;
                }
            }
            Normalize(embedding);
        }
        else
        {
            throw new InvalidOperationException($"不支持的 ONNX 输出维度: {outputShape.Length}D");
        }

        return embedding;
    }

    /// <summary>
    /// L2 归一化（原地操作）
    /// </summary>
    public float[] Normalize(float[] vector)
    {
        float sum = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }
        var norm = MathF.Sqrt(sum);
        if (norm < 1e-10f) return vector;

        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
        return vector;
    }

    /// <summary>
    /// 计算相似度（归一化后用内积）
    /// </summary>
    public float Similarity(float[] a, float[] b)
    {
        float dot = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
    }

    /// <summary>
    /// 创建随机向量（用于模拟）
    /// </summary>
    private float[] CreateRandomVector(int dimension)
    {
        var random = Random.Shared;
        var vector = new float[dimension];
        for (int i = 0; i < dimension; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        }
        Normalize(vector);
        return vector;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _session?.Dispose();
            if (_tokenizer is IDisposable disposable)
                disposable.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// 回退分词器（简单实现，仅用于测试或无词汇表时）
/// </summary>
internal class FallbackTokenizer : ITextTokenizer
{
    public string Name => "Fallback Tokenizer";
    public int VocabSize => 0;
    public int ClsTokenId => 101;
    public int SepTokenId => 102;
    public int PadTokenId => 0;
    public int UnkTokenId => 100;

    public (DenseTensor<long> InputIds, DenseTensor<long> AttentionMask, DenseTensor<long> TokenTypeIds)
        Tokenize(List<string> texts, int maxLength)
    {
        var batchSize = texts.Count;
        var inputIds = new DenseTensor<long>(new[] { batchSize, maxLength });
        var attentionMask = new DenseTensor<long>(new[] { batchSize, maxLength });
        var tokenTypeIds = new DenseTensor<long>(new[] { batchSize, maxLength });

        for (int i = 0; i < batchSize; i++)
        {
            var text = texts[i];
            var chars = text.Take(maxLength - 2).ToList();

            inputIds[i, 0] = ClsTokenId;
            attentionMask[i, 0] = 1;

            for (int j = 0; j < chars.Count; j++)
            {
                inputIds[i, j + 1] = chars[j];
                attentionMask[i, j + 1] = 1;
            }

            inputIds[i, chars.Count + 1] = SepTokenId;
            attentionMask[i, chars.Count + 1] = 1;
        }

        return (inputIds, attentionMask, tokenTypeIds);
    }

    public IReadOnlyList<int> Encode(string text, int maxLength = 512)
    {
        var result = new List<int> { ClsTokenId };
        foreach (var c in text.Take(maxLength - 2))
        {
            result.Add(c);
        }
        result.Add(SepTokenId);
        return result;
    }

    public int CountTokens(string text)
    {
        return string.IsNullOrEmpty(text) ? 0 : Math.Min(text.Length + 2, 512);
    }
}

/// <summary>
/// 向量编码配置
/// </summary>
public class EmbeddingOptions
{
    public string ModelPath { get; set; } = "";
    public string ModelName { get; set; } = "bge-small-zh-v1.5";
    public bool UseCuda { get; set; } = false;
    public int CudaDeviceId { get; set; } = 0;
    public string? CudaLibraryPath { get; set; }
    public int MaxSequenceLength { get; set; } = 512;
    public int BatchSize { get; set; } = 32;
    public AsymmetricEncodingConfig? AsymmetricEncoding { get; set; }
}
