using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Services.Tokenizers;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 向量编码服务 - ONNX + 归一化
/// 支持 ITextTokenizer 接口，可切换不同分词器实现
/// </summary>
public class EmbeddingService : IEmbeddingService, IDisposable
{
    private readonly InferenceSession? _session;
    private readonly EmbeddingOptions _options;
    private readonly ITextTokenizer _tokenizer;
    private bool _disposed;

    public string ModelName => _options.ModelName;
    public int Dimension { get; }

    public EmbeddingService(EmbeddingOptions options)
    {
        _options = options;

        // 设置 CUDA/TensorRT 库路径（必须在加载 ONNX Runtime 之前）
        if (options.UseCuda && !string.IsNullOrEmpty(options.CudaLibraryPath))
        {
            var cudaPath = options.CudaLibraryPath;
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath.Contains(cudaPath))
            {
                Environment.SetEnvironmentVariable("PATH", $"{cudaPath};{currentPath}");
                Console.WriteLine($"[EmbeddingService] 已添加库路径到 PATH: {cudaPath}");
            }
        }

        // 检查模型文件是否存在
        if (!File.Exists(options.ModelPath))
        {
            Console.WriteLine($"[EmbeddingService] 模型文件不存在: {options.ModelPath}");
            Console.WriteLine("[EmbeddingService] 使用模拟模式（返回随机向量）");
            Dimension = 384;
            _tokenizer = new FallbackTokenizer();
            return;
        }

        try
        {
            // 加载 ONNX 模型
            var sessionOptions = new SessionOptions();
            sessionOptions.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;

            if (options.UseCuda)
            {
                try
                {
                    sessionOptions.AppendExecutionProvider_CUDA(options.CudaDeviceId);
                    Console.WriteLine($"[EmbeddingService] 使用 CUDA GPU: {options.CudaDeviceId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EmbeddingService] CUDA 不可用，回退到 CPU: {ex.Message}");
                }
            }
            sessionOptions.AppendExecutionProvider_CPU();

            _session = new InferenceSession(options.ModelPath, sessionOptions);

            // 获取维度
            var outputMeta = _session.OutputMetadata;
            Dimension = outputMeta.First().Value.Dimensions[^1];

            Console.WriteLine($"[EmbeddingService] 模型加载成功: {options.ModelName}");
            Console.WriteLine($"[EmbeddingService] 向量维度: {Dimension}");

            // 加载分词器（优先使用 Microsoft.ML.Tokenizers）
            _tokenizer = LoadTokenizer(options.ModelPath);
            Console.WriteLine($"[EmbeddingService] 分词器: {_tokenizer.Name}, 词汇表大小: {_tokenizer.VocabSize}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EmbeddingService] 模型加载失败: {ex.Message}");
            Dimension = 384;
            _tokenizer = new FallbackTokenizer();
        }
    }

    /// <summary>
    /// 加载分词器（优先级：Microsoft.ML.Tokenizers > BERTTokenizers > 自定义实现）
    /// </summary>
    private static ITextTokenizer LoadTokenizer(string modelPath)
    {
        var modelDir = Path.GetDirectoryName(modelPath) ?? "";

        // 1. 优先尝试 Microsoft.ML.Tokenizers（微软官方，最高性能）
        var mlTokenizer = MLBertTokenizer.CreateFromDirectory(modelDir);
        if (mlTokenizer != null)
        {
            Console.WriteLine("[EmbeddingService] 使用 Microsoft.ML.Tokenizers（官方高性能模式）");
            return mlTokenizer;
        }

        // 2. 尝试 BERTTokenizers（第三方高性能库）
        var bertTokenizer = BertTokenizerWrapper.CreateFromDirectory(modelDir);
        if (bertTokenizer != null)
        {
            Console.WriteLine("[EmbeddingService] 使用 BERTTokenizers（高性能模式）");
            return bertTokenizer;
        }

        // 3. 回退到自定义 BertTokenizer（基于 tokenizer.json）
        var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");
        if (File.Exists(tokenizerPath))
        {
            Console.WriteLine("[EmbeddingService] 使用自定义 BertTokenizer");
            return new BertTokenizer(tokenizerPath);
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
    /// 批量编码
    /// </summary>
    public async Task<float[][]> EncodeBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0) return Array.Empty<float[]>();

        // 如果模型未加载，返回模拟向量
        if (_session == null)
        {
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
        var embeddings = results.First().AsEnumerable<float>().ToArray();

        var inferenceMs = sw.ElapsedMilliseconds;
        sw.Restart();

        // 重塑为 [batch, dimension]
        var batchEmbeddings = new float[textList.Count][];
        for (int i = 0; i < textList.Count; i++)
        {
            batchEmbeddings[i] = new float[Dimension];
            Array.Copy(embeddings, i * Dimension, batchEmbeddings[i], 0, Dimension);
            Normalize(batchEmbeddings[i]);
        }

        var postProcessMs = sw.ElapsedMilliseconds;

#if DEBUG
        if (textList.Count >= 4)
        {
            Console.WriteLine($"[EmbeddingService] Batch {textList.Count}: Tokenize={tokenizeMs}ms, Inference={inferenceMs}ms, PostProcess={postProcessMs}ms");
        }
#endif

        return await Task.FromResult(batchEmbeddings);
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
}
