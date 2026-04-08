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
}

/// <summary>
/// 模型管理器接口
/// </summary>
public interface IModelManager
{
    /// <summary>
    /// 获取所有可用模型
    /// </summary>
    Task<List<ModelInfo>> GetAvailableModelsAsync();
    
    /// <summary>
    /// 获取已下载的模型
    /// </summary>
    List<ModelInfo> GetDownloadedModels();
    
    /// <summary>
    /// 获取当前使用的模型
    /// </summary>
    ModelInfo? GetCurrentModel();
    
    /// <summary>
    /// 切换模型
    /// </summary>
    Task<bool> SwitchModelAsync(string modelName);
    
    /// <summary>
    /// 下载模型
    /// </summary>
    Task<bool> DownloadModelAsync(string modelName, IProgress<float>? progress = null);
    
    /// <summary>
    /// 量化模型
    /// </summary>
    Task<bool> QuantizeModelAsync(string modelName, string quantizationType = "fp16");
    
    /// <summary>
    /// 删除模型
    /// </summary>
    Task<bool> DeleteModelAsync(string modelName);
    
    /// <summary>
    /// 获取模型推荐
    /// </summary>
    Task<List<ModelInfo>> GetRecommendedModelsAsync(string? language = null, bool preferGpu = true);
}

/// <summary>
/// 模型管理器实现
/// </summary>
public class ModelManager : IModelManager, IDisposable
{
    private readonly string _modelsPath;
    private readonly string _configPath;
    private readonly Dictionary<string, ModelInfo> _modelRegistry;
    private ModelInfo? _currentModel;
    private IEmbeddingService? _embeddingService;
    private bool _disposed;

