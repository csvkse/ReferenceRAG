using ReferenceRAG.Core.Features.ModelManagement.Protobuf;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Coordinator;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Models;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.StateMachine;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Strategies;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 模型版本/变体信息（同模型多版本：如 Q4_K_M, Q8_0, FP16, ONNX 等）
/// </summary>
public class ModelVariantInfo
{
    /// <summary>
    /// 变体唯一标识（如 "Q4_K_M", "Q8_0", "onnx-embedded", "onnx-external"）
    /// </summary>
    public string VariantId { get; set; } = string.Empty;

    /// <summary>
    /// 友好显示名称（如 "GGUF Q4_K_M (推荐)", "ONNX 嵌入式"）
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 文件名（如 "Qwen3-Embedding-0.6B-Q4_K_M.gguf", "model.onnx"）
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 相对模型目录的相对路径
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// 绝对物理文件路径
    /// </summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>
    /// 模型格式: "gguf" | "onnx"
    /// </summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// 量化类型（如 "Q4_K_M", "Q8_0", "INT8", "FP16"）
    /// </summary>
    public string? Quantization { get; set; }

    /// <summary>
    /// 文件字节大小
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// 本地是否已下载就绪
    /// </summary>
    public bool IsDownloaded { get; set; } = true;

    /// <summary>
    /// 当前是否处于选中激活状态
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// 是否包含视觉投影器或为视觉多模态变体
    /// </summary>
    public bool IsMultimodal { get; set; }

    /// <summary>
    /// 伴生视觉投影器文件名（如 "mmproj-F16.gguf"）
    /// </summary>
    public string? CompanionMmprojFileName { get; set; }

    /// <summary>
    /// 伴生视觉投影器大小 (字节)
    /// </summary>
    public long CompanionMmprojSizeBytes { get; set; }

    /// <summary>
    /// 本地是否已下载就绪该变体的投影器
    /// </summary>
    public bool HasMmprojLocally { get; set; }
}

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
    /// 是否为多模态架构（支持视觉理解/需配套 mmproj 视觉投影器）
    /// </summary>
    public bool IsMultimodal { get; set; }

    /// <summary>
    /// 本地是否已存在视觉投影器（如 mmproj-F16.gguf）
    /// </summary>
    public bool HasMmprojLocally { get; set; }

    /// <summary>
    /// 本地视觉投影器文件名（如 "mmproj-F16.gguf"）
    /// </summary>
    public string? LocalMmprojFileName { get; set; }

    /// <summary>
    /// 本地视觉投影器文件大小 (字节)
    /// </summary>
    public long LocalMmprojSizeBytes { get; set; }

    /// <summary>
    /// 远端仓库是否提供视觉投影器
    /// </summary>
    public bool RemoteProjectorAvailable { get; set; }

    /// <summary>
    /// 远端推荐视觉投影器文件名（如 "mmproj-F16.gguf"）
    /// </summary>
    public string? RemoteProjectorFileName { get; set; }

    /// <summary>
    /// 远端推荐视觉投影器文件大小 (字节)
    /// </summary>
    public long RemoteProjectorSizeBytes { get; set; }

    /// <summary>
    /// 视觉投影器状态: "not_applicable" | "ready" | "missing" | "downloading"
    /// </summary>
    public string ProjectorStatus { get; set; } = "not_applicable";

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

    /// <summary>
    /// 该模型已安装/已下载的所有版本变体列表
    /// </summary>
    public List<ModelVariantInfo> InstalledVariants { get; set; } = new();

    /// <summary>
    /// 当前选中的激活变体文件名称（若有）
    /// </summary>
    public string? ActiveVariantFileName { get; set; }

    public ModelInfo Clone() => new ModelInfo
    {
        Name = Name,
        DisplayName = DisplayName,
        Description = Description,
        Dimension = Dimension,
        MaxSequenceLength = MaxSequenceLength,
        ModelType = ModelType,
        IsQuantized = IsQuantized,
        QuantizationType = QuantizationType,
        ModelSizeBytes = ModelSizeBytes,
        DownloadUrl = DownloadUrl,
        IsDownloaded = IsDownloaded,
        LocalPath = LocalPath,
        IsGpuSupported = IsGpuSupported,
        Languages = Languages != null ? (string[])Languages.Clone() : null,
        BenchmarkScore = BenchmarkScore,
        HasOnnx = HasOnnx,
        IsMultimodal = IsMultimodal,
        HasMmprojLocally = HasMmprojLocally,
        LocalMmprojFileName = LocalMmprojFileName,
        LocalMmprojSizeBytes = LocalMmprojSizeBytes,
        RemoteProjectorAvailable = RemoteProjectorAvailable,
        RemoteProjectorFileName = RemoteProjectorFileName,
        RemoteProjectorSizeBytes = RemoteProjectorSizeBytes,
        ProjectorStatus = ProjectorStatus,
        AsymmetricEncoding = AsymmetricEncoding != null ? new AsymmetricEncodingConfig
        {
            QueryPrefix = AsymmetricEncoding.QueryPrefix,
            DocumentPrefix = AsymmetricEncoding.DocumentPrefix
        } : null,
        OnnxFormat = OnnxFormat,
        CanConvertFormat = CanConvertFormat,
        InstalledVariants = InstalledVariants != null ? new List<ModelVariantInfo>(InstalledVariants) : new(),
        ActiveVariantFileName = ActiveVariantFileName
    };
}

/// <summary>
/// 视觉投影器状态与远端探测结果
/// </summary>
public class ProjectorStatusResult
{
    public string ModelName { get; set; } = string.Empty;
    public bool IsMultimodal { get; set; }
    public bool HasLocalMmproj { get; set; }
    public string? LocalMmprojFileName { get; set; }
    public long LocalMmprojSizeBytes { get; set; }
    public bool HasRemoteMmproj { get; set; }
    public string? RemoteMmprojFileName { get; set; }
    public long RemoteMmprojSizeBytes { get; set; }
    public string? RemoteMmprojPath { get; set; }
    public string? RemoteRepository { get; set; }
    public List<ModelVariantFileOption> AvailableRemoteMmprojs { get; set; } = new();
    public string Status { get; set; } = "not_applicable"; // "ready" | "missing" | "not_applicable" | "downloading"
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 模型路径变更结果
/// </summary>
public class ModelPathChangeResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<string> Migrated { get; set; } = new();
    public List<ModelInfo> EmbeddingModels { get; set; } = new();
    public List<ModelInfo> RerankModels { get; set; } = new();
}

/// <summary>
/// 模型管理器接口
/// </summary>
public interface IModelManager
{
    Task<List<ModelInfo>> GetAvailableModelsAsync(string modelType = "");
    List<ModelInfo> GetDownloadedModels();
    ModelInfo? GetCurrentModel();
    Task<bool> SwitchModelAsync(string modelName);
    Task<(bool Success, string? Error)> DownloadModelAsync(string modelName, IProgress<float>? progress = null, string? targetFormat = null, string? onnxVariantPath = null, string? repository = null);
    Task<(bool Success, string? Error)> DownloadModelAsync(string modelName, IProgress<DownloadProgress>? progress, string? targetFormat = null, string? onnxVariantPath = null, CancellationToken cancellationToken = default, string? repository = null);
    Task<bool> DeleteModelAsync(string modelName);
    Task<List<ModelInfo>> GetRecommendedModelsAsync(string? language = null, bool preferGpu = true);
    void RefreshLocalModels();

    /// <summary>
    /// 切换同一模型的特定变体版本（如 Q4_K_M, Q8_0, ONNX 等）
    /// </summary>
    Task<bool> SwitchModelVariantAsync(string modelName, string variantFileName, bool isRerank = false);

