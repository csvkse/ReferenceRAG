using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services.Tokenizers;
using System.Diagnostics;

namespace ReferenceRAG.Core.Services.Rerank;

/// <summary>
/// 重排服务 - 基于 ONNX Runtime 的 Cross-Encoder 实现
/// 用于对 Query-Document 对进行相关性评分
/// </summary>
public class OnnxRerankService : IRerankService, IDisposable
{
    private readonly RerankOptions _options;
    private InferenceSession? _session;
    private ITextTokenizer _tokenizer;
    private bool _simulationMode;
    private readonly object _lock = new();
    private bool _disposed;

    public string ModelName => _options.ModelName;
    public bool IsLoaded => _session != null && !_simulationMode;

    public OnnxRerankService(RerankOptions options)
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
        // 设置 CUDA 库路径（必须在加载 ONNX Runtime 之前）
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
                Console.WriteLine($"[OnnxRerankService] 已添加库路径到 PATH: {string.Join(", ", pathsToAdd)}");
            }
        }

        // 检查模型文件是否存在
        if (!File.Exists(modelPath))
        {
            Console.WriteLine($"[OnnxRerankService] 模型文件不存在: {modelPath}");
            Console.WriteLine("[OnnxRerankService] 使用模拟模式（返回随机分数）");
            _simulationMode = true;
            // 即使模型不存在，也更新配置中的模型名称
            _options.ModelPath = modelPath;
            _options.ModelName = modelName;
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
                    // 设置确定性推理模式，避免 CUDA 非确定性操作
                    sessionOptions.EnableMemoryPattern = false;

                    sessionOptions.AppendExecutionProvider_CUDA(_options.CudaDeviceId);
                    sessionOptions.AppendExecutionProvider_CPU();
                    Console.WriteLine($"[OnnxRerankService] 使用 CUDA GPU: {_options.CudaDeviceId} (确定性模式)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[OnnxRerankService] CUDA 不可用，回退到 CPU: {ex.Message}");
                    sessionOptions.AppendExecutionProvider_CPU();
                }
            }
            else
            {
                sessionOptions.AppendExecutionProvider_CPU();
            }

            _session = new InferenceSession(modelPath, sessionOptions);

            _options.ModelPath = modelPath;
            _options.ModelName = modelName;

            Console.WriteLine($"[OnnxRerankService] 模型加载成功: {modelName}");

            // 加载分词器
            _tokenizer = LoadTokenizer(modelPath);
            Console.WriteLine($"[OnnxRerankService] 分词器: {_tokenizer.Name}, 词汇表大小: {_tokenizer.VocabSize}");

            _simulationMode = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OnnxRerankService] 模型加载失败: {ex.Message}");
            _simulationMode = true;
            // 即使加载失败，也更新配置中的模型名称
            _options.ModelPath = modelPath;
            _options.ModelName = modelName;
        }
    }

    /// <summary>
    /// 加载分词器
    /// </summary>
    private static ITextTokenizer LoadTokenizer(string modelPath)
    {
        var modelDir = Path.GetDirectoryName(modelPath) ?? "";
        var tokenizerPath = Path.Combine(modelDir, "tokenizer.json");

        // 优先使用 HuggingFace 完整分词器
        if (File.Exists(tokenizerPath))
        {
            try
            {
                var hfTokenizer = new HuggingFaceTokenizer(tokenizerPath);
                Console.WriteLine("[OnnxRerankService] 使用 HuggingFace 完整分词器");
                return hfTokenizer;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OnnxRerankService] HuggingFace 分词器初始化失败: {ex.Message}");
            }
        }

        // 回退到 Microsoft.ML.Tokenizers
        var mlTokenizer = MLBertTokenizer.CreateFromDirectory(modelDir);
        if (mlTokenizer != null)
        {
            Console.WriteLine("[OnnxRerankService] 使用 Microsoft.ML.Tokenizers");
            return mlTokenizer;
        }

        // 最后回退到简单分词器
        Console.WriteLine("[OnnxRerankService] 使用简单分词器");
        return new FallbackTokenizer();
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
                    Console.WriteLine($"[OnnxRerankService] 正在切换模型: {modelName}");
                    LoadModel(modelPath, modelName);
                    Console.WriteLine($"[OnnxRerankService] 模型切换完成: {modelName}");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[OnnxRerankService] 模型切换失败: {ex.Message}");
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
            Console.WriteLine("[OnnxRerankService] 模型已卸载");
        }
    }

    /// <summary>
    /// 对单个查询-文档对进行重排评分
    /// </summary>
    public async Task<double> RerankAsync(string query, string document, CancellationToken cancellationToken = default)
    {
        var result = await RerankBatchAsync(query, new[] { document }, cancellationToken);
        return result.Documents.FirstOrDefault()?.RelevanceScore ?? 0.0;
    }

    /// <summary>
    /// 对多个文档进行批量重排评分
    /// </summary>
    public async Task<RerankResult> RerankBatchAsync(string query, IEnumerable<string> documents, CancellationToken cancellationToken = default)
    {
        var docList = documents.ToList();
        var sw = Stopwatch.StartNew();

        var result = new RerankResult
        {
            Query = query,
            Documents = new List<RerankDocument>()
        };

        if (docList.Count == 0)
        {
            result.DurationMs = sw.ElapsedMilliseconds;
            return result;
        }

        if (_simulationMode || _session == null)
        {
            // 模拟模式：返回随机分数
            var random = Random.Shared;
            for (int i = 0; i < docList.Count; i++)
            {
                result.Documents.Add(new RerankDocument
                {
                    Index = i,
                    Document = docList[i],
                    RelevanceScore = random.NextDouble()
                });
            }
            result.DurationMs = sw.ElapsedMilliseconds;
            return result;
        }

        // 批量推理优化：将多个 query-document 对一起输入模型
        var scores = await Task.Run(() =>
        {
            lock (_lock)
            {
                return ComputeRelevanceScoresBatch(query, docList);
            }
        }, cancellationToken);

        // 按分数降序排列
        result.Documents = scores.OrderByDescending(d => d.RelevanceScore).ToList();
        result.DurationMs = sw.ElapsedMilliseconds;

        return result;
    }

    /// <summary>
    /// 批量计算多个 Query-Document 对的相关性分数
    /// </summary>
    private List<RerankDocument> ComputeRelevanceScoresBatch(string query, List<string> documents)
    {
        if (_session == null || _simulationMode)
        {
            return documents.Select((doc, index) => new RerankDocument
            {
                Index = index,
                Document = doc,
                RelevanceScore = Random.Shared.NextDouble()
            }).ToList();
        }

        var maxLength = _options.MaxSequenceLength;
        var batchSize = documents.Count;

        // 批量分词
        var allInputIds = new long[batchSize, maxLength];
        var allAttentionMask = new long[batchSize, maxLength];
        var allTokenTypeIds = new long[batchSize, maxLength];

        for (int i = 0; i < batchSize; i++)
        {
            var tokens = TokenizeQueryDocument(query, documents[i]);
            for (int j = 0; j < maxLength; j++)
            {
                allInputIds[i, j] = tokens.InputIds[0, j];
                allAttentionMask[i, j] = tokens.AttentionMask[0, j];
                allTokenTypeIds[i, j] = tokens.TokenTypeIds[0, j];
            }
        }

        // 构建 ONNX 输入
        var inputNames = _session.InputNames;
        var hasTokenTypeIds = inputNames.Contains("token_type_ids");

        // 使用正确的方式创建 DenseTensor：先创建一维数组再指定维度
        var inputIdsFlat = new long[batchSize * maxLength];
        var attentionMaskFlat = new long[batchSize * maxLength];
        var tokenTypeIdsFlat = new long[batchSize * maxLength];

        for (int i = 0; i < batchSize; i++)
        {
            for (int j = 0; j < maxLength; j++)
            {
                inputIdsFlat[i * maxLength + j] = allInputIds[i, j];
                attentionMaskFlat[i * maxLength + j] = allAttentionMask[i, j];
                tokenTypeIdsFlat[i * maxLength + j] = allTokenTypeIds[i, j];
            }
        }

        var inputIdsTensor = new DenseTensor<long>(inputIdsFlat, new[] { batchSize, maxLength });
        var attentionMaskTensor = new DenseTensor<long>(attentionMaskFlat, new[] { batchSize, maxLength });

        NamedOnnxValue[] inputs = hasTokenTypeIds
            ? new NamedOnnxValue[3]
            : new NamedOnnxValue[2];

        inputs[0] = NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor);
        inputs[1] = NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor);
        if (hasTokenTypeIds)
        {
            var tokenTypeIdsTensor = new DenseTensor<long>(tokenTypeIdsFlat, new[] { batchSize, maxLength });
            inputs[2] = NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor);
        }

        // 执行批量推理
        using var results = _session.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();
        var outputShape = outputTensor.Dimensions;

        // 解析输出
        var scores = new List<RerankDocument>();
        for (int i = 0; i < batchSize; i++)
        {
            double score;
            if (outputShape.Length == 1 || (outputShape.Length == 2 && outputShape[1] == 1))
            {
                // 单输出：[batch] 或 [batch, 1]
                var rawScore = outputShape.Length == 1 ? outputTensor[i] : outputTensor[i, 0];
                score = Sigmoid(rawScore);
            }
            else if (outputShape.Length == 2 && outputShape[1] == 2)
            {
                // 二分类输出：[batch, 2]
                var negLogit = outputTensor[i, 0];
                var posLogit = outputTensor[i, 1];
                score = Softmax(posLogit, negLogit);
            }
            else
            {
                // 尝试取第一个值
                score = Sigmoid(outputTensor[i]);
            }

            scores.Add(new RerankDocument
            {
                Index = i,
                Document = documents[i],
                RelevanceScore = Math.Clamp(score, 0.0, 1.0)
            });
        }

        return scores;
    }

    /// <summary>
    /// 计算单个 Query-Document 对的相关性分数
    /// Cross-Encoder: 将 query 和 document 一起输入模型
    /// </summary>
    private double ComputeRelevanceScore(string query, string document)
    {
        if (_session == null || _simulationMode)
        {
            return Random.Shared.NextDouble();
        }

        // 对 query-document 对进行分词
        // Cross-Encoder 格式: [CLS] query [SEP] document [SEP]
        var tokens = TokenizeQueryDocument(query, document);

        // 构建 ONNX 输入
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

        // 执行推理
        using var results = _session.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();

        // 输出格式：[1] 或 [1, 1] - 单个分数
        // 或者 [1, 2] - 二分类 logits (negative, positive)
        var outputShape = outputTensor.Dimensions;

        double score;
        if (outputShape.Length == 1 || (outputShape.Length == 2 && outputShape[1] == 1))
        {
            // 单输出：直接使用 sigmoid 归一化到 [0, 1]
            var rawScore = outputShape.Length == 1 ? outputTensor[0] : outputTensor[0, 0];
            score = Sigmoid(rawScore);
        }
        else if (outputShape.Length == 2 && outputShape[1] == 2)
        {
            // 二分类输出：使用 softmax 获取正类概率
            var negLogit = outputTensor[0, 0];
            var posLogit = outputTensor[0, 1];
            score = Softmax(posLogit, negLogit);
        }
        else
        {
            // 尝试取第一个值
            score = Sigmoid(outputTensor[0]);
        }

        return Math.Clamp(score, 0.0, 1.0);
    }

    /// <summary>
    /// 对 Query-Document 对进行分词
    /// </summary>
    private (DenseTensor<long> InputIds, DenseTensor<long> AttentionMask, DenseTensor<long> TokenTypeIds)
    TokenizeQueryDocument(string query, string document)
    {
        var maxLength = _options.MaxSequenceLength;
        var docCharLimit = Math.Max(maxLength * 8, maxLength);
        if (document.Length > docCharLimit)
        {
            document = document[..docCharLimit];
        }

        // 构建输入文本: [CLS] query [SEP] document [SEP]
        // 使用分词器编码
        var queryTokens = _tokenizer.Encode(query, maxLength);
        var docTokens = _tokenizer.Encode(document, maxLength);

        // 合并 tokens，确保不超过最大长度
        // [CLS] + query_tokens + [SEP] + doc_tokens + [SEP]
        var tokens = new List<long> { _tokenizer.ClsTokenId };
        
        // 添加 query tokens（去掉首尾的 CLS 和 SEP）
        foreach (var t in queryTokens.Skip(1).TakeWhile((_, i) => i < queryTokens.Count - 1))
        {
            if (tokens.Count >= maxLength - 2) break;
            tokens.Add(t);
        }
        
        tokens.Add(_tokenizer.SepTokenId);
        
        // 添加 document tokens（去掉首尾的 CLS 和 SEP）
        foreach (var t in docTokens.Skip(1).TakeWhile((_, i) => i < docTokens.Count - 1))
        {
            if (tokens.Count >= maxLength - 1) break;
            tokens.Add(t);
        }

        tokens.Add(_tokenizer.SepTokenId);

        // 填充到 maxLength
        var attentionMask = new List<long>();
        var tokenTypeIds = new List<long>();
        var sepIndex = tokens.Count - 1;

        var actualLength = tokens.Count;
        for (int i = 0; i < actualLength; i++)
        {
            attentionMask.Add(1);
            // Query 部分 token_type_id = 0，Document 部分 = 1
            tokenTypeIds.Add(i <= sepIndex ? 0 : 1);
        }

        // Padding
        while (tokens.Count < maxLength)
        {
            tokens.Add(_tokenizer.PadTokenId);
            attentionMask.Add(0);
            tokenTypeIds.Add(0);
        }

        // 创建 tensors
        var inputIds = new DenseTensor<long>(new[] { 1, maxLength });
        var attentionMaskTensor = new DenseTensor<long>(new[] { 1, maxLength });
        var tokenTypeIdsTensor = new DenseTensor<long>(new[] { 1, maxLength });

        for (int i = 0; i < maxLength; i++)
        {
            inputIds[0, i] = tokens[i];
            attentionMaskTensor[0, i] = attentionMask[i];
            tokenTypeIdsTensor[0, i] = tokenTypeIds[i];
        }

        return (inputIds, attentionMaskTensor, tokenTypeIdsTensor);
    }

    /// <summary>
    /// Sigmoid 函数
    /// </summary>
    private static double Sigmoid(float x)
    {
        return 1.0 / (1.0 + Math.Exp(-x));
    }

    /// <summary>
    /// Softmax 计算正类概率
    /// </summary>
    private static double Softmax(float posLogit, float negLogit)
    {
        var maxLogit = Math.Max(posLogit, negLogit);
        var expPos = Math.Exp(posLogit - maxLogit);
        var expNeg = Math.Exp(negLogit - maxLogit);
        return expPos / (expPos + expNeg);
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
/// 重排服务配置
/// </summary>
public class RerankOptions
{
    public string ModelPath { get; set; } = "";
    public string ModelName { get; set; } = "bge-reranker-base";
    public bool UseCuda { get; set; } = false;
    public int CudaDeviceId { get; set; } = 0;
    public string? CudaLibraryPath { get; set; }
    public int MaxSequenceLength { get; set; } = 512;
}