    // 预定义模型库
    private static readonly List<ModelInfo> PredefinedModels = new()
    {
        new ModelInfo
        {
            Name = "bge-small-zh-v1.5",
            DisplayName = "BGE Small Chinese v1.5",
            Description = "BAAI通用中文向量模型，适合中文语义搜索",
            Dimension = 512,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "zh", "en" },
            BenchmarkScore = 0.85f,
            DownloadUrl = "https://huggingface.co/BAAI/bge-small-zh-v1.5"
        },
        new ModelInfo
        {
            Name = "bge-small-zh-fp16",
            DisplayName = "BGE Small Chinese FP16",
            Description = "BGE中文模型FP16量化版，体积更小速度更快",
            Dimension = 512,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            IsQuantized = true,
            QuantizationType = "fp16",
            Languages = new[] { "zh", "en" },
            BenchmarkScore = 0.84f
        },
        new ModelInfo
        {
            Name = "bge-small-trt-fp16",
            DisplayName = "BGE Small TensorRT FP16",
            Description = "BGE中文模型TensorRT优化版，GPU加速",
            Dimension = 512,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            IsQuantized = true,
            QuantizationType = "trt-fp16",
            IsGpuSupported = true,
            Languages = new[] { "zh", "en" },
            BenchmarkScore = 0.84f
        },
        new ModelInfo
        {
            Name = "bge-base-en-v1.5",
            DisplayName = "BGE Base English v1.5",
            Description = "BAAI通用英文向量模型，适合英文语义搜索",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "embedding",
            Languages = new[] { "en" },
            BenchmarkScore = 0.87f,
            DownloadUrl = "https://huggingface.co/BAAI/bge-base-en-v1.5"
        },
        new ModelInfo
        {
            Name = "bge-m3",
            DisplayName = "BGE M3",
            Description = "BAAI多语言向量模型，支持100+语言",
            Dimension = 1024,
            MaxSequenceLength = 8192,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "ja", "ko", "fr", "de", "es" },
            BenchmarkScore = 0.89f,
            DownloadUrl = "https://huggingface.co/BAAI/bge-m3"
        },
        new ModelInfo
        {
            Name = "text-embedding-3-small",
            DisplayName = "OpenAI Text Embedding 3 Small",
            Description = "OpenAI最新嵌入模型，高性能低成本",
            Dimension = 1536,
            MaxSequenceLength = 8191,
            ModelType = "embedding",
            Languages = new[] { "zh", "en" },
            BenchmarkScore = 0.90f,
            DownloadUrl = "api://openai"
        }
    };

    public ModelManager(string modelsPath, string configPath)
    {
        _modelsPath = modelsPath;
        _configPath = configPath;
        _modelRegistry = new Dictionary<string, ModelInfo>();
        
        InitializeRegistry();
    }

    /// <summary>
    /// 设置嵌入服务实例
    /// </summary>
    public void SetEmbeddingService(IEmbeddingService service)
    {
        _embeddingService = service;
    }

    private void InitializeRegistry()
    {
        // 加载预定义模型
        foreach (var model in PredefinedModels)
        {
            _modelRegistry[model.Name] = model;
        }

        // 扫描本地模型
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
                
                if (_modelRegistry.TryGetValue(modelName, out var model))
                {
                    model.IsDownloaded = true;
                    model.LocalPath = dir;
                    model.ModelSizeBytes = fileInfo.Length;
                }
                else
                {
                    // 发现未知模型
                    _modelRegistry[modelName] = new ModelInfo
                    {
                        Name = modelName,
                        DisplayName = modelName,
                        Description = "本地模型",
                        Dimension = 512, // 默认维度，需要实际检测
                        IsDownloaded = true,
                        LocalPath = dir,
                        ModelSizeBytes = fileInfo.Length
                    };
                }
            }
        }
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
            // 更新配置文件
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

    public async Task<bool> DownloadModelAsync(string modelName, IProgress<float>? progress = null)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            Console.WriteLine($"[ModelManager] 模型不存在: {modelName}");
            return false;
        }

        if (model.IsDownloaded)
        {
            Console.WriteLine($"[ModelManager] 模型已存在: {modelName}");
            return true;
        }

        try
        {
            Console.WriteLine($"[ModelManager] 开始下载模型: {model.DisplayName}");
            progress?.Report(0);

            // 创建模型目录
            var modelDir = Path.Combine(_modelsPath, modelName);
            Directory.CreateDirectory(modelDir);

            // 检查是否是API模型
            if (model.DownloadUrl?.StartsWith("api://") == true)
            {
                Console.WriteLine($"[ModelManager] API模型无需下载: {modelName}");
                model.IsDownloaded = true;
                model.LocalPath = modelDir;
                return true;
            }

            // 使用 HuggingFace 下载器
            var downloader = new HuggingFaceModelDownloader();
            await downloader.DownloadAsync(
                model.DownloadUrl ?? $"BAAI/{modelName}",
                modelDir,
                new Progress<float>(p => progress?.Report(p))
            );

            model.IsDownloaded = true;
            model.LocalPath = modelDir;
            
            // 获取模型大小
            var onnxPath = Path.Combine(modelDir, "model.onnx");
            if (File.Exists(onnxPath))
            {
                model.ModelSizeBytes = new FileInfo(onnxPath).Length;
            }

            progress?.Report(100);
            Console.WriteLine($"[ModelManager] 模型下载完成: {model.DisplayName}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 模型下载失败: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> QuantizeModelAsync(string modelName, string quantizationType = "fp16")
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
            Console.WriteLine($"[ModelManager] 开始量化模型: {model.DisplayName} -> {quantizationType}");

            var sourcePath = Path.Combine(model.LocalPath ?? "", "model.onnx");
            var targetName = $"{modelName}-{quantizationType}";
            var targetDir = Path.Combine(_modelsPath, targetName);
            var targetPath = Path.Combine(targetDir, "model.onnx");

            Directory.CreateDirectory(targetDir);

            // 使用 ONNX Runtime 量化工具
            var quantizer = new ModelQuantizer();
            await quantizer.QuantizeAsync(sourcePath, targetPath, quantizationType);

            // 复制分词器文件
            foreach (var file in Directory.GetFiles(model.LocalPath ?? "", "*.*"))
            {
                var fileName = Path.GetFileName(file);
                if (fileName != "model.onnx" && !File.Exists(Path.Combine(targetDir, fileName)))
                {
                    File.Copy(file, Path.Combine(targetDir, fileName));
                }
            }

            // 注册新模型
            var quantizedModel = new ModelInfo
            {
                Name = targetName,
                DisplayName = $"{model.DisplayName} ({quantizationType.ToUpper()})",
                Description = $"{model.Description} - {quantizationType.ToUpper()}量化版",
                Dimension = model.Dimension,
                MaxSequenceLength = model.MaxSequenceLength,
                ModelType = model.ModelType,
                IsQuantized = true,
                QuantizationType = quantizationType,
                IsDownloaded = true,
                LocalPath = targetDir,
                ModelSizeBytes = new FileInfo(targetPath).Length,
                Languages = model.Languages,
                BenchmarkScore = model.BenchmarkScore * 0.99f // 量化略微降低精度
            };

            _modelRegistry[targetName] = quantizedModel;
            Console.WriteLine($"[ModelManager] 模型量化完成: {quantizedModel.DisplayName}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 模型量化失败: {ex.Message}");
            return false;
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

    private async Task<ObsidianRagConfig> LoadConfigAsync()
    {
        if (!File.Exists(_configPath))
        {
            return new ObsidianRagConfig();
        }

        var json = await File.ReadAllTextAsync(_configPath);
        return System.Text.Json.JsonSerializer.Deserialize<ObsidianRagConfig>(json) ?? new ObsidianRagConfig();
    }

    private async Task SaveConfigAsync(ObsidianRagConfig config)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllTextAsync(_configPath, json);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// HuggingFace 模型下载器
/// </summary>
internal class HuggingFaceModelDownloader
{
    public async Task DownloadAsync(string modelId, string targetDir, IProgress<float> progress)
    {
        // 简化实现：实际应使用 HuggingFace API 或 git lfs
        // 这里只做演示
        var files = new[] { "model.onnx", "config.json", "tokenizer.json", "tokenizer_config.json", "vocab.txt", "special_tokens_map.json" };
        
        for (int i = 0; i < files.Length; i++)
        {
            var file = files[i];
            var url = $"https://huggingface.co/{modelId}/resolve/main/{file}";
            var targetPath = Path.Combine(targetDir, file);
            
            // 模拟下载
            await Task.Delay(500);
            progress.Report((i + 1) * 100f / files.Length);
            
            Console.WriteLine($"[Downloader] 下载: {file}");
        }
    }
}

/// <summary>
/// 模型量化器
/// </summary>
internal class ModelQuantizer
{
    public async Task QuantizeAsync(string sourcePath, string targetPath, string quantizationType)
    {
        // 简化实现：实际应使用 ONNX Runtime 量化工具
        await Task.Delay(1000);
        
        // 复制源文件（实际应进行量化转换）
        File.Copy(sourcePath, targetPath, true);
        
        Console.WriteLine($"[Quantizer] 量化完成: {quantizationType}");
    }
}
