using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services.Tokenizers;
using System.Diagnostics;
using System.Numerics;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 向量编码服务 - ONNX + 归一化
/// 支持 ITextTokenizer 接口，可切换不同分词器实现
/// </summary>
public class EmbeddingService : IEmbeddingService, IDisposable
{
    private enum PoolingMode { Mean, Cls }

    private readonly EmbeddingOptions _options;
    private InferenceSession? _session;
    private ITextTokenizer _tokenizer;
    private bool _simulationMode;
    private bool _isEmbeddedFormat;
    private PoolingMode _poolingMode = PoolingMode.Mean;
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
        // 只要有配置就设置 PATH，不管当前是否启用 CUDA（为热切换做准备）
        if (!string.IsNullOrEmpty(_options.CudaLibraryPath))
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var existingPaths = new HashSet<string>(
                currentPath.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase
            );

            var pathsToAdd = _options.CudaLibraryPath
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !existingPaths.Contains(p.Trim()))
                .ToList();

            if (pathsToAdd.Count > 0)
            {
                var newPath = string.Join(";", pathsToAdd) + ";" + currentPath;
                Environment.SetEnvironmentVariable("PATH", newPath);
                Console.WriteLine($"[EmbeddingService] 已添加 CUDA 库路径到 PATH: {string.Join(", ", pathsToAdd)}");
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

            // 模型目录路径（用于读取配置文件）
            var modelDir = Path.GetDirectoryName(modelPath) ?? "";

            // 加载 ONNX 模型
            var sessionOptions = new SessionOptions();
            sessionOptions.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;

            if (_options.UseCuda)
            {
                try
                {
                    // 始终关闭内存模式以支持动态 batch size
                    // 对 embedded 和 external 两种格式均有效，消除逐条推理退化
                    sessionOptions.EnableMemoryPattern = false;
                    sessionOptions.AppendExecutionProvider_CUDA(_options.CudaDeviceId);
                    sessionOptions.AppendExecutionProvider_CPU();
                    Console.WriteLine($"[EmbeddingService] 使用 CUDA GPU: {_options.CudaDeviceId} (动态batch已启用)");
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
            // 优先使用 sentence_embedding 输出（BGE M3 等内置 CLS pooling 的模型）
            var outputMeta = _session.OutputMetadata;
            var hasSentenceEmbedding = outputMeta.ContainsKey("sentence_embedding");
            var outputInfo = hasSentenceEmbedding
                ? outputMeta["sentence_embedding"]
                : outputMeta.First().Value;
            var outputShape = outputInfo.Dimensions;
            Dimension = outputShape[^1];
            var isPooled = outputShape.Length == 2; // 2D = 已 pooling, 3D = 需要 mean pooling

            // ONNX 元数据中的符号维度会返回 0（如 ?(Divsentence_embedding_dim_1)）
            // 按优先级依次读取：1_Pooling/config.json → config.json hidden_size
            if (Dimension == 0)
            {
                var poolingConfig = Path.Combine(modelDir, "1_Pooling", "config.json");
                if (File.Exists(poolingConfig))
                {
                    try
                    {
                        var json = File.ReadAllText(poolingConfig);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("word_embedding_dimension", out var dimProp))
                        {
                            Dimension = dimProp.GetInt32();
                            Console.WriteLine($"[EmbeddingService] 从 1_Pooling/config.json 读取维度: {Dimension}");
                        }
                    }
                    catch
                    {
                        Console.WriteLine("[EmbeddingService] 无法从 1_Pooling/config.json 读取维度，尝试 config.json");
                    }
                }
            }

            // 终极 fallback：从 config.json 读取 hidden_size（多数 HuggingFace 模型均包含此文件）
            if (Dimension == 0)
            {
                var modelConfig = Path.Combine(modelDir, "config.json");
                if (File.Exists(modelConfig))
                {
                    try
                    {
                        var json = File.ReadAllText(modelConfig);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("hidden_size", out var hiddenProp))
                        {
                            Dimension = hiddenProp.GetInt32();
                            Console.WriteLine($"[EmbeddingService] 从 config.json hidden_size 读取维度: {Dimension}");
                        }
                    }
                    catch { }
                }
            }

            _options.ModelPath = modelPath;
            _options.ModelName = modelName;
            if (hasSentenceEmbedding)
                Console.WriteLine("[EmbeddingService] 检测到 sentence_embedding 输出，使用内置 CLS pooling");

            // 读取 1_Pooling/config.json 确定 pooling 策略（Sentence Transformers 标准约定）
            // sentence_embedding 输出已是 2D，无需再走 pooling 分支，此处仅针对 3D 输出
            _poolingMode = PoolingMode.Mean; // 默认 mean pooling
            if (!hasSentenceEmbedding)
            {
                var poolingConfig = Path.Combine(modelDir, "1_Pooling", "config.json");
                if (File.Exists(poolingConfig))
                {
                    try
                    {
                        var json = File.ReadAllText(poolingConfig);
                        using var doc = System.Text.Json.JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("pooling_mode_cls_token", out var clsProp) && clsProp.GetBoolean())
                        {
                            _poolingMode = PoolingMode.Cls;
                            Console.WriteLine("[EmbeddingService] 1_Pooling/config.json: 使用 CLS token pooling");
                        }
                        else
                        {
                            Console.WriteLine("[EmbeddingService] 1_Pooling/config.json: 使用 mean pooling");
                        }
                    }
                    catch
                    {
                        Console.WriteLine("[EmbeddingService] 1_Pooling/config.json 解析失败，回退到 mean pooling");
                    }
                }
            }

            // 从 ONNX 输入元数据自动检测 MaxSequenceLength
            // 如果模型有固定输入形状（dim > 0），优先使用模型的真实要求
            var inputMeta = _session.InputMetadata;
            if (inputMeta.TryGetValue("input_ids", out var inputIdsMeta))
            {
                var inputDims = inputIdsMeta.Dimensions;
                if (inputDims.Length >= 2 && inputDims[1] > 0)
                {
                    var modelMaxSeq = (int)inputDims[1];
                    if (modelMaxSeq != _options.MaxSequenceLength)
                    {
                        Console.WriteLine($"[EmbeddingService] ONNX 固定输入形状检测到 MaxSeqLen={modelMaxSeq}，当前配置为 {_options.MaxSequenceLength}，自动对齐");
                        _options.MaxSequenceLength = modelMaxSeq;
                    }
                }
            }

            Console.WriteLine($"[EmbeddingService] 模型加载成功: {modelName}");
            Console.WriteLine($"[EmbeddingService] 向量维度: {Dimension}, 输出形状: [{string.Join(", ", outputShape)}] ({(isPooled ? "已内置pooling" : "需要mean pooling")}), MaxSeqLen: {_options.MaxSequenceLength}");

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
            _isEmbeddedFormat = !File.Exists(Path.Combine(modelDir, "model.onnx.data"));
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
    public Task<bool> ReloadModelAsync(string modelPath, string modelName, int? maxSequenceLength = null)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                try
                {
                    Console.WriteLine($"[EmbeddingService] 正在切换模型: {modelName}");
                    if (maxSequenceLength.HasValue)
                    {
                        _options.MaxSequenceLength = maxSequenceLength.Value;
                        Console.WriteLine($"[EmbeddingService] MaxSequenceLength 更新为: {maxSequenceLength.Value}");
                    }
                    LoadModel(modelPath, modelName);
                    Console.WriteLine($"[EmbeddingService] 模型切换完成: {modelName}, 维度: {Dimension}, MaxSeqLen: {_options.MaxSequenceLength}");
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

        // 统一走批量推理路径（CUDA 已设置 EnableMemoryPattern=false，支持动态 batch）
        return await Task.Run(() =>
        {
            // 在 lock 内检查 _session 状态，避免竞态条件
            InferenceSession? session;
            lock (_lock)
            {
                if (_session == null || _simulationMode)
                {
                    Console.WriteLine("[EmbeddingService] 警告：运行在模拟模式，返回随机向量。请检查模型文件路径是否正确。");
                    return textList.Select(_ => CreateRandomVector(Dimension)).ToArray();
                }
                session = _session;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Tokenize
            var tokens = _tokenizer.Tokenize(textList, _options.MaxSequenceLength);
            var tokenizeMs = sw.ElapsedMilliseconds;
            sw.Restart();

            // ONNX 推理
            var inputNames = session.InputNames;
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

            using var results = session.Run(inputs);
            // 优先使用 sentence_embedding（内置 CLS pooling），回退到第一个输出
            var hasSentEmbedding = session.OutputMetadata.ContainsKey("sentence_embedding");
            var outputTensor = hasSentEmbedding
                ? results.First(r => r.Name == "sentence_embedding").AsTensor<float>()
                : results.First().AsTensor<float>();
            var outputShape = outputTensor.Dimensions;

            var inferenceMs = sw.ElapsedMilliseconds;
            sw.Restart();

            var batchSize = textList.Count;
            var batchEmbeddings = new float[batchSize][];

            if (outputShape.Length == 2)
            {
                // 模型已内置 pooling，直接按 batch 维度切片
                // Dimension 为 0 时（符号维度未能在 LoadModel 阶段静态解析），从实际输出后验更新
                if (Dimension == 0)
                    Dimension = outputShape[1];
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
                // last_hidden_state [batch, seq_len, dim]
                var seqLen = outputShape[1];
                if (Dimension == 0)
                    Dimension = outputShape[2];
                Debug.Assert(outputShape[2] == Dimension,
                    $"隐藏层维度不匹配：期望 {Dimension}，实际 {outputShape[2]}");

                if (_poolingMode == PoolingMode.Cls)
                {
                    // CLS token pooling: 取 seq 位置 0
                    for (int i = 0; i < batchSize; i++)
                    {
                        var embedding = new float[Dimension];
                        for (int d = 0; d < Dimension; d++)
                            embedding[d] = outputTensor[i, 0, d];
                        Normalize(embedding);
                        batchEmbeddings[i] = embedding;
                    }
                }
                else
                {
                    // Mean pooling（masked average）
                    var attentionMask = tokens.AttentionMask;
                    for (int i = 0; i < batchSize; i++)
                    {
                        var embedding = new float[Dimension];
                        float maskSum = 0;
                        for (int s = 0; s < seqLen; s++)
                        {
                            var maskVal = (float)attentionMask[i, s];
                            if (maskVal == 0) continue;
                            maskSum += maskVal;
                            for (int d = 0; d < Dimension; d++)
                                embedding[d] += outputTensor[i, s, d] * maskVal;
                        }
                        if (maskSum > 0)
                            for (int d = 0; d < Dimension; d++)
                                embedding[d] /= maskSum;
                        Normalize(embedding);
                        batchEmbeddings[i] = embedding;
                    }
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
        var hasSentEmb = _session.OutputMetadata.ContainsKey("sentence_embedding");
        var outputTensor = hasSentEmb
            ? results.First(r => r.Name == "sentence_embedding").AsTensor<float>()
            : results.First().AsTensor<float>();
        var outputShape = outputTensor.Dimensions;

        // 后验更新 Dimension（应对 LoadModel 阶段符号维度为 0 的情况）
        if (Dimension == 0)
            Dimension = outputShape.Length == 2 ? outputShape[1] : outputShape[2];

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
            // 3D [1, seq_len, dim]
            var seqLen = outputShape[1];
            if (_poolingMode == PoolingMode.Cls)
            {
                // CLS token pooling
                for (int d = 0; d < Dimension; d++)
                    embedding[d] = outputTensor[0, 0, d];
                Normalize(embedding);
            }
            else
            {
                // Mean pooling
                var attentionMask = tokens.AttentionMask;
                float maskSum = 0;
                for (int s = 0; s < seqLen; s++)
                {
                    var maskVal = attentionMask[0, s];
                    if (maskVal == 0) continue;
                    maskSum += maskVal;
                    for (int d = 0; d < Dimension; d++)
                        embedding[d] += outputTensor[0, s, d] * maskVal;
                }
                if (maskSum > 0)
                    for (int d = 0; d < Dimension; d++)
                        embedding[d] /= maskSum;
                Normalize(embedding);
            }
        }
        else
        {
            throw new InvalidOperationException($"不支持的 ONNX 输出维度: {outputShape.Length}D");
        }

        return embedding;
    }

    /// <summary>
    /// L2 归一化（原地操作，使用 SIMD 加速）
    /// </summary>
    public float[] Normalize(float[] vector)
    {
        var span = vector.AsSpan();
        float sum = 0;

        // 使用 SIMD 向量化计算平方和
        if (Vector.IsHardwareAccelerated && span.Length >= Vector<float>.Count)
        {
            var sumVec = Vector<float>.Zero;
            var i = 0;

            // 每次处理一个向量宽度
            for (; i <= span.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var v = new Vector<float>(span.Slice(i, Vector<float>.Count));
                sumVec += v * v;
            }

            // 水平求和
            sum = Vector.Dot(sumVec, Vector<float>.One);

            // 处理剩余元素
            for (; i < span.Length; i++)
            {
                sum += span[i] * span[i];
            }
        }
        else
        {
            // 回退到标量计算
            for (int i = 0; i < span.Length; i++)
            {
                sum += span[i] * span[i];
            }
        }

        var norm = MathF.Sqrt(sum);
        if (norm < 1e-10f) return vector;

        // 使用 SIMD 向量化除法
        if (Vector.IsHardwareAccelerated && span.Length >= Vector<float>.Count)
        {
            var normVec = new Vector<float>(norm);
            var j = 0;

            for (; j <= span.Length - Vector<float>.Count; j += Vector<float>.Count)
            {
                var v = new Vector<float>(span.Slice(j, Vector<float>.Count));
                (v / normVec).CopyTo(span.Slice(j));
            }

            // 处理剩余元素
            for (; j < span.Length; j++)
            {
                span[j] /= norm;
            }
        }
        else
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] /= norm;
            }
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