    /// <summary>
    /// 删除同一模型的某个特定变体文件（若为最后一个变体则删除整个模型）
    /// </summary>
    Task<bool> DeleteModelVariantAsync(string modelName, string variantFileName, bool isRerank = false);

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
    Task<ModelPathChangeResult> SetModelsPathAsync(
        string newPath, bool migrateExisting = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取模型的下载选项
    /// </summary>
    Task<ModelDownloadOptions> GetDownloadOptionsAsync(string modelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// 搜索开源社区模型变种 (如 GGUF 量化版本、ONNX 变种等)
    /// </summary>
    Task<List<ModelVariantSearchResult>> SearchVariantsAsync(
        string query, string? format = null, int limit = 15, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取特定变种仓库的详细信息与文件清单
    /// </summary>
    Task<ModelVariantDetail?> GetVariantDetailAsync(
        string repositoryId, CancellationToken cancellationToken = default);

    // ========== 重排模型管理 ==========

    /// <summary>
    /// 获取当前重排模型
    /// </summary>
    ModelInfo? GetCurrentRerankModel();

    /// <summary>
    /// 切换重排模型
    /// </summary>
    Task<bool> SwitchRerankModelAsync(string modelName);

    /// <summary>
    /// 获取所有重排模型列表
    /// </summary>
    List<ModelInfo> GetRerankModels();

    /// <summary>
    /// 获取已下载的重排模型列表
    /// </summary>
    List<ModelInfo> GetDownloadedRerankModels();

    // ========== 聊天模型管理 ==========

    /// <summary>
    /// 获取当前聊天模型
    /// </summary>
    ModelInfo? GetCurrentChatModel();

    /// <summary>
    /// 切换聊天模型
    /// </summary>
    Task<bool> SwitchChatModelAsync(string modelName);

    /// <summary>
    /// 获取所有聊天模型列表
    /// </summary>
    List<ModelInfo> GetChatModels();

    /// <summary>
    /// 获取已下载的聊天模型列表
    /// </summary>
    List<ModelInfo> GetDownloadedChatModels();

    // ========== 模型变更事件 ==========

    /// <summary>
    /// 当模型路径变更时触发（需要外部服务重新加载模型）
    /// </summary>
    event EventHandler<ModelPathChangedEventArgs>? ModelPathChanged;

    /// <summary>
    /// 当 Embedding 模型切换时触发
    /// </summary>
    event EventHandler<ModelSwitchedEventArgs>? EmbeddingModelSwitched;

    /// <summary>
    /// 当 Rerank 模型切换时触发
    /// </summary>
    event EventHandler<ModelSwitchedEventArgs>? RerankModelSwitched;

    /// <summary>
    /// 当 Chat 模型切换时触发
    /// </summary>
    event EventHandler<ModelSwitchedEventArgs>? ChatModelSwitched;

    /// <summary>
    /// 查询模型视觉投影器（mmproj）的本地与远端就绪状态
    /// </summary>
    Task<ProjectorStatusResult> CheckProjectorStatusAsync(
        string modelName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 单独下载/补全模型的视觉投影器（mmproj）
    /// </summary>
    Task<(bool Success, string? Error)> DownloadProjectorAsync(
        string modelName,
        string? projectorPath = null,
        string? repository = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 模型路径变更事件参数
/// </summary>
public class ModelPathChangedEventArgs : EventArgs
{
    public string NewPath { get; set; } = string.Empty;
    public List<string> MigratedModels { get; set; } = new();
}

/// <summary>
/// 模型切换事件参数
/// </summary>
public class ModelSwitchedEventArgs : EventArgs
{
    public string ModelName { get; set; } = string.Empty;
    public string ModelPath { get; set; } = string.Empty;
}

/// <summary>
/// 模型管理器实现
/// </summary>
public class ModelManager : IModelManager, IDisposable
{
    private string _modelsPath;
    private readonly ConfigManager _configManager;
    private readonly Dictionary<string, ModelInfo> _modelRegistry = new();
    private ModelInfo? _currentModel;
    private ModelInfo? _currentRerankModel;
    private ModelInfo? _currentChatModel;
    private bool _disposed;

    // 事件声明
    public event EventHandler<ModelPathChangedEventArgs>? ModelPathChanged;
    public event EventHandler<ModelSwitchedEventArgs>? EmbeddingModelSwitched;
    public event EventHandler<ModelSwitchedEventArgs>? RerankModelSwitched;
    public event EventHandler<ModelSwitchedEventArgs>? ChatModelSwitched;

    // 预定义模型库 - 包含 ONNX 和 PyTorch 版本信息
    public static readonly IReadOnlyList<ModelInfo> PredefinedModels = new List<ModelInfo>
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
        new ModelInfo
        {
            Name = "gte-multilingual-base",
            DisplayName = "GTE Multilingual Base",
            Description = "阿里达摩院mGTE多语言多任务向量模型，70+语言，8K长上下文，支持稠密与稀疏混合检索",
            Dimension = 768,
            MaxSequenceLength = 8192,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "multilingual" },
            BenchmarkScore = 0.90f,
            DownloadUrl = "Alibaba-NLP/gte-multilingual-base",
            ModelSizeBytes = 600 * 1024 * 1024,
            HasOnnx = false
        },
        // ========== xVITA 繁体中文优化系列 ==========
        new ModelInfo
        {
            Name = "xVITA-Embed-300M-zhTW",
            DisplayName = "xVITA Embed 300M zh-TW",
            Description = "xCloudinfo繁体中文/台湾语境优化嵌入模型 (需HF授权Token/Gated)，基于EmbeddingGemma架构，8K上下文与非对称检索",
            Dimension = 768,
            MaxSequenceLength = 8192,
            ModelType = "embedding",
            Languages = new[] { "zh-TW", "zh", "en" },
            BenchmarkScore = 0.88f,
            DownloadUrl = "xCloudinfo/xVITA-Embed-300M-zhTW",
            ModelSizeBytes = 600 * 1024 * 1024,
            HasOnnx = false,
            AsymmetricEncoding = new AsymmetricEncodingConfig
            {
                QueryPrefix = "task: search result | query: ",
                DocumentPrefix = "title: none | text: "
            }
        },
        // ========== Qwen 系列（阿里通义）==========
        new ModelInfo
        {
            Name = "Qwen3-Embedding-0.6B",
            DisplayName = "Qwen3 Embedding 0.6B",
            Description = "阿里通义千问Qwen3系列0.6B轻量长文本向量模型，原生预编译GGUF量化，LLamaSharp直接免转加载，32K长上下文",
            Dimension = 1024,
            MaxSequenceLength = 32768,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "multilingual", "code" },
            BenchmarkScore = 0.93f,
            DownloadUrl = "Qwen/Qwen3-Embedding-0.6B-GGUF",
            ModelSizeBytes = (long)(450 * 1024 * 1024),
            HasOnnx = true,
            AsymmetricEncoding = new AsymmetricEncodingConfig
            {
                QueryPrefix = "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:",
                DocumentPrefix = ""
            }
        },
        new ModelInfo
        {
            Name = "Qwen3-Embedding-4B",
            DisplayName = "Qwen3 Embedding 4B",
            Description = "阿里通义千问Qwen3系列4B大参数高精度向量模型，原生预编译GGUF量化，LLamaSharp直接免转加载，32K长上下文",
            Dimension = 2560,
            MaxSequenceLength = 32768,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "multilingual", "code" },
            BenchmarkScore = 0.96f,
            DownloadUrl = "Qwen/Qwen3-Embedding-4B-GGUF",
            ModelSizeBytes = (long)(2.5 * 1024 * 1024 * 1024),
            HasOnnx = true,
            AsymmetricEncoding = new AsymmetricEncodingConfig
            {
                QueryPrefix = "Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:",
                DocumentPrefix = ""
            }
        },
        // ========== Jina 系列 ==========
        new ModelInfo
        {
            Name = "jina-embeddings-v3",
            DisplayName = "Jina Embeddings v3",
            Description = "Jina AI多语言嵌入模型，89+语言及代码，8K上下文，支持任务自适应LoRA与MRL弹性维度",
            Dimension = 1024,
            MaxSequenceLength = 8192,
            ModelType = "embedding",
            Languages = new[] { "zh", "en", "multilingual", "code" },
            BenchmarkScore = 0.94f,
            DownloadUrl = "jinaai/jina-embeddings-v3",
            ModelSizeBytes = (long)(1.1 * 1024 * 1024 * 1024),
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
        // ========== 重排模型 ==========
        new ModelInfo
        {
            Name = "bge-reranker-large",
            DisplayName = "BGE Reranker Large",
            Description = "BAAI大参数重排序模型，更高精度但需要更多资源",
            Dimension = 1024,
            MaxSequenceLength = 512,
            ModelType = "reranker",
            Languages = new[] { "zh", "en" },
            BenchmarkScore = 0.92f,
            DownloadUrl = "BAAI/bge-reranker-large",
            ModelSizeBytes = (long)(1.3 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "bge-reranker-v2-m3",
            DisplayName = "BGE Reranker v2 M3",
            Description = "BAAI多语言重排序模型v2，支持100+语言，MTEB高分",
            Dimension = 1024,
            MaxSequenceLength = 8192,
            ModelType = "reranker",
            Languages = new[] { "zh", "en", "multilingual" },
            BenchmarkScore = 0.93f,
            DownloadUrl = "BAAI/bge-reranker-v2-m3",
            ModelSizeBytes = (long)(2.2 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "bge-reranker-v2-gemma",
            DisplayName = "BGE Reranker v2 Gemma",
            Description = "基于Gemma的重排序模型，最高精度但需要大量资源",
            Dimension = 2560,
            MaxSequenceLength = 8192,
            ModelType = "reranker",
            Languages = new[] { "zh", "en", "multilingual" },
            BenchmarkScore = 0.95f,
            DownloadUrl = "BAAI/bge-reranker-v2-gemma",
            ModelSizeBytes = (long)(8.5 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "bge-reranker-v2-minicpm-layerwise",
            DisplayName = "BGE Reranker v2 MiniCPM",
            Description = "轻量级重排序模型，支持层级压缩，适合低资源环境",
            Dimension = 768,
            MaxSequenceLength = 2048,
            ModelType = "reranker",
            Languages = new[] { "zh", "en" },
            BenchmarkScore = 0.88f,
            DownloadUrl = "BAAI/bge-reranker-v2-minicpm-layerwise",
            ModelSizeBytes = (long)(1.8 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        // ========== 中英双语超轻量重排模型 ==========
        new ModelInfo
        {
            Name = "bce-reranker-base_v1",
            DisplayName = "BCE Reranker Base v1",
            Description = "网易有道中英日韩四语重排模型，MTEB得分超越bge-reranker，支持跨语言检索",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "reranker",
            Languages = new[] { "zh", "en", "ja", "ko" },
            BenchmarkScore = 0.91f,
            DownloadUrl = "maidalun1020/bce-reranker-base_v1",
            ModelSizeBytes = (long)(0.56 * 1024 * 1024 * 1024), // ~560MB
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "gte-multilingual-reranker-base",
            DisplayName = "GTE Multilingual Reranker Base",
            Description = "阿里GTE多语言重排模型，支持75+语言，SOTA性能，推理速度比LLM快10倍",
            Dimension = 768,
            MaxSequenceLength = 8192,
            ModelType = "reranker",
            Languages = new[] { "zh", "en", "multilingual" },
            BenchmarkScore = 0.90f,
            DownloadUrl = "Alibaba-NLP/gte-multilingual-reranker-base",
            ModelSizeBytes = (long)(0.6 * 1024 * 1024 * 1024), // ~600MB (306M params)
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "Qwen3-Reranker-0.6B",
            DisplayName = "Qwen3 Reranker 0.6B",
            Description = "阿里通义千问Qwen3系列0.6B轻量重排模型，100+语言及代码，原生32K超长上下文与深层推理能力",
            Dimension = 1024,
            MaxSequenceLength = 32768,
            ModelType = "reranker",
            Languages = new[] { "zh", "en", "multilingual", "code" },
            BenchmarkScore = 0.95f,
            DownloadUrl = "Qwen/Qwen3-Reranker-0.6B",
            ModelSizeBytes = (long)(1.2 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "Qwen3-Reranker-4B",
            DisplayName = "Qwen3 Reranker 4B",
            Description = "阿里通义千问Qwen3系列4B大参数高精度重排模型，100+语言及代码，原生32K超长上下文与旗舰级精排质量",
            Dimension = 2560,
            MaxSequenceLength = 32768,
            ModelType = "reranker",
            Languages = new[] { "zh", "en", "multilingual", "code" },
            BenchmarkScore = 0.97f,
            DownloadUrl = "Qwen/Qwen3-Reranker-4B",
            ModelSizeBytes = (long)(8.5 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "jina-reranker-v2-base-multilingual",
            DisplayName = "Jina Reranker v2 Base Multilingual",
            Description = "Jina AI多语言极速重排模型，278M轻量参数，支持滑动窗口长文本分块与FlashAttention加速",
            Dimension = 768,
            MaxSequenceLength = 1024,
            ModelType = "reranker",
            Languages = new[] { "zh", "en", "multilingual" },
            BenchmarkScore = 0.92f,
            DownloadUrl = "jinaai/jina-reranker-v2-base-multilingual",
            ModelSizeBytes = (long)(0.55 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "mxbai-rerank-base-v1",
            DisplayName = "mxbai Rerank Base v1",
            Description = "Mixedbread轻量高精度重排模型，内置官方ONNX，137M参数，低延迟高精度，边缘端与高并发RAG首选",
            Dimension = 768,
            MaxSequenceLength = 512,
            ModelType = "reranker",
            Languages = new[] { "en", "multilingual" },
            BenchmarkScore = 0.89f,
            DownloadUrl = "mixedbread-ai/mxbai-rerank-base-v1",
            ModelSizeBytes = (long)(0.55 * 1024 * 1024 * 1024),
            HasOnnx = true
        },
        new ModelInfo
        {
            Name = "mxbai-rerank-xsmall-v1",
            DisplayName = "mxbai Rerank xSmall v1",
            Description = "Mixedbread超轻量级重排模型，内置官方ONNX，仅110M微型体积，极致低延迟与资源占用",
            Dimension = 384,
            MaxSequenceLength = 512,
            ModelType = "reranker",
            Languages = new[] { "en", "multilingual" },
            BenchmarkScore = 0.85f,
            DownloadUrl = "mixedbread-ai/mxbai-rerank-xsmall-v1",
            ModelSizeBytes = (long)(0.22 * 1024 * 1024 * 1024),
            HasOnnx = true
        },
        new ModelInfo
        {
            Name = "ms-marco-MiniLM-L-6-v2",
            DisplayName = "MS MARCO MiniLM L6 v2",
            Description = "微软轻量级英文重排序模型，速度快资源占用低",
            Dimension = 384,
            MaxSequenceLength = 512,
            ModelType = "reranker",
            Languages = new[] { "en" },
            BenchmarkScore = 0.85f,
            DownloadUrl = "cross-encoder/ms-marco-MiniLM-L-6-v2",
            ModelSizeBytes = (long)(0.08 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "ms-marco-MiniLM-L-12-v2",
            DisplayName = "MS MARCO MiniLM L12 v2",
            Description = "微软中等规模英文重排序模型，平衡精度和速度",
            Dimension = 384,
            MaxSequenceLength = 512,
            ModelType = "reranker",
            Languages = new[] { "en" },
            BenchmarkScore = 0.88f,
            DownloadUrl = "cross-encoder/ms-marco-MiniLM-L-12-v2",
            ModelSizeBytes = (long)(0.12 * 1024 * 1024 * 1024),
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
        },
        // ========== 聊天大语言模型 (Chat) ==========
        new ModelInfo
        {
            Name = "Qwen2.5-1.5B-Instruct-GGUF",
            DisplayName = "Qwen 2.5 1.5B Instruct",
            Description = "通义千问 1.5B 轻量指令大模型（极速小钢炮），占用显存极小 (~1.5GB)，纯 CPU 或核显即可极速流畅运行",
            Dimension = 1536,
            MaxSequenceLength = 32768,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.88f,
            DownloadUrl = "Qwen/Qwen2.5-1.5B-Instruct-GGUF",
            ModelSizeBytes = (long)(1.0 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "Qwen2.5-7B-Instruct-GGUF",
            DisplayName = "Qwen 2.5 7B Instruct",
            Description = "通义千问 7B 满血指令模型（主流推荐），中文、代码与复杂推理能力极强，推荐 6GB~8GB+ 独显运行",
            Dimension = 3584,
            MaxSequenceLength = 32768,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.95f,
            DownloadUrl = "Qwen/Qwen2.5-7B-Instruct-GGUF",
            ModelSizeBytes = (long)(4.7 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "DeepSeek-R1-Distill-Qwen-1.5B-GGUF",
            DisplayName = "DeepSeek R1 Distill Qwen 1.5B",
            Description = "DeepSeek-R1 强化学习蒸馏模型（深度思维链），具备强大深度思考推理（CoT）能力，兼具小巧体积与高智商",
            Dimension = 1536,
            MaxSequenceLength = 32768,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.90f,
            DownloadUrl = "deepseek-ai/DeepSeek-R1-Distill-Qwen-1.5B-GGUF",
            ModelSizeBytes = (long)(1.1 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "Qwen2.5-3B-Instruct-GGUF",
            DisplayName = "Qwen 2.5 3B Instruct",
            Description = "通义千问 3B 指令模型（黄金甜点位），兼顾极小显存占用 (~2.5GB) 与高智能水平，纯 CPU 或 4GB+ 显存均可极佳运行",
            Dimension = 2048,
            MaxSequenceLength = 32768,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.91f,
            DownloadUrl = "Qwen/Qwen2.5-3B-Instruct-GGUF",
            ModelSizeBytes = (long)(2.1 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "DeepSeek-R1-Distill-Qwen-7B-GGUF",
            DisplayName = "DeepSeek R1 Distill Qwen 7B",
            Description = "DeepSeek-R1 强化学习蒸馏 7B 满血版（最强深度推理），拥有强大的长思维链（CoT）深度分析与复杂逻辑解题能力",
            Dimension = 3584,
            MaxSequenceLength = 32768,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.96f,
            DownloadUrl = "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B-GGUF",
            ModelSizeBytes = (long)(4.7 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "Qwen2.5-Coder-7B-Instruct-GGUF",
            DisplayName = "Qwen 2.5 Coder 7B Instruct",
            Description = "通义千问专业代码大模型（代码与技术专家），针对编程、算法、技术文档解析与知识库问答进行了深度强化",
            Dimension = 3584,
            MaxSequenceLength = 32768,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.95f,
            DownloadUrl = "Qwen/Qwen2.5-Coder-7B-Instruct-GGUF",
            ModelSizeBytes = (long)(4.7 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "Llama-3.2-3B-Instruct-GGUF",
            DisplayName = "Llama 3.2 3B Instruct",
            Description = "Meta 针对端侧设备推出的最新一代多语言模型（Meta 端侧旗舰），架构先进、响应飞快，英文与通用问答表现优异",
            Dimension = 3072,
            MaxSequenceLength = 131072,
            ModelType = "chat",
            Languages = new[] { "en", "multilingual", "code" },
            BenchmarkScore = 0.92f,
            DownloadUrl = "bartowski/Llama-3.2-3B-Instruct-GGUF",
            ModelSizeBytes = (long)(2.2 * 1024 * 1024 * 1024),
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "Phi-3.5-mini-instruct-onnx",
            DisplayName = "Phi-3.5-mini-instruct",
            Description = "微软专为 DirectML / ONNX Runtime 优化的 3.8B 参数轻量旗舰模型 (ONNX DirectML)，支持 128k 超长上下文，多语言与代码推理表现优异",
            Dimension = 3072,
            MaxSequenceLength = 131072,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.93f,
            DownloadUrl = "microsoft/Phi-3.5-mini-instruct-onnx",
            ModelSizeBytes = (long)(2.3 * 1024 * 1024 * 1024),
            HasOnnx = true
        },
        new ModelInfo
        {
            Name = "Qwen2.5-1.5B-Instruct-ONNX",
            DisplayName = "Qwen 2.5 1.5B Instruct ONNX",
            Description = "通义千问 1.5B ONNX 格式版本 (ONNX DirectML)，适配 Windows DirectML / NPU / GPU 加速，轻量高速端侧推理",
            Dimension = 1536,
            MaxSequenceLength = 32768,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.88f,
            DownloadUrl = "Qwen/Qwen2.5-1.5B-Instruct-ONNX",
            ModelSizeBytes = (long)(1.2 * 1024 * 1024 * 1024),
            HasOnnx = true
        },
        // ========== 2026 前沿轻量/视觉/高跑分端侧模型 ==========
        new ModelInfo
        {
            Name = "FunctionGemma-270M-IT",
            DisplayName = "FunctionGemma 270M IT",
            Description = "谷歌针对工具/函数调用专项优化的 270M 极简模型（超轻量工具路由），INT8 量化仅 288MB，BFCL 得分 61.6，专精函数路由与工具决策",
            Dimension = 1024,
            MaxSequenceLength = 32768,
            ModelType = "chat",
            Languages = new[] { "en", "code" },
            BenchmarkScore = 0.85f,
            DownloadUrl = "google/function-gemma-270m-it-gguf",
            ModelSizeBytes = 288L * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "LFM2.5-350M-Instruct-GGUF",
            DisplayName = "LFM2.5 350M Instruct",
            Description = "Liquid AI 350M 极致微型模型（极致轻量首选），Q4_K_M 仅 229MB，支持工具调用、JSON 结构化与文本提取，冷启动与内存零压力",
            Dimension = 1024,
            MaxSequenceLength = 32768,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.82f,
            DownloadUrl = "LiquidAI/LFM-2.5-350M-Instruct-GGUF",
            ModelSizeBytes = 229L * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "Qwen3.5-0.8B-Instruct-GGUF",
            DisplayName = "Qwen3.5 0.8B Instruct",
            Description = "通义千问 0.8B 紧凑全能模型（体积与通用平衡/视觉），Q4_0 仅 563MB，原生支持 262K 超长上下文，集成视觉感知、深度推理与代码工具能力",
            Dimension = 1024,
            MaxSequenceLength = 262144,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.86f,
            DownloadUrl = "Qwen/Qwen3.5-0.8B-Instruct-GGUF",
            ModelSizeBytes = 563L * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "LFM2.5-1.2B-Instruct-GGUF",
            DisplayName = "LFM2.5 1.2B Instruct",
            Description = "Liquid AI 1.2B 指令模型（离线RAG轻量专家），Q4_K_M 约 731MB，专精离线 RAG 知识库问答、JSON 与工具调用 (BFCL 49.12)",
            Dimension = 1536,
            MaxSequenceLength = 32768,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.88f,
            DownloadUrl = "LiquidAI/LFM-2.5-1.2B-Instruct-GGUF",
            ModelSizeBytes = 731L * 1024 * 1024,
            HasOnnx = false
        },
        new ModelInfo
        {
            Name = "Bonsai-2-2B-GGUF",
            DisplayName = "Bonsai 2 2B",
            Description = "高压缩比紧凑型视觉与代码推理模型（紧凑视觉与高智商推理），体积仅 480MB 左右，支持 262K 超大上下文与图像感知 (BFCL 60.53)",
            Dimension = 2048,
            MaxSequenceLength = 262144,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.90f,
            DownloadUrl = "Bonsai/Bonsai-2-2B-GGUF",
            ModelSizeBytes = 480L * 1024 * 1024,
            HasOnnx = false,
            IsMultimodal = true
        },
        new ModelInfo
        {
            Name = "Qwen3.5-2B-Instruct-GGUF",
            DisplayName = "Qwen3.5 2B Instruct",
            Description = "通义千问 2B 黄金规格模型（综合能力首选/视觉推荐），Q4_K_M 约 1.28GB，支持 262K 上下文、原生多模态视觉感知、高阶工具调用与代码生成",
            Dimension = 2048,
            MaxSequenceLength = 262144,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.92f,
            DownloadUrl = "Qwen/Qwen3.5-2B-Instruct-GGUF",
            ModelSizeBytes = (long)(1.28 * 1024 * 1024 * 1024),
            HasOnnx = false,
            IsMultimodal = true
        },
        new ModelInfo
        {
            Name = "Qwen3.5-4B-Instruct-GGUF",
            DisplayName = "Qwen3.5 4B Instruct",
            Description = "通义千问 4B 强劲多模态模型（多模态高阶主力/视觉），Q4_K_M 约 2.74GB，支持 262K 上下文、视觉/代码/工具深度协同 (BFCL 50.3)",
            Dimension = 2560,
            MaxSequenceLength = 262144,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.94f,
            DownloadUrl = "Qwen/Qwen3.5-4B-Instruct-GGUF",
            ModelSizeBytes = (long)(2.74 * 1024 * 1024 * 1024),
            HasOnnx = false,
            IsMultimodal = true
        },
        new ModelInfo
        {
            Name = "Gemma-4-E2B-IT-GGUF",
            DisplayName = "Gemma 4 E2B IT",
            Description = "谷歌 Gemma 4 视听一体多模态端侧模型（谷歌全能视听多模态），Q4_K_M 约 3.11GB，支持 128K 上下文、图像/音频输入与代码推理",
            Dimension = 2560,
            MaxSequenceLength = 131072,
            ModelType = "chat",
            Languages = new[] { "zh", "en", "code" },
            BenchmarkScore = 0.91f,
            DownloadUrl = "google/gemma-4-e2b-it-gguf",
            ModelSizeBytes = (long)(3.11 * 1024 * 1024 * 1024),
            HasOnnx = false,
            IsMultimodal = true
        }
    };

    public ModelManager(string modelsPath, ConfigManager configManager)
    {
        _modelsPath = Path.GetFullPath(modelsPath);
        _configManager = configManager;
        
        InitializeRegistry();
    }

    private void InitializeRegistry()
    {
        _modelRegistry.Clear();
        foreach (var model in PredefinedModels)
            _modelRegistry[model.Name] = model.Clone();

        // 先扫描目录，填充 IsDownloaded + LocalPath
        ScanLocalModels();

        // 再根据配置设置 _currentModel / _currentRerankModel（此时 LocalPath 已正确）
        CheckConfiguredModel();
    }

    /// <summary>
    /// 根据配置设置当前模型（在 ScanLocalModels 之后调用，LocalPath 已由扫描正确填充）
    /// </summary>
    private void CheckConfiguredModel()
    {
        try
        {
            var config = LoadConfigAsync().GetAwaiter().GetResult();

            if (!string.IsNullOrEmpty(config.Embedding?.ModelName) &&
                _modelRegistry.TryGetValue(config.Embedding.ModelName, out var embModel) &&
                embModel.IsDownloaded)
            {
                _currentModel = embModel;
                Console.WriteLine($"[ModelManager] 当前嵌入模型: {embModel.Name}, 路径: {embModel.LocalPath}");
            }

            if (!string.IsNullOrEmpty(config.Rerank?.CurrentModel) &&
                _modelRegistry.TryGetValue(config.Rerank.CurrentModel, out var rerankModel) &&
                rerankModel.IsDownloaded)
            {
                _currentRerankModel = rerankModel;
                Console.WriteLine($"[ModelManager] 当前重排模型: {rerankModel.Name}, 路径: {rerankModel.LocalPath}");
            }

            var chatModelName = config.Chat?.Model ?? config.Chat?.LocalGguf?.ModelName;
            if (!string.IsNullOrEmpty(chatModelName) &&
                _modelRegistry.TryGetValue(chatModelName, out var chatModel) &&
                chatModel.IsDownloaded)
            {
                _currentChatModel = chatModel;
                Console.WriteLine($"[ModelManager] 当前聊天模型: {chatModel.Name}, 路径: {chatModel.LocalPath}");
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

    private static readonly System.Text.RegularExpressions.Regex QuantRegex = new(
        @"(Q\d+_[A-Z0-9_]+|q\d+_[a-z0-9_]+|int8|int4|fp16|fp32|f16|f32)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 从文件名中解析量化规格标签（如 Q4_K_M, Q8_0, INT8, FP16）
    /// </summary>
    public static string? ExtractQuantizationFromFileName(string fileName)
    {
        var match = QuantRegex.Match(fileName);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// 扫描指定模型目录下的所有变体版本文件（GGUF 及 ONNX），构建已安装变体集合
    /// </summary>
    private List<ModelVariantInfo> ScanDirectoryVariants(string dir, string modelType, string? currentConfiguredPath)
    {
        var variants = new List<ModelVariantInfo>();
        if (!Directory.Exists(dir)) return variants;

        // 1. 扫描所有 GGUF 变体文件（排除伴生视觉投影器作为独立对话模型）
        var allGgufFiles = Directory.GetFiles(dir, "*.gguf", SearchOption.AllDirectories);
        var mmprojFile = allGgufFiles.FirstOrDefault(f => GgufMultimodalCompanionStrategy.IsMmproj(Path.GetFileName(f)));
        var hasMmproj = mmprojFile != null;
        var ggufFiles = allGgufFiles.Where(f => !GgufMultimodalCompanionStrategy.IsMmproj(Path.GetFileName(f))).ToArray();
        if (ggufFiles.Length == 0 && allGgufFiles.Length > 0)
        {
            ggufFiles = allGgufFiles;
        }

        string? mmprojName = hasMmproj ? Path.GetFileName(mmprojFile) : null;
        long mmprojSize = hasMmproj ? new FileInfo(mmprojFile!).Length : 0;

        foreach (var file in ggufFiles)
        {
            var fileName = Path.GetFileName(file);
            var fi = new FileInfo(file);
            var quant = ExtractQuantizationFromFileName(fileName);
            var varId = !string.IsNullOrEmpty(quant) ? quant : Path.GetFileNameWithoutExtension(fileName);
            var displayName = $"GGUF {(!string.IsNullOrEmpty(quant) ? quant : Path.GetFileNameWithoutExtension(fileName))}";
            if (hasMmproj && !GgufMultimodalCompanionStrategy.IsMmproj(fileName))
            {
                displayName += " (含视觉模型)";
            }

            var isMulti = hasMmproj || GgufMultimodalCompanionStrategy.IsLikelyVisionModel(fileName);

            variants.Add(new ModelVariantInfo
            {
                VariantId = varId,
                DisplayName = displayName,
                FileName = fileName,
                RelativePath = Path.GetRelativePath(dir, file),
                FullPath = Path.GetFullPath(file),
                Format = "gguf",
                Quantization = quant,
                FileSizeBytes = fi.Length,
                IsDownloaded = true,
                IsMultimodal = isMulti,
                CompanionMmprojFileName = mmprojName,
                CompanionMmprojSizeBytes = mmprojSize,
                HasMmprojLocally = hasMmproj
            });
        }

        // 2. 扫描所有 ONNX 变体文件
        if (string.Equals(modelType, "chat", StringComparison.OrdinalIgnoreCase))
        {
            var ogaConfigs = Directory.GetFiles(dir, "genai_config.json", SearchOption.AllDirectories);
            if (ogaConfigs.Length > 0)
            {
                foreach (var configFile in ogaConfigs)
                {
                    var ogaSubDir = Path.GetDirectoryName(configFile)!;
                    var relDir = Path.GetRelativePath(dir, ogaSubDir);
                    var variantName = relDir == "." ? Path.GetFileName(dir) : relDir.Replace('\\', '/');
                    var dirFiles = Directory.GetFiles(ogaSubDir, "*", SearchOption.AllDirectories);
                    var dirSize = dirFiles.Sum(f => new FileInfo(f).Length);
                    var quant = ExtractQuantizationFromFileName(variantName) ?? (variantName.Contains("gpu", StringComparison.OrdinalIgnoreCase) ? "DirectML" : "CPU");

                    variants.Add(new ModelVariantInfo
                    {
                        VariantId = variantName,
                        DisplayName = $"ONNX {variantName}",
                        FileName = relDir == "." ? "genai_config.json" : relDir,
                        RelativePath = relDir,
                        FullPath = ogaSubDir,
                        Format = "onnx",
                        Quantization = quant,
                        FileSizeBytes = dirSize,
                        IsDownloaded = true
                    });
                }
            }
        }

        if (variants.Count == 0 || !string.Equals(modelType, "chat", StringComparison.OrdinalIgnoreCase))
        {
            var onnxFiles = Directory.GetFiles(dir, "*.onnx", SearchOption.AllDirectories);
            foreach (var file in onnxFiles)
            {
                var fileName = Path.GetFileName(file);
                var fi = new FileInfo(file);
                var onnxFormat = DetectOnnxFormat(dir);
                var quant = ExtractQuantizationFromFileName(fileName);

                string varId;
                string displayName;
                if (string.Equals(fileName, "model.onnx", StringComparison.OrdinalIgnoreCase))
                {
                    varId = onnxFormat == "external" ? "onnx-external" : "onnx-embedded";
                    displayName = onnxFormat == "external" ? "ONNX 外部数据" : "ONNX 嵌入式";
                }
                else
                {
                    varId = Path.GetFileNameWithoutExtension(fileName);
                    displayName = $"ONNX {varId}";
                }

                variants.Add(new ModelVariantInfo
                {
                    VariantId = varId,
                    DisplayName = displayName,
                    FileName = fileName,
                    RelativePath = Path.GetRelativePath(dir, file),
                    FullPath = Path.GetFullPath(file),
                    Format = "onnx",
                    Quantization = quant,
                    FileSizeBytes = fi.Length,
                    IsDownloaded = true
                });
            }
        }

        // 3. 匹配当前激活状态
        var matched = false;
        if (!string.IsNullOrEmpty(currentConfiguredPath))
        {
            var normConfigPath = Path.GetFullPath(currentConfiguredPath);
            foreach (var v in variants)
            {
                if (string.Equals(normConfigPath, v.FullPath, StringComparison.OrdinalIgnoreCase) ||
                    normConfigPath.EndsWith(v.FileName, StringComparison.OrdinalIgnoreCase) ||
                    normConfigPath.Contains(v.VariantId, StringComparison.OrdinalIgnoreCase))
                {
                    v.IsActive = true;
                    matched = true;
                    break;
                }
            }
        }

        if (!matched && variants.Count > 0)
        {
            var preferred = variants.FirstOrDefault(v => v.Format == "onnx" && (v.VariantId.Contains("gpu", StringComparison.OrdinalIgnoreCase) || v.VariantId.Contains("directml", StringComparison.OrdinalIgnoreCase)))
                ?? (string.Equals(modelType, "reranker", StringComparison.OrdinalIgnoreCase) ? variants.FirstOrDefault(v => v.Format == "onnx") : null)
                ?? variants[0];
            preferred.IsActive = true;
        }

        return variants;
    }

    private void ScanLocalModels()
    {
        if (!Directory.Exists(_modelsPath))
        {
            Directory.CreateDirectory(_modelsPath);
        }

        var config = _configManager.Load();
        var configuredEmbPath = config.Embedding?.ModelPath;
        var configuredRerankPath = config.Rerank?.ModelPath;
        var configuredChatPath = !string.IsNullOrEmpty(config.Chat?.LocalOnnxGenAi?.ModelPath)
            ? config.Chat.LocalOnnxGenAi.ModelPath
            : config.Chat?.LocalGguf?.ModelPath;

        // 首先将所有模型的 IsDownloaded 重置为 false（路径变更后需要重新检测）
        foreach (var model in _modelRegistry.Values)
        {
            model.IsDownloaded = false;
            model.LocalPath = null;
            model.InstalledVariants.Clear();
            model.ActiveVariantFileName = null;
        }

        // 扫描子目录结构（新结构）
        var embeddingPath = Path.Combine(_modelsPath, "Embedding");
        if (Directory.Exists(embeddingPath))
            ScanModelDirectory(embeddingPath, "embedding", configuredEmbPath);

        var rerankerPath = Path.Combine(_modelsPath, "Reranker");
        if (Directory.Exists(rerankerPath))
            ScanModelDirectory(rerankerPath, "reranker", configuredRerankPath);

        var chatPath = Path.Combine(_modelsPath, "Chat");
        if (Directory.Exists(chatPath))
            ScanModelDirectory(chatPath, "chat", configuredChatPath);

        // 兼容扫描：扁平结构（旧版下载直接放在 _modelsPath/modelName/）
        // 仅处理尚未被子目录扫描命中的已注册模型
        foreach (var model in _modelRegistry.Values.Where(m => !m.IsDownloaded))
        {
            var flatDir = Path.Combine(_modelsPath, model.Name);
            if (Directory.Exists(flatDir))
            {
                var targetConfigPath = model.ModelType == "reranker" ? configuredRerankPath : (model.ModelType == "chat" ? configuredChatPath : configuredEmbPath);
                ScanModelDirectory(_modelsPath, model.ModelType, targetConfigPath, new[] { model.Name });
            }
        }
    }

    /// <summary>
    /// 扫描指定目录下的模型，提取多变体版本
    /// </summary>
    private void ScanModelDirectory(string basePath, string modelType, string? currentConfiguredPath = null, string[]? specificDirs = null)
    {
        var dirsToScan = specificDirs ?? Directory.GetDirectories(basePath).Select(d => Path.GetFileName(d));

        foreach (var modelName in dirsToScan)
        {
            var dir = specificDirs != null ? Path.Combine(basePath, modelName) : Path.Combine(basePath, modelName);
            if (!Directory.Exists(dir))
                continue;

            var variants = ScanDirectoryVariants(dir, modelType, currentConfiguredPath);

            if (variants.Count > 0)
            {
                var directorySize = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                    .Sum(f => new FileInfo(f).Length);
                var activeVariant = variants.FirstOrDefault(v => v.IsActive) ?? variants[0];
                var onnxFormat = activeVariant.Format == "gguf" ? "gguf" : DetectOnnxFormat(dir);
                var canConvert = onnxFormat != "gguf" && directorySize < 2L * 1024 * 1024 * 1024;
                var hasValidOnnx = onnxFormat != "invalid";

                var allGgufInDir = Directory.GetFiles(dir, "*.gguf", SearchOption.AllDirectories);
                var localMmprojPath = allGgufInDir.FirstOrDefault(f => GgufMultimodalCompanionStrategy.IsMmproj(Path.GetFileName(f)));
                bool hasLocalMmproj = localMmprojPath != null;
                string? localMmprojName = hasLocalMmproj ? Path.GetFileName(localMmprojPath) : null;
                long localMmprojSize = hasLocalMmproj ? new FileInfo(localMmprojPath!).Length : 0;

                if (_modelRegistry.TryGetValue(modelName, out var model))
                {
                    model.IsDownloaded = hasValidOnnx;
                    model.LocalPath = dir;
                    model.ModelSizeBytes = directorySize;
                    model.OnnxFormat = onnxFormat;
                    model.HasOnnx = onnxFormat != "gguf" && hasValidOnnx;
                    model.CanConvertFormat = canConvert;
                    model.InstalledVariants = variants;
                    model.ActiveVariantFileName = activeVariant.FileName;

                    var isMulti = model.IsMultimodal || hasLocalMmproj || GgufMultimodalCompanionStrategy.IsLikelyVisionModel(modelName);
                    model.IsMultimodal = isMulti;
                    model.HasMmprojLocally = hasLocalMmproj;
                    model.LocalMmprojFileName = localMmprojName;
                    model.LocalMmprojSizeBytes = localMmprojSize;
                    model.ProjectorStatus = isMulti ? (hasLocalMmproj ? "ready" : "missing") : "not_applicable";
                }
                else
                {
                    var dimension = DetectModelDimension(dir);
                    var isMulti = hasLocalMmproj || GgufMultimodalCompanionStrategy.IsLikelyVisionModel(modelName);
                    _modelRegistry[modelName] = new ModelInfo
                    {
                        Name = modelName,
                        DisplayName = modelName,
                        Description = activeVariant.Format == "gguf" ? "本地 GGUF 模型" : "本地模型",
                        Dimension = dimension > 0 ? dimension : 0,
                        ModelType = modelType,
                        IsDownloaded = hasValidOnnx,
                        LocalPath = dir,
                        ModelSizeBytes = directorySize,
                        HasOnnx = onnxFormat != "gguf" && hasValidOnnx,
                        OnnxFormat = onnxFormat,
                        CanConvertFormat = canConvert,
                        InstalledVariants = variants,
                        ActiveVariantFileName = activeVariant.FileName,
                        IsMultimodal = isMulti,
                        HasMmprojLocally = hasLocalMmproj,
                        LocalMmprojFileName = localMmprojName,
                        LocalMmprojSizeBytes = localMmprojSize,
                        ProjectorStatus = isMulti ? (hasLocalMmproj ? "ready" : "missing") : "not_applicable"
                    };
                }
            }
        }

        // 兼容扫描：根目录下直接存放的独立 .gguf 模型文件
        if (specificDirs == null && Directory.Exists(basePath))
        {
            var looseGgufFiles = Directory.GetFiles(basePath, "*.gguf");
            foreach (var ggufFile in looseGgufFiles)
            {
                var looseModelName = Path.GetFileNameWithoutExtension(ggufFile);
                if (ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Strategies.GgufMultimodalCompanionStrategy.IsMmproj(looseModelName)) continue;
                if (_modelRegistry.ContainsKey(looseModelName)) continue;

                var fileInfo = new FileInfo(ggufFile);
                var quant = ExtractQuantizationFromFileName(looseModelName);
                var singleVariant = new ModelVariantInfo
                {
                    VariantId = !string.IsNullOrEmpty(quant) ? quant : looseModelName,
                    DisplayName = $"GGUF {(!string.IsNullOrEmpty(quant) ? quant : looseModelName)}",
                    FileName = Path.GetFileName(ggufFile),
                    RelativePath = Path.GetFileName(ggufFile),
                    FullPath = Path.GetFullPath(ggufFile),
                    Format = "gguf",
                    Quantization = quant,
                    FileSizeBytes = fileInfo.Length,
                    IsDownloaded = true,
                    IsActive = true
                };

                _modelRegistry[looseModelName] = new ModelInfo
                {
                    Name = looseModelName,
                    DisplayName = looseModelName,
                    Description = "本地 GGUF 模型文件",
                    Dimension = 0,
                    ModelType = modelType,
                    IsDownloaded = true,
                    LocalPath = ggufFile,
                    ModelSizeBytes = fileInfo.Length,
                    HasOnnx = false,
                    OnnxFormat = "gguf",
                    CanConvertFormat = false,
                    InstalledVariants = new List<ModelVariantInfo> { singleVariant },
                    ActiveVariantFileName = singleVariant.FileName
                };
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

    public Task<List<ModelInfo>> GetAvailableModelsAsync(string modeltype = "")
    {
        if (string.IsNullOrWhiteSpace(modeltype))
        {
            return Task.FromResult(_modelRegistry.Values.ToList());
        }

        return Task.FromResult(_modelRegistry.Values.Where(m => m.ModelType == modeltype).ToList());
    }

    public List<ModelInfo> GetDownloadedModels()
    {
        return _modelRegistry.Values.Where(m => m.IsDownloaded).ToList();
    }

    public ModelInfo? GetCurrentModel()
    {
        if (_currentModel != null) return _currentModel;

        var cfg = _configManager.Load();
        if (cfg.Embedding?.Mode == "openai" && !string.IsNullOrEmpty(cfg.Embedding.ModelName))
        {
            return new ModelInfo
            {
                Name = cfg.Embedding.ModelName,
                DisplayName = $"{cfg.Embedding.ModelName} (API)",
                Dimension = cfg.Embedding.ApiDimension ?? 0,
                ModelType = "embedding",
                IsDownloaded = true
            };
        }

        return null;
    }

    /// <summary>
    /// 解析模型所在物理文件路径（自动识别 .gguf 或 .onnx 文件，优先匹配指定的激活变体文件名）
    /// </summary>
    public static string ResolveModelFilePath(string? localPath, string? preferredVariantFileName = null)
    {
        if (string.IsNullOrEmpty(localPath)) return string.Empty;
        if (File.Exists(localPath)) return localPath;

        if (Directory.Exists(localPath))
        {
            var isChat = localPath.Contains("Chat", StringComparison.OrdinalIgnoreCase);
            if (isChat)
            {
                var ogaDir = OnnxGenAiPathResolver.ResolveModelDirectory(localPath);
                if (!string.IsNullOrEmpty(ogaDir) && OnnxGenAiPathResolver.IsValidOgaDirectory(ogaDir))
                {
                    if (!string.IsNullOrEmpty(preferredVariantFileName) && !preferredVariantFileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                    {
                        var targetVariantDir = Path.Combine(localPath, preferredVariantFileName);
                        if (Directory.Exists(targetVariantDir) && File.Exists(Path.Combine(targetVariantDir, "genai_config.json")))
                        {
                            return targetVariantDir;
                        }
                    }
                    return ogaDir;
                }
            }

            if (!string.IsNullOrEmpty(preferredVariantFileName))
            {
                var targetFile = Path.Combine(localPath, preferredVariantFileName);
                if (File.Exists(targetFile)) return targetFile;

                var subMatches = Directory.GetFiles(localPath, preferredVariantFileName, SearchOption.AllDirectories);
                if (subMatches.Length > 0) return subMatches[0];
            }

            var isReranker = localPath.Contains("Reranker", StringComparison.OrdinalIgnoreCase);
            var onnxPath = Path.Combine(localPath, "model.onnx");

            // 对于 Reranker 目录，若存在官方标准 model.onnx 则优先采用 ONNX（Cross-Encoder 最优运行时）
            if (isReranker && File.Exists(onnxPath)) return onnxPath;

            var ggufFiles = Directory.GetFiles(localPath, "*.gguf", SearchOption.AllDirectories);
            if (ggufFiles.Length > 0 && !isReranker) return ggufFiles[0];

            if (File.Exists(onnxPath)) return onnxPath;

            if (ggufFiles.Length > 0) return ggufFiles[0];

            var subOnnxFiles = Directory.GetFiles(localPath, "model.onnx", SearchOption.AllDirectories);
            if (subOnnxFiles.Length > 0) return subOnnxFiles[0];

            var anyOnnxFiles = Directory.GetFiles(localPath, "*.onnx", SearchOption.AllDirectories);
            if (anyOnnxFiles.Length > 0) return anyOnnxFiles[0];
        }

        return Path.Combine(localPath, "model.onnx");
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
            var resolvedPath = ResolveModelFilePath(model.LocalPath, model.ActiveVariantFileName);
            config.Embedding.ModelPath = resolvedPath;
            config.Embedding.ModelName = modelName;
            config.Embedding.MaxSequenceLength = model.MaxSequenceLength;
            await SaveConfigAsync(config);

            _currentModel = model;
            Console.WriteLine($"[ModelManager] 已切换到模型: {model.DisplayName} (Path={resolvedPath}, MaxSeqLen={model.MaxSequenceLength})");

            // 触发模型切换事件，通知外部服务重新加载
            EmbeddingModelSwitched?.Invoke(this, new ModelSwitchedEventArgs
            {
                ModelName = modelName,
                ModelPath = resolvedPath
            });

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 切换模型失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 切换同一模型的特定变体版本（如 Q4_K_M, Q8_0, ONNX 等）
    /// </summary>
    public async Task<bool> SwitchModelVariantAsync(string modelName, string variantFileName, bool isRerank = false)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            Console.WriteLine($"[ModelManager] 模型不存在: {modelName}");
            return false;
        }

        if (!model.IsDownloaded || model.InstalledVariants.Count == 0)
        {
            Console.WriteLine($"[ModelManager] 模型未下载或无变体: {modelName}");
            return false;
        }

        var variant = model.InstalledVariants.FirstOrDefault(v =>
            string.Equals(v.FileName, variantFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v.VariantId, variantFileName, StringComparison.OrdinalIgnoreCase));

        if (variant == null)
        {
            Console.WriteLine($"[ModelManager] 未找到变体: {variantFileName} 于模型 {modelName}");
            return false;
        }

        try
        {
            var config = await LoadConfigAsync();
            if (model.ModelType == "chat")
            {
                var isGguf = variant.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || variant.Format == "gguf";
                var isOnnx = variant.FileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) || variant.Format == "onnx" ||
                             variant.FileName.EndsWith("genai_config.json", StringComparison.OrdinalIgnoreCase);

                if (!isGguf && !isOnnx)
                {
                    Console.WriteLine($"[ModelManager] 切换聊天模型变体失败：当前仅支持 GGUF 与 ONNX 格式聊天模型，不支持 {variant.FileName}");
                    return false;
                }

                if (config.Chat == null) config.Chat = new ChatModelConfig();
                config.Chat.Model = modelName;

                if (isOnnx)
                {
                    config.Chat.ActiveBackend = "LocalOnnxGenAi";
                    if (config.Chat.LocalOnnxGenAi == null) config.Chat.LocalOnnxGenAi = new LocalOnnxGenAiChatProfile();
                    config.Chat.LocalOnnxGenAi.ModelName = modelName;
                    config.Chat.LocalOnnxGenAi.ModelPath = variant.FullPath;
                }
                else
                {
                    config.Chat.ActiveBackend = "LocalGguf";
                    if (config.Chat.LocalGguf == null) config.Chat.LocalGguf = new LocalGgufChatProfile();
                    config.Chat.LocalGguf.ModelName = modelName;
                    config.Chat.LocalGguf.Variant = variant.FileName;
                    config.Chat.LocalGguf.ModelPath = variant.FullPath;
                    if (model.MaxSequenceLength > 0)
                    {
                        config.Chat.LocalGguf.ContextSize = model.MaxSequenceLength;
                    }
                }
                _currentChatModel = model;
            }
            else if (isRerank || model.ModelType == "reranker")
            {
                if (config.Rerank == null) config.Rerank = new RerankConfig();
                config.Rerank.CurrentModel = modelName;
                config.Rerank.ModelPath = variant.FullPath;
                _currentRerankModel = model;
            }
            else
            {
                if (config.Embedding == null) config.Embedding = new EmbeddingConfig();
                config.Embedding.ModelName = modelName;
                config.Embedding.ModelPath = variant.FullPath;
                config.Embedding.MaxSequenceLength = model.MaxSequenceLength;
                _currentModel = model;
            }
            await SaveConfigAsync(config);

            // 更新变体激活状态
            foreach (var v in model.InstalledVariants)
            {
                v.IsActive = (v == variant);
            }
            model.ActiveVariantFileName = variant.FileName;
            model.OnnxFormat = variant.Format == "gguf" ? "gguf" : DetectOnnxFormat(model.LocalPath ?? "");

            Console.WriteLine($"[ModelManager] 成功切换模型变体: {modelName} -> {variant.FileName} (Path={variant.FullPath})");

            if (model.ModelType == "chat")
            {
                ChatModelSwitched?.Invoke(this, new ModelSwitchedEventArgs
                {
                    ModelName = modelName,
                    ModelPath = variant.FullPath
                });
            }
            else if (isRerank || model.ModelType == "reranker")
            {
                RerankModelSwitched?.Invoke(this, new ModelSwitchedEventArgs
                {
                    ModelName = modelName,
                    ModelPath = variant.FullPath
                });
            }
            else
            {
                EmbeddingModelSwitched?.Invoke(this, new ModelSwitchedEventArgs
                {
                    ModelName = modelName,
                    ModelPath = variant.FullPath
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 切换模型变体异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 删除同一模型的某个特定变体文件（若为最后一个变体则删除整个模型）
    /// </summary>
    public async Task<bool> DeleteModelVariantAsync(string modelName, string variantFileName, bool isRerank = false)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
            return false;

        if (!model.IsDownloaded || model.InstalledVariants.Count == 0)
            return false;

        var variant = model.InstalledVariants.FirstOrDefault(v =>
            string.Equals(v.FileName, variantFileName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(v.VariantId, variantFileName, StringComparison.OrdinalIgnoreCase));

        if (variant == null)
            return false;

        // 如果是仅有的一个变体，则直接删除整个模型
        if (model.InstalledVariants.Count <= 1)
        {
            return await DeleteModelAsync(modelName);
        }

        try
        {
            if (File.Exists(variant.FullPath))
            {
                File.Delete(variant.FullPath);
                var dataFile = variant.FullPath + "_data";
                if (File.Exists(dataFile)) File.Delete(dataFile);
            }

            model.InstalledVariants.Remove(variant);

            // 如果删除的是当前激活变体，自动切到第一个剩余变体
            if (variant.IsActive && model.InstalledVariants.Count > 0)
            {
                var nextActive = model.InstalledVariants[0];
                await SwitchModelVariantAsync(modelName, nextActive.FileName, isRerank);
            }

            model.ModelSizeBytes = model.InstalledVariants.Sum(v => v.FileSizeBytes);
            Console.WriteLine($"[ModelManager] 已删除变体: {modelName}/{variant.FileName}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 删除变体失败: {ex.Message}");
            return false;
        }
    }

    public Task<(bool Success, string? Error)> DownloadModelAsync(
        string modelName, 
        IProgress<float>? progress = null, 
        string? targetFormat = null, 
        string? onnxVariantPath = null,
        string? repository = null)
    {
        var wrapper = progress != null ? new Progress<DownloadProgress>(p => progress.Report(p.Progress)) : null;
        return DownloadModelAsync(modelName, wrapper, targetFormat, onnxVariantPath, default, repository);
    }

    private IModelSourceAdapter CreateModelSourceAdapter()
    {
        var hub = _configManager.Load().ModelHub;
        var token = !string.IsNullOrWhiteSpace(hub?.HfToken)
            ? hub.HfToken
            : Environment.GetEnvironmentVariable("HF_TOKEN");
        var endpoint = hub?.HfEndpoint;
        return new HuggingFaceSourceAdapter(token, endpoint);
    }

    public async Task<(bool Success, string? Error)> DownloadModelAsync(
        string modelName,
        IProgress<DownloadProgress>? progress,
        string? targetFormat = null,
        string? onnxVariantPath = null,
        CancellationToken cancellationToken = default,
        string? repository = null)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            Console.WriteLine($"[ModelManager] 模型不存在: {modelName}");
            return (false, $"模型不存在: {modelName}");
        }

        if (model.IsDownloaded)
        {
            // 如果用户指定了具体变体，检查该具体变体是否已存在
            // 如果未指定具体变体，或者具体变体已存在，才跳过下载；否则允许增量下载新变体！
            bool variantAlreadyExists = false;
            if (!string.IsNullOrEmpty(onnxVariantPath))
            {
                var fileName = Path.GetFileName(onnxVariantPath);
                variantAlreadyExists = model.InstalledVariants.Any(v =>
                    string.Equals(v.FileName, fileName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(v.RelativePath, onnxVariantPath, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                variantAlreadyExists = true;
            }

            if (variantAlreadyExists)
            {
                Console.WriteLine($"[ModelManager] 模型变体已存在: {modelName} ({onnxVariantPath})");
                return (true, null);
            }
        }

        // 新下载写入子目录（Embedding/、Reranker/ 或 Chat/），与 ScanLocalModels 保持一致
        var subDir = model.ModelType switch
        {
            "reranker" => "Reranker",
            "chat" => "Chat",
            _ => "Embedding"
        };
        var modelDir = Path.Combine(_modelsPath, subDir, modelName);

        try
        {
            Console.WriteLine($"[ModelManager] 开始下载模型: {model.DisplayName}");
            Directory.CreateDirectory(modelDir);

            var adapter = CreateModelSourceAdapter();
            var coordinator = new ModelDownloadCoordinator(defaultAdapterFactory: () => CreateModelSourceAdapter());
            var stateMachine = new DownloadStateMachine(modelName, model.ModelSizeBytes, progress);

            await coordinator.DownloadModelAsync(
                model.DownloadUrl ?? $"BAAI/{modelName}",
                modelDir,
                stateMachine,
                adapter,
                targetFormat,
                onnxVariantPath,
                cancellationToken,
                repository
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
                    var srcData = Path.Combine(srcDir, "model.onnx_data");
                    if (File.Exists(srcData))
                    {
                        File.Copy(srcData, Path.Combine(modelDir, "model.onnx_data"), true);
                    }
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
                    // 检查是否为 GGUF 模型
                    var ggufFiles = Directory.GetFiles(modelDir, "*.gguf", SearchOption.AllDirectories);
                    if (ggufFiles.Length > 0)
                    {
                        var mainGguf = ggufFiles[0];
                        Console.WriteLine($"[ModelManager] 检测到下载的 GGUF 模型: {mainGguf}");
                        model.IsDownloaded = true;
                        model.LocalPath = modelDir;
                        model.ModelSizeBytes = new FileInfo(mainGguf).Length;
                        model.Dimension = DetectModelDimension(modelDir);
                        model.OnnxFormat = "gguf";
                        model.HasOnnx = false;
                        model.CanConvertFormat = false;

                        Console.WriteLine($"[ModelManager] GGUF 模型下载完成: {model.DisplayName}");
                        ScanLocalModels();
                        return (true, null);
                    }

                    throw new InvalidOperationException("模型文件下载不完整，缺少 model.onnx 或 .gguf 文件");
                }
            }

            model.IsDownloaded = true;
            model.LocalPath = modelDir;
            model.ModelSizeBytes = new FileInfo(onnxPath).Length;
            model.Dimension = DetectModelDimension(modelDir);
            model.OnnxFormat = DetectOnnxFormat(modelDir);
            model.HasOnnx = model.OnnxFormat != "invalid";
            model.CanConvertFormat = model.ModelSizeBytes < 2L * 1024 * 1024 * 1024;

            Console.WriteLine($"[ModelManager] 模型下载完成: {model.DisplayName}");
            ScanLocalModels();
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[ModelManager] 用户取消了模型下载: {model.DisplayName}");
            CleanupIncompleteDownload(modelDir, onnxVariantPath);
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 模型下载失败: {ex.Message}");
            CleanupIncompleteDownload(modelDir, onnxVariantPath);
            return (false, ex.Message);
        }
    }

    private void CleanupIncompleteDownload(string modelDir, string? onnxVariantPath)
    {
        try
        {
            if (!Directory.Exists(modelDir)) return;

            // 1. 清理临时下载文件碎片 (*.tmp_*)
            foreach (var tmpFile in Directory.GetFiles(modelDir, "*.tmp_*", SearchOption.AllDirectories))
            {
                try { File.Delete(tmpFile); } catch { }
            }

            // 2. 如果指定了具体变体，清理该变体对应的未完成目标文件
            if (!string.IsNullOrEmpty(onnxVariantPath))
            {
                var targetFile = Path.Combine(modelDir, Path.GetFileName(onnxVariantPath));
                if (File.Exists(targetFile))
                {
                    try { File.Delete(targetFile); } catch { }
                }
            }

            // 3. 检查是否保留有其他已成功安装的有效模型文件（.gguf / .onnx）
            var hasOtherVariants = Directory.EnumerateFiles(modelDir, "*.*", SearchOption.AllDirectories)
                .Any(f => f.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
                          f.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));

            // 4. 若没有任何有效模型文件，说明是首次下载失败，安全清理整个目录
            if (!hasOtherVariants)
            {
                Directory.Delete(modelDir, true);
            }
        }
        catch { }
    }

    /// <summary>
    /// 查询模型视觉投影器（mmproj）的本地与远端就绪状态
    /// </summary>
    public async Task<ProjectorStatusResult> CheckProjectorStatusAsync(
        string modelName,
        CancellationToken cancellationToken = default)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            return new ProjectorStatusResult
            {
                ModelName = modelName,
                Status = "not_applicable",
                Message = $"未找到指定模型: {modelName}"
            };
        }

        var isMulti = model.IsMultimodal ||
                      GgufMultimodalCompanionStrategy.IsLikelyVisionModel(modelName) ||
                      GgufMultimodalCompanionStrategy.IsLikelyVisionModel(model.DownloadUrl ?? string.Empty);

        string? localDir = model.LocalPath;
        if (string.IsNullOrEmpty(localDir))
        {
            var subDir = model.ModelType switch
            {
                "reranker" => "Reranker",
                "chat" => "Chat",
                _ => "Embedding"
            };
            var candidate = Path.Combine(_modelsPath, subDir, modelName);
            if (Directory.Exists(candidate)) localDir = candidate;
        }

        bool hasLocalMmproj = false;
        string? localMmprojName = null;
        long localMmprojSize = 0;

        if (!string.IsNullOrEmpty(localDir) && Directory.Exists(localDir))
        {
            var mmprojFile = Directory.GetFiles(localDir, "*.gguf", SearchOption.AllDirectories)
                .FirstOrDefault(f => GgufMultimodalCompanionStrategy.IsMmproj(Path.GetFileName(f)));
            if (mmprojFile != null)
            {
                hasLocalMmproj = true;
                localMmprojName = Path.GetFileName(mmprojFile);
                localMmprojSize = new FileInfo(mmprojFile).Length;
            }
        }

        // 探测远端仓库是否提供 mmproj
        var coordinator = new ModelDownloadCoordinator(defaultAdapterFactory: () => CreateModelSourceAdapter());
        var repo = model.DownloadUrl ?? model.Name;

        ModelDownloadOptions? downloadOptions = null;
        try
        {
            downloadOptions = await coordinator.GetDownloadOptionsAsync(repo, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 探测远端投影器选项失败: {ex.Message}");
        }

        var remoteMmprojs = new List<ModelVariantFileOption>();
        OnnxFileOption? recMmproj = null;

        if (downloadOptions != null && downloadOptions.MmprojOptions.Count > 0)
        {
            isMulti = true;
            recMmproj = downloadOptions.RecommendedMmproj ?? downloadOptions.MmprojOptions.FirstOrDefault();
            foreach (var opt in downloadOptions.MmprojOptions)
            {
                remoteMmprojs.Add(new ModelVariantFileOption
                {
                    Path = opt.Path,
                    FileName = Path.GetFileName(opt.Path),
                    FileSize = opt.Size,
                    Format = "gguf",
                    Quantization = opt.IsQuantized ? "GGUF" : null,
                    IsRecommended = opt.IsRecommended,
                    Description = opt.Description ?? opt.DisplayName,
                    IsMultimodal = true
                });
            }
        }

        var hasRemoteMmproj = remoteMmprojs.Count > 0;
        string status = !isMulti
            ? "not_applicable"
            : (hasLocalMmproj ? "ready" : (hasRemoteMmproj ? "missing" : "not_applicable"));

        // 同步刷新本地模型注册表属性
        model.IsMultimodal = isMulti;
        model.HasMmprojLocally = hasLocalMmproj;
        model.LocalMmprojFileName = localMmprojName;
        model.LocalMmprojSizeBytes = localMmprojSize;
        model.RemoteProjectorAvailable = hasRemoteMmproj;
        model.RemoteProjectorFileName = recMmproj != null ? Path.GetFileName(recMmproj.Path) : null;
        model.RemoteProjectorSizeBytes = recMmproj?.Size ?? 0;
        model.ProjectorStatus = status;

        string msg = status switch
        {
            "ready" => $"本地已就绪视觉投影器: {localMmprojName} ({localMmprojSize / 1024.0 / 1024.0:F1} MB)",
            "missing" => $"检测到仓库存在配套视觉投影器 ({remoteMmprojs.Count} 个可用)，本地尚未下载，点击可单独补全",
            _ => "当前模型不是多模态模型或远端未发布配套投影器"
        };

        return new ProjectorStatusResult
        {
            ModelName = modelName,
            IsMultimodal = isMulti,
            HasLocalMmproj = hasLocalMmproj,
            LocalMmprojFileName = localMmprojName,
            LocalMmprojSizeBytes = localMmprojSize,
            HasRemoteMmproj = hasRemoteMmproj,
            RemoteMmprojFileName = recMmproj != null ? Path.GetFileName(recMmproj.Path) : null,
            RemoteMmprojSizeBytes = recMmproj?.Size ?? 0,
            RemoteMmprojPath = recMmproj?.Path,
            RemoteRepository = recMmproj?.Repository ?? repo,
            AvailableRemoteMmprojs = remoteMmprojs,
            Status = status,
            Message = msg
        };
    }

    /// <summary>
    /// 单独下载/补全模型的视觉投影器（mmproj）
    /// </summary>
    public async Task<(bool Success, string? Error)> DownloadProjectorAsync(
        string modelName,
        string? projectorPath = null,
        string? repository = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            return (false, $"模型不存在: {modelName}");
        }

        // 确定下载目标目录
        var subDir = model.ModelType switch
        {
            "reranker" => "Reranker",
            "chat" => "Chat",
            _ => "Embedding"
        };
        var modelDir = model.LocalPath ?? Path.Combine(_modelsPath, subDir, modelName);
        Directory.CreateDirectory(modelDir);

        try
        {
            var targetRepo = repository ?? model.DownloadUrl ?? modelName;
            string? targetProjectorPath = projectorPath;

            // 若调用方未显式指定 projectorPath，则自动探查推荐的投影器路径
            if (string.IsNullOrWhiteSpace(targetProjectorPath))
            {
                var statusResult = await CheckProjectorStatusAsync(modelName, cancellationToken);
                if (!statusResult.HasRemoteMmproj || string.IsNullOrWhiteSpace(statusResult.RemoteMmprojPath))
                {
                    return (false, $"远端仓库未提供视觉投影器: {targetRepo}");
                }
                targetProjectorPath = statusResult.RemoteMmprojPath;
                if (!string.IsNullOrEmpty(statusResult.RemoteRepository))
                {
                    targetRepo = statusResult.RemoteRepository;
                }
            }

            Console.WriteLine($"[ModelManager] 开始单独下载视觉投影器: {modelName} -> {targetProjectorPath} (Repo: {targetRepo})");

            var adapter = CreateModelSourceAdapter();
            var coordinator = new ModelDownloadCoordinator(defaultAdapterFactory: () => CreateModelSourceAdapter());
            var stateMachine = new DownloadStateMachine(
                $"{modelName}_projector",
                0,
                progress);

            await coordinator.DownloadModelAsync(
                targetRepo,
                modelDir,
                stateMachine,
                adapter,
                targetFormat: "gguf",
                onnxVariantPath: targetProjectorPath,
                cancellationToken: cancellationToken,
                repository: targetRepo
            );

            // 下载完成后重新扫描本地模型，自愈状态
            ScanLocalModels();

            Console.WriteLine($"[ModelManager] 视觉投影器下载完成: {modelName} -> {targetProjectorPath}");
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[ModelManager] 用户取消了视觉投影器下载: {modelName}");
            throw;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 下载视觉投影器失败: {ex.Message}");
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
        // ONNX Runtime 期望外部数据文件名为 model.onnx_data（下划线）
        var onnxDataPath = Path.Combine(modelDir, "model.onnx_data");

        // 首先检查根目录的 ONNX 文件
        if (!File.Exists(onnxPath))
        {
            // 如果根目录没有，搜索子目录（支持 external 格式在 onnx/ 子目录的情况）
            var subOnnxFiles = Directory.GetFiles(modelDir, "model.onnx", SearchOption.AllDirectories);
            if (subOnnxFiles.Length > 0)
            {
                onnxPath = subOnnxFiles[0];
                // 对于 subdirectory 的情况，data 文件通常也在同一目录
                onnxDataPath = Path.Combine(Path.GetDirectoryName(onnxPath)!, "model.onnx_data");
            }
            else
            {
                return "unknown";
            }
        }

        // 如果存在 _data 文件，则为外部数据格式
        if (File.Exists(onnxDataPath))
            return "external";

        var onnxFileInfo = new FileInfo(onnxPath);

        // 检测 ONNX 文件内部是否引用了外部数据（通过文件内容，而非文件大小推断）
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
            // ONNX Runtime 期望外部数据文件名为 model.onnx_data（下划线）
            var onnxDataPath = Path.Combine(model.LocalPath, "model.onnx_data");
            var backupPath = Path.Combine(model.LocalPath, "model.onnx.bak");
            var backupDataPath = Path.Combine(model.LocalPath, "model.onnx_data.bak");

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

            // 使用纯 C# 原生 OnnxFormatConverter 进行转换（零 Python 依赖）
            var (success, error) = await OnnxFormatConverter.ConvertFormatAsync(
                model.LocalPath,
                targetFormat,
                new Progress<float>(p => progress?.Report(10 + p * 0.8f)));

            if (success)
            {
                // 验证转换后的实际格式
                var actualFormat = DetectOnnxFormat(model.LocalPath);
                if (actualFormat != targetFormat)
                {
                    Console.WriteLine($"[ModelManager] 转换后实际格式为 {actualFormat}，未达到目标 {targetFormat}，恢复备份");
                    RestoreBackup(onnxPath, onnxDataPath, backupPath, backupDataPath);
                    model.OnnxFormat = DetectOnnxFormat(model.LocalPath);

                    var sizeGb = model.ModelSizeBytes / 1024.0 / 1024.0 / 1024.0;
                    var hint = actualFormat == "external"
                        ? $"模型 {modelName}（{sizeGb:F1} GB）转换后仍为 external 格式（模型过大，无法内嵌到单文件）。external 格式同样受支持，无需转换。"
                        : $"转换后格式仍为 {actualFormat}，已恢复原始模型。";
                    return (false, hint);
                }

                // 清理备份文件
                if (File.Exists(backupPath)) File.Delete(backupPath);
                if (File.Exists(backupDataPath)) File.Delete(backupDataPath);

                // 清理备份文件
                if (File.Exists(backupPath)) File.Delete(backupPath);
                if (File.Exists(backupDataPath)) File.Delete(backupDataPath);

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
                RestoreBackup(onnxPath, onnxDataPath, backupPath, backupDataPath);
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
            var hub = _configManager.Load().ModelHub;
            var token = !string.IsNullOrWhiteSpace(hub?.HfToken)
                ? hub.HfToken
                : Environment.GetEnvironmentVariable("HF_TOKEN");
            var endpoint = hub?.HfEndpoint;
            var downloader = new HuggingFaceModelDownloader(token, endpoint);
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
    /// 获取模型的下载选项
    /// </summary>
    public async Task<ModelDownloadOptions> GetDownloadOptionsAsync(string modelName, CancellationToken cancellationToken = default)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            Console.WriteLine($"[ModelManager] 模型不存在: {modelName}");
            return new ModelDownloadOptions { ModelName = modelName };
        }

        try
        {
            var adapter = CreateModelSourceAdapter();
            var coordinator = new ModelDownloadCoordinator(defaultAdapterFactory: () => CreateModelSourceAdapter());
            var options = await coordinator.GetDownloadOptionsAsync(model.DownloadUrl ?? $"BAAI/{modelName}", adapter, cancellationToken);
            Console.WriteLine($"[ModelManager] 获取下载选项: {modelName}, HasOnnx={options.HasOnnx}, Options={options.AllOptions.Count}");
            return options;
        }
        catch (OperationCanceledException)
        {
            return new ModelDownloadOptions { ModelName = modelName };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 获取下载选项失败: {ex.Message}");
            return new ModelDownloadOptions { ModelName = modelName };
        }
    }

    public async Task<List<ModelVariantSearchResult>> SearchVariantsAsync(
        string query, string? format = null, int limit = 15, CancellationToken cancellationToken = default)
    {
        try
        {
            using var adapter = CreateModelSourceAdapter();
            return await adapter.SearchVariantsAsync(query, format, limit, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 搜索社区变种异常: {ex.Message}");
            return new List<ModelVariantSearchResult>();
        }
    }

    public async Task<ModelVariantDetail?> GetVariantDetailAsync(
        string repositoryId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var adapter = CreateModelSourceAdapter();
            return await adapter.GetVariantDetailAsync(repositoryId, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 获取变种详情异常: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 设置模型保存路径（可选迁移已有模型）
    /// </summary>
    public async Task<ModelPathChangeResult> SetModelsPathAsync(
        string newPath, bool migrateExisting = false, CancellationToken cancellationToken = default)
    {
        var result = new ModelPathChangeResult();

        // 1. 验证路径不为空
        if (string.IsNullOrWhiteSpace(newPath))
        {
            result.Error = "路径不能为空";
            return result;
        }

        // 1b. 检查 Windows 非法字符（注意: : 是盘符分隔符，如 E:\，在路径中间才是非法的）
        // Windows 合法的路径分隔符: \ 和 /
        // Windows 非法的文件名字符: < > : " | ? * （但在路径首个盘符后的第二个字符是合法的）
        var illegalChars = new[] { '<', '>', '"', '|', '?', '*' };
        foreach (var c in illegalChars)
        {
            if (newPath.Contains(c))
            {
                result.Error = $"路径不能包含非法字符: {c}";
                return result;
            }
        }
        // 检查是否有孤立的 :（不在盘符位置，如 E: 或 E:\ 之后的 :）
        // 盘符格式: X:\ 或 X: 后面跟其他路径
        if (newPath.Length >= 2 && newPath[1] == ':')
        {
            // 这看起来像盘符，检查后面是否有其他 : 出现
            var afterDrive = newPath.Substring(2);
            if (afterDrive.Contains(':'))
            {
                result.Error = "路径不能包含非法字符: :";
                return result;
            }
        }

        // 1c. 检查相对路径 // OPT-20260412-001: Issue-4 路径格式验证
        var isAbsolute = Path.IsPathRooted(newPath);
        if (!isAbsolute)
        {
            result.Error = "必须使用绝对路径";
            return result;
        }

        // 2. 规范化路径
        var normalizedPath = Path.GetFullPath(newPath);

        // 如果路径没有变化，直接返回，避免误触发模型状态重置
        if (string.Equals(normalizedPath, _modelsPath, StringComparison.OrdinalIgnoreCase))
        {
            result.Success = true;
            return result;
        }

        // 3. 禁止根目录
        if (normalizedPath == Path.GetPathRoot(normalizedPath))
        {
            result.Error = "不能使用磁盘根目录作为模型路径";
            return result;
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
                result.Error = "不能使用系统目录作为模型路径";
                return result;
            }
        }

        // 5. 禁止路径遍历
        if (newPath.Contains("..") || newPath.Contains('~'))
        {
            result.Error = "路径不能包含 '..' 或 '~'";
            return result;
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
                        result.Migrated.Add(dirName);
                    }
                }
            }

            // 7. 创建新目录
            Directory.CreateDirectory(normalizedPath);

            // 8. 更新配置（更新顶层 ModelsRootPath）
            var config = await LoadConfigAsync();
            config.ModelsRootPath = normalizedPath;
            await SaveConfigAsync(config);

            // 9. 更新内部字段并重新扫描
            _modelsPath = normalizedPath;
            ScanLocalModels();

            // 9.1 获取扫描到的模型列表（返回全部注册模型，保留预定义元数据）// OPT-20260412-001: Issue-2 模型列表返回
            result.EmbeddingModels = _modelRegistry.Values
                .Where(m => m.ModelType == "embedding")
                .ToList();
            result.RerankModels = _modelRegistry.Values
                .Where(m => m.ModelType == "reranker")
                .ToList();

            // 9.2 清除当前模型引用（路径变更后需要重新选择）// OPT-20260412-001: Issue-1 模型卸载
            _currentModel = null;
            _currentRerankModel = null;

            // 9.3 清除配置中的模型名称（避免 GetCurrentModel 仍返回旧模型）
            config.Embedding.ModelName = null;
            config.Embedding.ModelPath = null;
            if (config.Rerank != null)
            {
                config.Rerank.CurrentModel = null;
                config.Rerank.ModelPath = null;
            }
            await SaveConfigAsync(config);

            // 触发模型路径变更事件，通知外部服务卸载旧模型
            ModelPathChanged?.Invoke(this, new ModelPathChangedEventArgs
            {
                NewPath = normalizedPath,
                MigratedModels = result.Migrated
            });

            Console.WriteLine($"[ModelManager] 模型路径已更新: {normalizedPath} (迁移了 {result.Migrated.Count} 个模型, 扫描到 {result.EmbeddingModels.Count} 个嵌入模型, {result.RerankModels.Count} 个重排模型)");
            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 设置模型路径失败: {ex.Message}");
            result.Error = $"设置路径失败: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// 恢复转换前的备份文件。
    /// 以备份文件是否存在为基准：若原始模型无 .data 文件（backupDataPath 不存在），
    /// 则转换过程产生的任何 .data 文件都必须清理，避免污染模型目录。
    /// </summary>
    private static void RestoreBackup(string onnxPath, string onnxDataPath, string backupPath, string backupDataPath)
    {
        if (File.Exists(backupPath))
        {
            File.Copy(backupPath, onnxPath, true);
            File.Delete(backupPath);
            Console.WriteLine($"[ModelManager] 已恢复 ONNX 文件: {onnxPath}");
        }

        if (File.Exists(backupDataPath))
        {
            // 原始模型有 .data 文件，恢复它
            File.Copy(backupDataPath, onnxDataPath, true);
            File.Delete(backupDataPath);
            Console.WriteLine($"[ModelManager] 已恢复外部数据文件: {onnxDataPath}");
        }
        else
        {
            // 原始模型没有 .data 文件；若转换产生了 .data，必须清理
            if (File.Exists(onnxDataPath))
            {
                File.Delete(onnxDataPath);
                Console.WriteLine($"[ModelManager] 已清理转换产生的残留 .data 文件: {onnxDataPath}");
            }
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

    // ========== 重排模型管理实现 ==========

    /// <summary>
    /// 获取当前重排模型
    /// </summary>
    public ModelInfo? GetCurrentRerankModel()
    {
        var cfg = _configManager.Load();
        if (cfg.Rerank?.Mode == "openai" && !string.IsNullOrEmpty(cfg.Rerank.ModelName))
        {
            return new ModelInfo
            {
                Name = cfg.Rerank.ModelName,
                DisplayName = $"{cfg.Rerank.ModelName} (API)",
                ModelType = "reranker",
                IsDownloaded = true
            };
        }

        return _currentRerankModel;
    }

    /// <summary>
    /// 切换重排模型
    /// </summary>
    public async Task<bool> SwitchRerankModelAsync(string modelName)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            Console.WriteLine($"[ModelManager] 重排模型不存在: {modelName}");
            return false;
        }

        if (model.ModelType != "reranker")
        {
            Console.WriteLine($"[ModelManager] 模型 {modelName} 不是重排模型 (ModelType: {model.ModelType})");
            return false;
        }

        if (!model.IsDownloaded)
        {
            Console.WriteLine($"[ModelManager] 重排模型未下载: {modelName}");
            return false;
        }

        try
        {
            _currentRerankModel = model;

            // 保存到配置
            var config = await LoadConfigAsync();
            if (config.Rerank == null)
            {
                config.Rerank = new RerankConfig();
            }
            var resolvedPath = ResolveModelFilePath(model.LocalPath, model.ActiveVariantFileName);
            config.Rerank.CurrentModel = modelName;
            config.Rerank.ModelPath = resolvedPath;
            await SaveConfigAsync(config);

            Console.WriteLine($"[ModelManager] 已切换重排模型: {model.DisplayName} (Path={resolvedPath})");

            // 触发重排模型切换事件
            RerankModelSwitched?.Invoke(this, new ModelSwitchedEventArgs
            {
                ModelName = modelName,
                ModelPath = resolvedPath
            });

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 切换重排模型失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取所有重排模型列表
    /// </summary>
    public List<ModelInfo> GetRerankModels()
    {
        return _modelRegistry.Values
            .Where(m => m.ModelType == "reranker")
            .ToList();
    }

    /// <summary>
    /// 获取已下载的重排模型列表
    /// </summary>
    public List<ModelInfo> GetDownloadedRerankModels()
    {
        return _modelRegistry.Values
            .Where(m => m.ModelType == "reranker" && m.IsDownloaded)
            .ToList();
    }

    // ========== 聊天模型管理实现 ==========

    /// <summary>
    /// 获取当前聊天模型
    /// </summary>
    public ModelInfo? GetCurrentChatModel()
    {
        var cfg = _configManager.Load();
        if (cfg.Chat?.ActiveBackend == "OpenAiApi" && !string.IsNullOrEmpty(cfg.Chat.OpenAiApi?.Model))
        {
            return new ModelInfo
            {
                Name = cfg.Chat.OpenAiApi.Model,
                DisplayName = $"{cfg.Chat.OpenAiApi.Model} (API)",
                ModelType = "chat",
                IsDownloaded = true
            };
        }

        if (_currentChatModel != null)
        {
            return _currentChatModel;
        }

        var activeName = cfg.Chat?.ActiveBackend == "LocalOnnxGenAi"
            ? cfg.Chat.LocalOnnxGenAi?.ModelName
            : cfg.Chat?.LocalGguf?.ModelName;

        if (!string.IsNullOrEmpty(activeName) && _modelRegistry.TryGetValue(activeName, out var found))
        {
            return found;
        }

        return null;
    }

    /// <summary>
    /// 切换聊天模型
    /// </summary>
    public async Task<bool> SwitchChatModelAsync(string modelName)
    {
        if (!_modelRegistry.TryGetValue(modelName, out var model))
        {
            Console.WriteLine($"[ModelManager] 聊天模型不存在: {modelName}");
            return false;
        }

        if (model.ModelType != "chat")
        {
            Console.WriteLine($"[ModelManager] 模型 {modelName} 不是聊天模型 (ModelType: {model.ModelType})");
            return false;
        }

        if (!model.IsDownloaded)
        {
            Console.WriteLine($"[ModelManager] 聊天模型未下载: {modelName}");
            return false;
        }

        try
        {
            _currentChatModel = model;

            var config = await LoadConfigAsync();
            if (config.Chat == null)
            {
                config.Chat = new ChatModelConfig();
            }
            var resolvedPath = ResolveModelFilePath(model.LocalPath, model.ActiveVariantFileName);
            var isGguf = (resolvedPath != null && resolvedPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) ||
                         model.Name.Contains("GGUF", StringComparison.OrdinalIgnoreCase) ||
                         (model.ActiveVariantFileName != null && model.ActiveVariantFileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));

            var isOnnx = !isGguf &&
                         ((resolvedPath != null && (resolvedPath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ||
                                                   (Directory.Exists(resolvedPath) && (File.Exists(Path.Combine(resolvedPath, "genai_config.json")) || File.Exists(Path.Combine(resolvedPath, "model.onnx")))))) ||
                          model.Name.Contains("onnx", StringComparison.OrdinalIgnoreCase));

            config.Chat.Model = modelName;

            if (isOnnx)
            {
                config.Chat.ActiveBackend = "LocalOnnxGenAi";
                if (config.Chat.LocalOnnxGenAi == null) config.Chat.LocalOnnxGenAi = new LocalOnnxGenAiChatProfile();
                config.Chat.LocalOnnxGenAi.ModelName = modelName;
                var ogaDir = OnnxGenAiPathResolver.ResolveModelDirectory(resolvedPath) ?? resolvedPath;
                config.Chat.LocalOnnxGenAi.ModelPath = ogaDir;
            }
            else
            {
                config.Chat.ActiveBackend = "LocalGguf";
                if (config.Chat.LocalGguf == null) config.Chat.LocalGguf = new LocalGgufChatProfile();
                config.Chat.LocalGguf.ModelName = modelName;
                config.Chat.LocalGguf.Variant = model.ActiveVariantFileName;
                config.Chat.LocalGguf.ModelPath = resolvedPath;
                var planner = new ReferenceRAG.Core.Features.HardwareAcceleration.ChatPerformancePlanner();
                var plan = planner.PlanProfile(
                    resolvedPath,
                    modelName,
                    useGpu: config.Chat.LocalGguf.Acceleration?.Backend != AccelerationBackend.Cpu);

                config.Chat.LocalGguf.ContextSize = plan.ContextSize;
                config.Chat.LocalGguf.GpuLayers = plan.GpuLayers;
                config.Chat.LocalGguf.BatchSize = plan.BatchSize;
                config.Chat.LocalGguf.UBatchSize = plan.UBatchSize;
            }
            await SaveConfigAsync(config);

            Console.WriteLine($"[ModelManager] 已切换聊天模型: {model.DisplayName} (Path={resolvedPath}, Backend={config.Chat.ActiveBackend})");

            ChatModelSwitched?.Invoke(this, new ModelSwitchedEventArgs
            {
                ModelName = modelName,
                ModelPath = resolvedPath
            });

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ModelManager] 切换聊天模型失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取所有聊天模型列表
    /// </summary>
    public List<ModelInfo> GetChatModels()
    {
        return _modelRegistry.Values
            .Where(m => m.ModelType == "chat")
            .ToList();
    }

    /// <summary>
    /// 获取已下载的聊天模型列表
    /// </summary>
    public List<ModelInfo> GetDownloadedChatModels()
    {
        var models = _modelRegistry.Values
            .Where(m => m.ModelType == "chat" && m.IsDownloaded)
            .ToList();

        if (models.Count == 0 && Directory.Exists(_modelsPath))
        {
            var chatDir = Path.Combine(_modelsPath, "Chat");
            if (Directory.Exists(chatDir) && Directory.EnumerateFiles(chatDir, "*.gguf", SearchOption.AllDirectories).Any())
            {
                ScanLocalModels();
                models = _modelRegistry.Values
                    .Where(m => m.ModelType == "chat" && m.IsDownloaded)
                    .ToList();
            }
        }

        return models;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
