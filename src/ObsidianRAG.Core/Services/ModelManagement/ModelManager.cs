using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 模型信息
/// </summary>
public class ModelInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Dimension { get; set; }
    public int MaxSequenceLength { get; set; } = 512;
    public string ModelType { get; set; } = "embedding";
    public bool IsQuantized { get; set; }
    public string? QuantizationType { get; set; }
    public long ModelSizeBytes { get; set; }
    public string? DownloadUrl { get; set; }
    public bool IsDownloaded { get; set; }
    public string? LocalPath { get; set; }
    public bool IsGpuSupported { get; set; } = true;
    public string[]? Languages { get; set; }
    public float? BenchmarkScore { get; set; }
    public bool HasOnnx { get; set; } = true; // 是否在 HuggingFace 上有 ONNX 版本

    /// <summary>
    /// 非对称编码配置（null 表示不支持非对称编码）
    /// </summary>
    public AsymmetricEncodingConfig? AsymmetricEncoding { get; set; }

    /// <summary>
    /// 当前 ONNX 格式: "embedded" | "external" | "unknown"
    /// </summary>
    public string OnnxFormat { get; set; } = "unknown";

    /// <summary>
    /// 是否支持格式转换（模型大小小于 2GB 时为 true）
    /// </summary>
    public bool CanConvertFormat { get; set; }
}

/// <summary>
/// 模型管理器接口
/// </summary>
public interface IModelManager
{
    Task<List<ModelInfo>> GetAvailableModelsAsync();
    List<ModelInfo> GetDownloadedModels();
    ModelInfo? GetCurrentModel();
    Task<bool> SwitchModelAsync(string modelName);
    Task<(bool Success, string? Error)> DownloadModelAsync(string modelName, IProgress<float>? progress = null, string? targetFormat = null, string? onnxVariantPath = null);
    Task<bool> DeleteModelAsync(string modelName);
    Task<List<ModelInfo>> GetRecommendedModelsAsync(string? language = null, bool preferGpu = true);
    void RefreshLocalModels();

    /// <summary>
    /// 检测模型 ONNX 格式
    /// </summary>
    string DetectOnnxFormat(string modelDir);

    /// <summary>
    /// 转换模型格式
    /// </summary>
    Task<(bool Success, string? Error)> ConvertFormatAsync(
        string modelName,
        string targetFormat,
        IProgress<float>? progress = null);

    /// <summary>
    /// 添加自定义 HuggingFace 模型
    /// </summary>
    Task<(bool Success, string? Error, ModelInfo? Model)> AddCustomModelAsync(
        string huggingFaceId,
        string? displayName = null);

    /// <summary>
    /// 设置模型保存路径（可选迁移已有模型）
    /// </summary>
    Task<(bool Success, string? Error, List<string> Migrated)> SetModelsPathAsync(
        string newPath, bool migrateExisting = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取模型可用的 ONNX 变体列表
    /// </summary>
    [Obsolete("Use GetDownloadOptionsAsync instead")]
    Task<List<OnnxVariant>> GetOnnxVariantsAsync(string modelName);

    /// <summary>
    /// 获取模型的下载选项
    /// </summary>
    Task<ModelDownloadOptions> GetDownloadOptionsAsync(string modelName);
}

/// <summary>
/// 模型管理器实现
/// </summary>
public class ModelManager : IModelManager, IDisposable
{
    private string _modelsPath;
    private readonly ConfigManager _configManager;
    private readonly Dictionary<string, ModelInfo> _modelRegistry;
    private ModelInfo? _currentModel;
    private bool _disposed;

    // 预定义模型库 - 包含 ONNX 和 PyTorch 版本信息
    private static readonly List<ModelInfo> PredefinedModels = new()
    {
        // 中文模型
        new ModelInfo
        {
            Name = "bge-small-zh-v1.5",
            DisplayName = "BGE Small Chinese v1.5",
            Description = "BAAI通用中文向量模型，适合中文语义搜索，体积小速度快",
            Dimension = 512,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh", "en" },
            BenchmarkScore = 0.85f,
            DownloadUrl = "BAAI/bge-small-zh-v1.5",
            ModelSizeBytes = 102 * 1024 * 1024,
            HasOnnx = false, // PyTorch only，需要转换
            AsymmetricEncoding = new AsymmetricEncodingConfig
            {
                QueryPrefix = "query: ",
                DocumentPrefix = "passage: "
            }
        },
        new ModelInfo
        {
            Name = "bge-base-zh-v1.5",
            DisplayName = "BGE Base Chinese v1.5",
            Description = "BAAI通用中文向量模型 Base 版本，更高精度",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh", "en" },
            BenchmarkScore = 0.92f,
            DownloadUrl = "BAAI/bge-base-zh-v1.5",
            ModelSizeBytes = 406 * 1024 * 1024,
            HasOnnx = false,
            AsymmetricEncoding = new AsymmetricEncodingConfig
            {
                QueryPrefix = "query: ",
                DocumentPrefix = "passage: "
            }
        },
        new ModelInfo
        {
            Name = "bge-large-zh-v1.5",
            DisplayName = "BGE Large Chinese v1.5",
            Description = "BAAI通用中文向量模型 Large 版本，最高精度",
            Dimension = 1024,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh", "en" },
            BenchmarkScore = 0.95f,
            DownloadUrl = "BAAI/bge-large-zh-v1.5",
            ModelSizeBytes = (long)(1.3 * 1024 * 1024 * 1024),
            HasOnnx = false,
            AsymmetricEncoding = new AsymmetricEncodingConfig
            {
                QueryPrefix = "query: ",
                DocumentPrefix = "passage: "
            }
        },
        // 英文模型
        new ModelInfo
        {
            Name = "bge-small-en-v1.5",
            DisplayName = "BGE Small English v1.5",
            Description = "BAAI通用英文向量模型，适合英文语义搜索",
            Dimension = 384,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "en" },
            BenchmarkScore = 0.83f,
            DownloadUrl = "BAAI/bge-small-en-v1.5",
            ModelSizeBytes = 133 * 1024 * 1024,
            HasOnnx = false,
            AsymmetricEncoding = new AsymmetricEncodingConfig
            {
                QueryPrefix = "query: ",
                DocumentPrefix = "passage: "
            }
        },
        new ModelInfo
        {
            Name = "bge-base-en-v1.5",
            DisplayName = "BGE Base English v1.5",
            Description = "BAAI通用英文向量模型 Base 版本，更高精度",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "en" },
            BenchmarkScore = 0.89f,
            DownloadUrl = "BAAI/bge-base-en-v1.5",
            ModelSizeBytes = 438 * 1024 * 1024,
            HasOnnx = false,
            AsymmetricEncoding = new AsymmetricEncodingConfig
            {
                QueryPrefix = "query: ",
                DocumentPrefix = "passage: "
            }
        },
        // 多语言模型
        new ModelInfo
        {
            Name = "bge-m3",
            DisplayName = "BGE M3",
            Description = "BAAI多语言向量模型，支持100+语言，功能最全",
            Dimension = 1024,
            MaxSequenceLength = 8192,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "ja", "ko", "fr", "de", "es" },
            BenchmarkScore = 0.91f,
            DownloadUrl = "BAAI/bge-m3",
            ModelSizeBytes = (long)(2.2 * 1024 * 1024 * 1024),
            HasOnnx = false,
            AsymmetricEncoding = new AsymmetricEncodingConfig
            {
                QueryPrefix = "query: ",
                DocumentPrefix = "passage: "
            }
        },
        // 已有 ONNX 版本的模型 (通过 optimum 导出)
        new ModelInfo
        {
            Name = "all-MiniLM-L6-v2",
            DisplayName = "MiniLM L6 v2 (ONNX)",
            Description = "轻量级英文向量模型，已有 ONNX 版本",
            Dimension = 384,
            MaxSequenceLength = 256,
            ModelType = "embedding",
            Languages = new[] { "en" },
            BenchmarkScore = 0.82f,
            DownloadUrl = "sentence-transformers/all-MiniLM-L6-v2",
            ModelSizeBytes = 90 * 1024 * 1024,
            HasOnnx = false
        },
        // ========== GTE 系列（阿里达摩院）==========
        new ModelInfo
        {
            Name = "gte-small-zh",
            DisplayName = "GTE Small Chinese",
            Description = "阿里GTE中文向量模型，轻量高效，适合中文语义搜索",
            Dimension = 512,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh" },
            BenchmarkScore = 0.84f,
            DownloadUrl = "thenlper/gte-small-zh",
            ModelSizeBytes = 130 * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "gte-base-zh",
            DisplayName = "GTE Base Chinese",
            Description = "阿里GTE中文向量模型 Base 版本，更高精度",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh" },
            BenchmarkScore = 0.88f,
            DownloadUrl = "thenlper/gte-base-zh",
            ModelSizeBytes = 410 * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "gte-large-zh",
            DisplayName = "GTE Large Chinese",
            Description = "阿里GTE中文向量模型 Large 版本，最高精度",
            Dimension = 1024,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh" },
            BenchmarkScore = 0.91f,
            DownloadUrl = "thenlper/gte-large-zh",
            ModelSizeBytes = 650 * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "gte-small",
            DisplayName = "GTE Small Multilingual",
            Description = "阿里GTE多语言向量模型，支持中英日韩等语言",
            Dimension = 384,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "ja", "ko" },
            BenchmarkScore = 0.83f,
            DownloadUrl = "thenlper/gte-small",
            ModelSizeBytes = 130 * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "gte-base",
            DisplayName = "GTE Base Multilingual",
            Description = "阿里GTE多语言向量模型 Base 版本",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "ja", "ko" },
            BenchmarkScore = 0.87f,
            DownloadUrl = "thenlper/gte-base",
            ModelSizeBytes = 410 * 1024 * 1024,
            HasOnnx = false
        },
        // ========== E5 系列（微软）==========
        new ModelInfo
        {
            Name = "e5-small-v2",
            DisplayName = "E5 Small v2",
            Description = "微软E5英文向量模型，高效检索",
            Dimension = 384,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "en" },
            BenchmarkScore = 0.81f,
            DownloadUrl = "intfloat/e5-small-v2",
            ModelSizeBytes = 130 * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "e5-base-v2",
            DisplayName = "E5 Base v2",
            Description = "微软E5英文向量模型 Base 版本，更高精度",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "en" },
            BenchmarkScore = 0.86f,
            DownloadUrl = "intfloat/e5-base-v2",
            ModelSizeBytes = 438 * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "multilingual-e5-small",
            DisplayName = "Multilingual E5 Small",
            Description = "微软多语言E5向量模型，支持100+语言",
            Dimension = 384,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "ja", "ko", "fr", "de", "es" },
            BenchmarkScore = 0.82f,
            DownloadUrl = "intfloat/multilingual-e5-small",
            ModelSizeBytes = 470 * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "multilingual-e5-base",
            DisplayName = "Multilingual E5 Base",
            Description = "微软多语言E5向量模型 Base 版本，更高精度",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "ja", "ko", "fr", "de", "es" },
            BenchmarkScore = 0.87f,
            DownloadUrl = "intfloat/multilingual-e5-base",
            ModelSizeBytes = (long)(1.1 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        // ========== text2vec 系列（shibing624）==========
        new ModelInfo
        {
            Name = "text2vec-base-chinese",
            DisplayName = "Text2Vec Base Chinese",
            Description = "中文文本向量模型，适合中文语义相似度计算",
            Dimension = 768,
            MaxSequenceLength = 256,
            ModelType = "embedding",
            Languages = new[] { "zh" },
            BenchmarkScore = 0.80f,
            DownloadUrl = "shibing624/text2vec-base-chinese",
            ModelSizeBytes = 406 * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "text2vec-large-chinese",
            DisplayName = "Text2Vec Large Chinese",
            Description = "中文文本向量模型 Large 版本，更高精度",
            Dimension = 1024,
            MaxSequenceLength = 256,
            ModelType = "embedding",
            Languages = new[] { "zh" },
            BenchmarkScore = 0.84f,
            DownloadUrl = "shibing624/text2vec-large-chinese",
            ModelSizeBytes = (long)(1.3 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        // ========== paraphrase 系列 ==========
        new ModelInfo
        {
            Name = "paraphrase-multilingual-MiniLM-L12-v2",
            DisplayName = "Paraphrase Multilingual MiniLM L12",
            Description = "多语言语义相似度模型，支持50+语言，轻量高效",
            Dimension = 384,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "ja", "ko", "fr", "de", "es" },
            BenchmarkScore = 0.80f,
            DownloadUrl = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2",
            ModelSizeBytes = 470 * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "paraphrase-multilingual-mpnet-base-v2",
            DisplayName = "Paraphrase Multilingual MPNet Base",
            Description = "多语言语义相似度模型，更高精度",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "ja", "ko", "fr", "de", "es" },
            BenchmarkScore = 0.85f,
            DownloadUrl = "sentence-transformers/paraphrase-multilingual-mpnet-base-v2",
            ModelSizeBytes = (long)(1.0 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        // ========== MTEB 榜单高分模型 ==========
        new ModelInfo
        {
            Name = "bge-reranker-base",
            DisplayName = "BGE Reranker Base",
            Description = "BAAI重排序模型，用于二次精排提升检索质量",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "reranker",
            Languages = new[] { "zh", "en" },
            BenchmarkScore = 0.90f,
            DownloadUrl = "BAAI/bge-reranker-base",
            ModelSizeBytes = (long)(1.1 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "stella-base-zh-v3-1792",
            DisplayName = "Stella Base Chinese v3",
            Description = "中文向量模型，MTEB中文榜单高分模型",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh" },
            BenchmarkScore = 0.88f,
            DownloadUrl = "infgrad/stella-base-zh-v3-1792",
            ModelSizeBytes = 406 * 1024 * 1024,
            HasOnnx = false
        }
    };

    public ModelManager(string modelsPath, ConfigManager configManager)
    {
        _modelsPath = Path.GetFullPath(modelsPath);
        _configManager = configManager;
        _modelRegistry = new Dictionary<string, ModelInfo>();
        
        InitializeRegistry();
    }

    private void InitializeRegistry()
    {
        // 加载预定义模型
        foreach (var model in PredefinedModels)
        {
            _modelRegistry[model.Name] = model;
        }

        // 扫描本地模型目录
        ScanLocalModels();

        // 从配置文件检查当前模型路径
        CheckConfiguredModel();
    }

    /// <summary>
    /// 检查配置文件中的模型路径，标记已下载的模型
    /// </summary>
    private void CheckConfiguredModel()
    {
        try
        {
            var config = LoadConfigAsync().GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(config.Embedding?.ModelPath))
            {
                var modelPath = config.Embedding.ModelPath;
                if (File.Exists(modelPath))
                {
                    var modelDir = Path.GetDirectoryName(modelPath);
                    var modelName = config.Embedding.ModelName ?? Path.GetFileName(modelDir);

                    if (_modelRegistry.TryGetValue(modelName, out var model))
                    {
                        model.IsDownloaded = true;
                        model.LocalPath = modelDir;
                        _currentModel = model;
                        Console.WriteLine($"[ModelManager] 从配置加载当前模型: {modelName}, 路径: {modelDir}");
                    }
                    else
                    {
                        // 未注册的模型，添加到注册表
                        var dimension = DetectModelDimension(modelDir ?? "");
                        var directorySize = 0L;
                        if (!string.IsNullOrEmpty(modelDir) && Directory.Exists(modelDir))
                        {
                            directorySize = Directory.GetFiles(modelDir, "*", SearchOption.AllDirectories)
                                .Sum(f => new FileInfo(f).Length);
                        }

                        var newModel = new ModelInfo
                        {
                            Name = modelName,
                            DisplayName = modelName,
                            Description = "本地模型",
                            Dimension = dimension,
                            IsDownloaded = true,
                            LocalPath = modelDir,
                            ModelSizeBytes = directorySize,
                            HasOnnx = true
                        };
                        _modelRegistry[modelName] = newModel;
                        _currentModel = newModel;
                        Console.WriteLine($"[ModelManager] 注册新模型: {modelName}, 路径: {modelDir}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 检查配置模型失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 刷新本地模型列表
    /// </summary>
    public void RefreshLocalModels()
    {
        ScanLocalModels();
    }

    private void ScanLocalModels()
    {
        if (!Directory.Exists(_modelsPath))
        {
            Directory.CreateDirectory(_modelsPath);
            return;
        }

        foreach (var dir in Directory.GetDirectories(_modelsPath))
        {
            var modelName = Path.GetFileName(dir);
            var onnxPath = Path.Combine(dir, "model.onnx");

            if (File.Exists(onnxPath))
            {
                var fileInfo = new FileInfo(onnxPath);
                var directorySize = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
                var onnxFormat = DetectOnnxFormat(dir);
                var canConvert = directorySize < 2L * 1024 * 1024 * 1024; // < 2GB

                // 如果检测到残缺的 ONNX 文件，标记为未下载（需要重新转换）
                var hasValidOnnx = onnxFormat != "invalid";

                if (_modelRegistry.TryGetValue(modelName, out var model))
                {
                    model.IsDownloaded = hasValidOnnx; // 残缺文件视为未下载
                    model.LocalPath = dir;
                    model.ModelSizeBytes = directorySize;
                    model.OnnxFormat = onnxFormat;
                    model.HasOnnx = hasValidOnnx;
                    model.CanConvertFormat = canConvert;
                }
                else
                {
                    // 发现未注册的本地模型，尝试从配置推断维度
                    var dimension = DetectModelDimension(dir);

                    _modelRegistry[modelName] = new ModelInfo
                    {
                        Name = modelName,
                        DisplayName = modelName,
                        Description = "本地模型",
                        Dimension = dimension,
                        IsDownloaded = hasValidOnnx, // 残缺文件视为未下载
                        LocalPath = dir,
                        ModelSizeBytes = directorySize,
                        HasOnnx = hasValidOnnx,
                        OnnxFormat = onnxFormat,
                        CanConvertFormat = canConvert
                    };
                }

                // 如果是残缺文件，尝试自动重新转换
                if (onnxFormat == "invalid")
                {
                    Console.WriteLine($"[ModelManager] 检测到残缺 ONNX 文件，建议重新转换: {modelName}");
                    // 可以在这里触发自动重新转换，但目前先标记状态让用户手动处理
                }
            }
        }
    }

    /// <summary>
    /// 检测模型维度
    /// </summary>
    private static int DetectModelDimension(string modelDir)
    {
        try
        {
            var configPath = Path.Combine(modelDir, "config.json");
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                var config = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
                
                if (config.TryGetProperty("hidden_size", out var hiddenSize))
                {
                    return hiddenSize.GetInt32();
                }
            }
        }
        catch { }
        
        return 512; // 默认维度
    }

    public Task<List<ModelInfo>> GetAvailableModelsAsync()
    {
        return Task.FromResult(_modelRegistry.Values.ToList());
    }

    public List<ModelInfo> GetDownloadedModels()
    {
        return _modelRegistry.Values.Where(m => m.IsDownloaded).ToList();
    }

    public ModelInfo? GetCurrentModel()
    {
        return _currentModel;
    }

    public async Task<bool> SwitchModelAsync(string modelName)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            Console.WriteLine($"[ModelManager] 模型不存在: {modelName}");
            return false;
        }

        if (!model.IsDownloaded)
        {
            Console.WriteLine($"[ModelManager] 模型未下载: {modelName}");
            return false;
        }

        try
        {
            var config = await LoadConfigAsync();
            config.Embedding.ModelPath = Path.Combine(model.LocalPath ?? "", "model.onnx");
            config.Embedding.ModelName = modelName;
            await SaveConfigAsync(config);

            _currentModel = model;
            Console.WriteLine($"[ModelManager] 已切换到模型: {model.DisplayName}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 切换模型失败: {ex.Message}");
            return false;
        }
    }

    public async Task<(bool Success, string? Error)> DownloadModelAsync(string modelName, IProgress<float>? progress = null, string? targetFormat = null, string? onnxVariantPath = null)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            Console.WriteLine($"[ModelManager] 模型不存在: {modelName}");
            return (false, $"模型不存在: {modelName}");
        }

        if (model.IsDownloaded)
        {
            Console.WriteLine($"[ModelManager] 模型已存在: {modelName}");
            return (true, null);
        }

        var modelDir = Path.Combine(_modelsPath, modelName);
        
        try
        {
            Console.WriteLine($"[ModelManager] 开始下载模型: {model.DisplayName}");
            
            if (!model.HasOnnx)
            {
                Console.WriteLine($"[ModelManager] 注意: 此模型需要从 PyTorch 转换为 ONNX");
                Console.WriteLine($"[ModelManager] 请确保已安装 Python 和相关依赖 (pip install torch transformers)");
            }
            
            progress?.Report(0);

            Directory.CreateDirectory(modelDir);

            var downloader = new HuggingFaceModelDownloader();
            await downloader.DownloadAsync(
                model.DownloadUrl ?? $"BAAI/{modelName}",
                modelDir,
                new Progress<float>(p => progress?.Report(p)),
                default,
                targetFormat,
                onnxVariantPath
            );

            // 验证 ONNX 文件
            var onnxPath = Path.Combine(modelDir, "model.onnx");
            if (!File.Exists(onnxPath))
            {
                // 查找子目录中的 ONNX 文件（如 onnx/model.onnx）
                var subOnnxFiles = Directory.GetFiles(modelDir, "model.onnx", SearchOption.AllDirectories);
                if (subOnnxFiles.Length > 0)
                {
                    var srcOnnx = subOnnxFiles[0];
                    var srcDir = Path.GetDirectoryName(srcOnnx)!;
                    Console.WriteLine($"[ModelManager] 在子目录中找到 ONNX 文件: {srcOnnx}");
                    File.Copy(srcOnnx, onnxPath, true);
                    // 如果有配套的 .data 文件也一并复制
                    var srcData = Path.Combine(srcDir, "model.onnx.data");
                    if (File.Exists(srcData))
                    {
                        File.Copy(srcData, Path.Combine(modelDir, "model.onnx.data"), true);
                    }
                    // 如果有 1_Pooling 配置也复制（sentence-transformers 模型需要）
                    var srcPoolingDir = Path.Combine(Path.GetDirectoryName(srcDir)!, "1_Pooling");
                    if (Directory.Exists(srcPoolingDir))
                    {
                        var destPoolingDir = Path.Combine(modelDir, "1_Pooling");
                        if (!Directory.Exists(destPoolingDir))
                        {
                            Directory.CreateDirectory(destPoolingDir);
                            foreach (var f in Directory.GetFiles(srcPoolingDir))
                            {
                                File.Copy(f, Path.Combine(destPoolingDir, Path.GetFileName(f)), true);
                            }
                        }
                    }
                }
                else
                {
                    throw new InvalidOperationException("模型文件下载不完整，缺少 model.onnx");
                }
            }

            model.IsDownloaded = true;
            model.LocalPath = modelDir;
            model.ModelSizeBytes = new FileInfo(onnxPath).Length;
            model.Dimension = DetectModelDimension(modelDir);
            model.OnnxFormat = DetectOnnxFormat(modelDir);
            model.HasOnnx = model.OnnxFormat != "invalid";
            model.CanConvertFormat = model.ModelSizeBytes < 2L * 1024 * 1024 * 1024;

            progress?.Report(100);
            Console.WriteLine($"[ModelManager] 模型下载完成: {model.DisplayName}");
            return (true, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 模型下载失败: {ex.Message}");
            
            // 清理不完整的下载
            try
            {
                if (Directory.Exists(modelDir))
                {
                    Directory.Delete(modelDir, true);
                }
            }
            catch { }
            
            return (false, ex.Message);
        }
    }

    public Task<bool> DeleteModelAsync(string modelName)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            return Task.FromResult(false);
        }

        if (!model.IsDownloaded)
        {
            return Task.FromResult(false);
        }

        try
        {
            if (Directory.Exists(model.LocalPath))
            {
                Directory.Delete(model.LocalPath, true);
            }

            model.IsDownloaded = false;
            model.LocalPath = null;
            model.ModelSizeBytes = 0;

            Console.WriteLine($"[ModelManager] 模型已删除: {model.DisplayName}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 删除模型失败: {ex.Message}");
            return Task.FromResult(false);
        }
    }

    public Task<List<ModelInfo>> GetRecommendedModelsAsync(string? language = null, bool preferGpu = true)
    {
        var recommended = _modelRegistry.Values
            .Where(m => 
                (language == null || (m.Languages?.Contains(language) ?? false)) &&
                (!preferGpu || m.IsGpuSupported))
            .OrderByDescending(m => m.BenchmarkScore ?? 0)
            .Take(5)
            .ToList();

        return Task.FromResult(recommended);
    }

    /// <summary>
    /// 检测模型 ONNX 格式
    /// </summary>
    public string DetectOnnxFormat(string modelDir)
    {
        if (string.IsNullOrEmpty(modelDir) || !Directory.Exists(modelDir))
            return "unknown";

        var onnxPath = Path.Combine(modelDir, "model.onnx");
        var onnxDataPath = Path.Combine(modelDir, "model.onnx.data");

        if (!File.Exists(onnxPath))
            return "unknown";

        // 如果存在 .onnx.data 文件，则为外部数据格式
        if (File.Exists(onnxDataPath))
            return "external";

        // 检查 model.onnx 文件大小，如果大于 2GB 则可能是外部格式（但 .data 被删除）
        var onnxFileInfo = new FileInfo(onnxPath);
        if (onnxFileInfo.Length > 2L * 1024 * 1024 * 1024)
            return "external"; // 大文件也可能是嵌入式，但概率较低

        // 检测 ONNX 文件内部是否引用了外部数据
        if (HasExternalDataReferences(onnxPath))
        {
            // ONNX 文件引用了外部数据，但 .data 文件不存在
            Console.WriteLine($"[ModelManager] ONNX 文件引用外部数据但 .data 文件缺失: {onnxPath}");
            return "invalid";
        }

        // 检测残缺的 ONNX 文件：如果存在 PyTorch 模型但 ONNX 文件很小，说明转换失败
        var pytorchPath = Path.Combine(modelDir, "pytorch_model.bin");
        var safetensorsPath = Path.Combine(modelDir, "model.safetensors");

        var pytorchExists = File.Exists(pytorchPath) || File.Exists(safetensorsPath);
        if (pytorchExists && onnxFileInfo.Length < 10 * 1024 * 1024) // < 10MB
        {
            // PyTorch 模型存在但 ONNX 文件很小，可能是残缺文件
            Console.WriteLine($"[ModelManager] 检测到残缺的 ONNX 文件: {onnxPath} ({onnxFileInfo.Length / 1024.0 / 1024.0:F2} MB)");
            return "invalid";
        }

        return "embedded";
    }

    /// <summary>
    /// 检查 ONNX 文件是否引用了外部数据
    /// 通过扫描整个文件查找 protobuf 中的 ExternalDataDescriptor 关键标识
    /// </summary>
    private static bool HasExternalDataReferences(string onnxPath)
    {
        try
        {
            // ONNX protobuf 中外部数据引用的特征：
            // 1. "model.onnx.data" — 外部数据文件名（最可靠）
            // 2. ".onnx.data" — 通用的外部数据文件扩展名
            // 3. "external_data" — ONNX ExternalDataDescriptor 中的字段名
            // 注意：不能使用 "location" 作为检测条件，该词在 protobuf 中太常见，会误判

            using var fs = new FileStream(onnxPath, FileMode.Open, FileAccess.Read);
            using var reader = new BinaryReader(fs);

            // 扫描整个文件而非仅前 8KB，因为外部数据引用可能在文件任何位置
            var bufferSize = Math.Min(1024 * 1024, (int)fs.Length); // 最多扫描 1MB
            var buffer = reader.ReadBytes(bufferSize);
            var content = System.Text.Encoding.UTF8.GetString(buffer);

            if (content.Contains("model.onnx.data"))
            {
                return true;
            }

            // 检查 .onnx.data 通配模式（如 all-MiniLM-L6-v2.onnx.data）
            if (content.Contains(".onnx.data"))
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 检查 ONNX 外部数据引用失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 转换模型格式
    /// </summary>
    public async Task<(bool Success, string? Error)> ConvertFormatAsync(
        string modelName,
        string targetFormat,
        IProgress<float>? progress = null)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            return (false, $"模型不存在: {modelName}");
        }

        if (!model.IsDownloaded || string.IsNullOrEmpty(model.LocalPath))
        {
            return (false, $"模型未下载: {modelName}");
        }

        var currentFormat = DetectOnnxFormat(model.LocalPath);
        if (currentFormat == targetFormat)
        {
            return (false, $"模型已是目标格式: {targetFormat}");
        }

        if (currentFormat == "unknown")
        {
            return (false, "无法检测当前模型格式");
        }

        try
        {
            var onnxPath = Path.Combine(model.LocalPath, "model.onnx");
            var onnxDataPath = Path.Combine(model.LocalPath, "model.onnx.data");
            var backupPath = Path.Combine(model.LocalPath, "model.onnx.bak");
            var backupDataPath = Path.Combine(model.LocalPath, "model.onnx.data.bak");

            // 备份原始文件
            progress?.Report(5);
            if (File.Exists(onnxPath))
            {
                File.Copy(onnxPath, backupPath, true);
                Console.WriteLine($"[ModelManager] 已备份原始模型: {backupPath}");
            }
            if (File.Exists(onnxDataPath))
            {
                File.Copy(onnxDataPath, backupDataPath, true);
                Console.WriteLine($"[ModelManager] 已备份外部数据文件: {backupDataPath}");
            }

            progress?.Report(10);

            // 使用 HuggingFaceModelDownloader 进行转换
            var downloader = new HuggingFaceModelDownloader();
            var success = await downloader.ConvertToOnnxAsync(
                model.LocalPath,
                onnxPath,
                targetFormat,
                new Progress<float>(p => progress?.Report(10 + p * 0.8f)));

            if (success)
            {
                // 验证转换后的实际格式（PyTorch 可能自动回退到 external 格式）
                var actualFormat = DetectOnnxFormat(model.LocalPath);
                if (actualFormat != targetFormat)
                {
                    Console.WriteLine($"[ModelManager] 转换后实际格式为 {actualFormat}，未达到目标 {targetFormat}，恢复备份");
                    if (File.Exists(backupPath))
                    {
                        File.Copy(backupPath, onnxPath, true);
                        File.Delete(backupPath);
                    }
                    if (File.Exists(backupDataPath))
                    {
                        File.Copy(backupDataPath, onnxDataPath, true);
                        File.Delete(backupDataPath);
                    }
                    else if (File.Exists(onnxDataPath) && actualFormat != "external")
                    {
                        // 转换产生了不应该存在的 .data 文件，删除
                        File.Delete(onnxDataPath);
                    }
                    // 如果实际是 external 格式但目标是 embedded，保留 .data 文件
                    if (actualFormat == "external" && File.Exists(onnxDataPath))
                    {
                        Console.WriteLine($"[ModelManager] 保留外部数据文件: {onnxDataPath}");
                    }
                    model.OnnxFormat = actualFormat;
                    return (false, $"转换后格式仍为 {actualFormat}，模型可能过大无法嵌入（>2GB）");
                }

                // 删除外部数据文件（仅在实际确认为嵌入式时）
                if (targetFormat == "embedded" && File.Exists(onnxDataPath))
                {
                    File.Delete(onnxDataPath);
                    Console.WriteLine($"[ModelManager] 已删除外部数据文件: {onnxDataPath}");
                }

                // 清理备份文件
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                if (File.Exists(backupDataPath))
                {
                    File.Delete(backupDataPath);
                }

                // 更新模型信息
                model.OnnxFormat = targetFormat;
                model.ModelSizeBytes = Directory.GetFiles(model.LocalPath, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);

                progress?.Report(100);
                Console.WriteLine($"[ModelManager] 模型格式转换完成: {modelName} -> {targetFormat}");
                return (true, null);
            }
            else
            {
                // 恢复备份
                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, onnxPath, true);
                    File.Delete(backupPath);
                }
                if (File.Exists(backupDataPath))
                {
                    File.Copy(backupDataPath, onnxDataPath, true);
                    File.Delete(backupDataPath);
                }
                else if (File.Exists(onnxDataPath))
                {
                    // 转换失败但产生了 .data 文件，原模型可能没有，需要清理
                    // 但无法确定原模型是否有 .data，保守起见不删除
                }
                Console.WriteLine($"[ModelManager] 已恢复原始模型");
                return (false, "模型转换失败，已恢复原始模型");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 模型格式转换异常: {ex.Message}");
            return (false, $"转换异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 添加自定义 HuggingFace 模型
    /// </summary>
    public async Task<(bool Success, string? Error, ModelInfo? Model)> AddCustomModelAsync(
        string huggingFaceId,
        string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(huggingFaceId))
        {
            return (false, "HuggingFace 模型 ID 不能为空", null);
        }

        // 验证格式 (owner/model-name)
        if (!huggingFaceId.Contains('/') || huggingFaceId.Split('/').Length != 2)
        {
            return (false, "模型 ID 格式错误，应为: owner/model-name (例如: BAAI/bge-base-zh-v1.5)", null);
        }

        // 检查是否已存在
        var parts = huggingFaceId.Split('/');
        var modelName = parts[1];

        if (_modelRegistry.ContainsKey(modelName))
        {
            return (false, $"模型已存在: {modelName}", null);
        }

        try
        {
            // 获取模型信息
            var downloader = new HuggingFaceModelDownloader();
            var files = await downloader.GetModelFilesAsync(huggingFaceId);

            if (files.Count == 0)
            {
                return (false, $"无法获取模型信息，请检查模型 ID 是否正确: {huggingFaceId}", null);
            }

            // 估算模型大小
            var modelSize = files.Where(f => 
                f.Path.EndsWith(".bin") || 
                f.Path.EndsWith(".safetensors") ||
                f.Path.EndsWith(".onnx"))
                .Sum(f => f.Size);

            // 检测是否有 ONNX 版本
            var hasOnnx = files.Any(f => f.Path.EndsWith(".onnx"));

            // 创建模型信息
            var newModel = new ModelInfo
            {
                Name = modelName,
                DisplayName = displayName ?? modelName,
                Description = $"自定义模型: {huggingFaceId}",
                Dimension = 768, // 默认维度，下载后会自动检测
                DownloadUrl = huggingFaceId,
                ModelSizeBytes = modelSize,
                HasOnnx = hasOnnx,
                IsDownloaded = false,
                Languages = new[] { "multi" }
            };

            _modelRegistry[modelName] = newModel;
            Console.WriteLine($"[ModelManager] 已添加自定义模型: {modelName} ({huggingFaceId})");

            return (true, null, newModel);
        }
        catch (Exception ex)
        {
            return (false, $"添加模型失败: {ex.Message}", null);
        }
    }

    /// <summary>
    /// 获取模型可用的 ONNX 变体列表
    /// </summary>
    [Obsolete("Use GetDownloadOptionsAsync instead")]
    public async Task<List<OnnxVariant>> GetOnnxVariantsAsync(string modelName)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            Console.WriteLine($"[ModelManager] 模型不存在: {modelName}");
            return new List<OnnxVariant>();
        }

        try
        {
            var downloader = new HuggingFaceModelDownloader();
            var variants = await downloader.GetOnnxVariantsAsync(model.DownloadUrl ?? $"BAAI/{modelName}");
            Console.WriteLine($"[ModelManager] 获取到 {variants.Count} 个 ONNX 变体: {modelName}");
            return variants;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 获取 ONNX 变体失败: {ex.Message}");
            return new List<OnnxVariant>();
        }
    }

    /// <summary>
    /// 获取模型的下载选项
    /// </summary>
    public async Task<ModelDownloadOptions> GetDownloadOptionsAsync(string modelName)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            Console.WriteLine($"[ModelManager] 模型不存在: {modelName}");
            return new ModelDownloadOptions { ModelName = modelName };
        }

        try
        {
            var downloader = new HuggingFaceModelDownloader();
            var options = await downloader.GetDownloadOptionsAsync(model.DownloadUrl ?? $"BAAI/{modelName}");
            Console.WriteLine($"[ModelManager] 获取下载选项: {modelName}, HasOnnx={options.HasOnnx}, Options={options.AllOptions.Count}");
            return options;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 获取下载选项失败: {ex.Message}");
            return new ModelDownloadOptions { ModelName = modelName };
        }
    }

    /// <summary>
    /// 设置模型保存路径（可选迁移已有模型）
    /// </summary>
    public async Task<(bool Success, string? Error, List<string> Migrated)> SetModelsPathAsync(
        string newPath, bool migrateExisting = false, CancellationToken cancellationToken = default)
    {
        var migrated = new List<string>();

        // 1. 验证路径不为空
        if (string.IsNullOrWhiteSpace(newPath))
        {
            return (false, "路径不能为空", migrated);
        }

        // 2. 规范化路径
        var normalizedPath = Path.GetFullPath(newPath);

        // 3. 禁止根目录
        if (normalizedPath == Path.GetPathRoot(normalizedPath))
        {
            return (false, "不能使用磁盘根目录作为模型路径", migrated);
        }

        // 4. 禁止系统目录
        var systemDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var sysDir in systemDirs)
        {
            if (!string.IsNullOrEmpty(sysDir) &&
                normalizedPath.StartsWith(sysDir, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "不能使用系统目录作为模型路径", migrated);
            }
        }

        // 5. 禁止路径遍历
        if (newPath.Contains("..") || newPath.Contains('~'))
        {
            return (false, "路径不能包含 '..' 或 '~'", migrated);
        }

        try
        {
            // 6. 如需迁移，复制现有模型到新路径
            if (migrateExisting && Directory.Exists(_modelsPath))
            {
                Directory.CreateDirectory(normalizedPath);

                foreach (var dir in Directory.GetDirectories(_modelsPath))
                {
                    var dirName = Path.GetFileName(dir);
                    var destDir = Path.Combine(normalizedPath, dirName);

                    if (!Directory.Exists(destDir))
                    {
                        Console.WriteLine($"[ModelManager] 迁移模型: {dirName} -> {normalizedPath}");
                        CopyDirectory(dir, destDir);
                        migrated.Add(dirName);
                    }
                }
            }

            // 7. 创建新目录
            Directory.CreateDirectory(normalizedPath);

            // 8. 更新配置
            var config = await LoadConfigAsync();
            config.Embedding.ModelsPath = normalizedPath;
            await SaveConfigAsync(config);

            // 9. 更新内部字段并重新扫描
            _modelsPath = normalizedPath;
            ScanLocalModels();

            Console.WriteLine($"[ModelManager] 模型路径已更新: {normalizedPath} (迁移了 {migrated.Count} 个模型)");
            return (true, null, migrated);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 设置模型路径失败: {ex.Message}");
            return (false, $"设置路径失败: {ex.Message}", migrated);
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    private async Task<ObsidianRagConfig> LoadConfigAsync()
    {
        // 使用 ConfigManager 加载配置，确保缓存一致
        return await Task.FromResult(_configManager.Load());
    }

    private async Task SaveConfigAsync(ObsidianRagConfig config)
    {
        // 使用 ConfigManager 保存配置，确保缓存同步
        _configManager.Save(config);
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
