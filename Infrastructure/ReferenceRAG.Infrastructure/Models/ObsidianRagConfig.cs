namespace ReferenceRAG.Core.Models;

/// <summary>
/// ReferenceRAG 配置
/// </summary>
public class ObsidianRagConfig
{
    /// <summary>
    /// 配置文件结构版本（用于自动版本迁移，默认 2）
    /// </summary>
    public int SchemaVersion { get; set; } = 2;

    /// <summary>
    /// 数据存储路径
    /// </summary>
    public string DataPath { get; set; } = "data";

    /// <summary>
    /// 模型根目录（统一管理所有模型：嵌入式和重排）
    /// </summary>
    public string ModelsRootPath { get; set; } = "models";

    /// <summary>
    /// 源文件夹列表（支持多个）
    /// </summary>
    public List<SourceFolder> Sources { get; set; } = new();

    /// <summary>
    /// 向量模型配置
    /// </summary>
    public EmbeddingConfig Embedding { get; set; } = new();

    /// <summary>
    /// 分段配置
    /// </summary>
    public ChunkingConfig Chunking { get; set; } = new();

    /// <summary>
    /// 搜索配置
    /// </summary>
    public SearchConfig Search { get; set; } = new();

    /// <summary>
    /// 服务配置
    /// </summary>
    public ServiceConfig Service { get; set; } = new();

    /// <summary>
    /// 受控临时诊断配置
    /// </summary>
    public DiagnosticsConfig Diagnostics { get; set; } = new();

    /// <summary>
    /// 重排模型配置
    /// </summary>
    public RerankConfig Rerank { get; set; } = new();

    /// <summary>
    /// 索引配置
    /// </summary>
    public IndexingConfig Indexing { get; set; } = new();

    /// <summary>
    /// 模型中心与下载配置 (HuggingFace / 镜像源 / Token)
    /// </summary>
    public ModelHubConfig ModelHub { get; set; } = new();

    /// <summary>
    /// 模型常规驻留方案：Permanent（固定常驻）或 SlidingExpiration（滑动过期）
    /// </summary>
    public ModelRetentionStrategy ModelRetentionStrategy { get; set; } = ModelRetentionStrategy.SlidingExpiration;

    /// <summary>
    /// 模型无使用滑动过期自动卸载显存时间（秒，默认 300 即 5 分钟，<=0 表示常驻不卸载）
    /// </summary>
    public int ModelIdleTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// 仅在最小化到托盘时才启用滑动过期（窗口打开使用期间保持常驻）
    /// </summary>
    public bool UnloadOnlyWhenMinimizedToTray { get; set; } = true;

    /// <summary>
    /// 聊天对话模型配置
    /// </summary>
    public ChatModelConfig Chat { get; set; } = new();
}

/// <summary>
/// 模型常规驻留方案
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum ModelRetentionStrategy
{
    /// <summary>固定常驻（首次加载后长久驻留显存/内存，零冷启动等待）</summary>
    Permanent = 0,
    /// <summary>滑动过期（超时无调用后自动释放显存/内存）</summary>
    SlidingExpiration = 1
}


/// <summary>
/// 源文件夹配置
/// </summary>
public class SourceFolder
{
    /// <summary>
    /// 文件夹路径
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// 显示名称（用于区分不同来源）
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// 是否启用
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 文件匹配模式
    /// </summary>
    public List<string> FilePatterns { get; set; } = new() { "*.md", "*.txt" };

    /// <summary>
    /// 排除的目录
    /// </summary>
    public List<string> ExcludeDirs { get; set; } = new()
    {
        ".git",
        "node_modules",
        ".trash"
    };

    /// <summary>
    /// 排除的文件模式
    /// </summary>
    public List<string> ExcludeFiles { get; set; } = new()
    {
        "*.tmp",
        "*.bak",
        "~*"
    };

    /// <summary>
    /// 文件类型（用于分类）
    /// </summary>
    public SourceType Type { get; set; } = SourceType.Markdown;

    /// <summary>
    /// 自定义标签（用于过滤）
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 优先级（数字越大优先级越高）
    /// </summary>
    public int Priority { get; set; } = 0;
}

/// <summary>
/// 源类型
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum SourceType
{
    /// <summary>
    /// Obsidian 笔记库
    /// </summary>
    Obsidian,

    /// <summary>
    /// 普通 Markdown 文件
    /// </summary>
    Markdown,

    /// <summary>
    /// 文档目录
    /// </summary>
    Documents,

    /// <summary>
    /// 代码文档
    /// </summary>
    CodeDocs,

    /// <summary>
    /// 自定义
    /// </summary>
    Custom
}

/// <summary>
/// 硬件加速后端方案
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum AccelerationBackend
{
    /// <summary>自动感知协商（智能推荐，平滑降级）</summary>
    Auto = 0,
    /// <summary>NVIDIA CUDA 专有硬件加速</summary>
    Cuda = 1,
    /// <summary>通用 DirectML 跨平台算力 (AMD/Intel/Windows)</summary>
    DirectML = 2,
    /// <summary>通用 Vulkan 跨平台算力 (GGUF/llama.cpp)</summary>
    Vulkan = 3,
    /// <summary>AMD ROCm / HIP</summary>
    Rocm = 4,
    /// <summary>Intel OpenVINO / Level Zero</summary>
    OpenVino = 5,
    /// <summary>纯 CPU 运算</summary>
    Cpu = 6
}

/// <summary>
/// 硬件加速配置
/// </summary>
public class HardwareAccelerationConfig
{
    /// <summary>
    /// 加速后端方案（默认 Auto 自动感知协商）
    /// </summary>
    public AccelerationBackend Backend { get; set; } = AccelerationBackend.Auto;

    /// <summary>
    /// 设备 ID (0-based)
    /// </summary>
    public int DeviceId { get; set; } = 0;

    /// <summary>
    /// 自定义库搜索路径（可选）
    /// </summary>
    public string? CustomLibraryPath { get; set; }

    /// <summary>
    /// 发生异常时是否自动平滑回退至 CPU
    /// </summary>
    public bool FallbackToCpuOnFailure { get; set; } = true;

    /// <summary>
    /// 是否请求启用硬件加速（非纯 CPU）
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEnabled => Backend != AccelerationBackend.Cpu;
}

/// <summary>
/// 本地 ONNX 向量嵌入 Profile
/// </summary>
public class LocalOnnxEmbeddingProfile
{
    public string ModelPath { get; set; } = "";
    public string ModelName { get; set; } = "bge-small-zh-v1.5";
    public HardwareAccelerationConfig Acceleration { get; set; } = new();
    public int MaxSequenceLength { get; set; } = 512;
    public int BatchSize { get; set; } = 32;
}

/// <summary>
/// 本地 GGUF 向量嵌入 Profile
/// </summary>
public class LocalGgufEmbeddingProfile
{
    public string ModelPath { get; set; } = "";
    public string ModelName { get; set; } = "bge-m3-gguf";
    public HardwareAccelerationConfig Acceleration { get; set; } = new();
    public int GpuLayers { get; set; } = 33;
    public int MaxSequenceLength { get; set; } = 512;
}

/// <summary>
/// OpenAI 兼容 API 向量嵌入 Profile
/// </summary>
public class OpenAiApiEmbeddingProfile
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string ModelName { get; set; } = "bge-m3";
    public int? Dimension { get; set; }
    public int? MaxInputTokens { get; set; } = 512;
}

/// <summary>
/// 向量模型配置
/// </summary>
public class EmbeddingConfig
{
    /// <summary>当前激活后端方案：LocalOnnx | LocalGguf | OpenAiApi</summary>
    public string ActiveBackend { get; set; } = "LocalOnnx";

    public LocalOnnxEmbeddingProfile LocalOnnx { get; set; } = new();
    public LocalGgufEmbeddingProfile LocalGguf { get; set; } = new();
    public OpenAiApiEmbeddingProfile OpenAiApi { get; set; } = new();

    private string _modelPath = "";
    private string _modelName = "bge-small-zh-v1.5";
    private HardwareAccelerationConfig _acceleration = new();
    private int _maxSequenceLength = 512;
    private int _batchSize = 32;
    private string _mode = "onnx";
    private string? _apiBaseUrl;
    private string? _apiKey;
    private int? _apiDimension;
    private int? _apiMaxInputTokens = 512;

    /// <summary>模型路径</summary>
    public string ModelPath
    {
        get => !string.IsNullOrEmpty(LocalOnnx?.ModelPath) ? LocalOnnx.ModelPath : _modelPath;
        set
        {
            _modelPath = value;
            if (LocalOnnx != null) LocalOnnx.ModelPath = value;
        }
    }

    /// <summary>模型名称</summary>
    public string ModelName
    {
        get
        {
            if (ActiveBackend == "OpenAiApi" && !string.IsNullOrEmpty(OpenAiApi?.ModelName))
                return OpenAiApi.ModelName;
            if (ActiveBackend == "LocalGguf" && !string.IsNullOrEmpty(LocalGguf?.ModelName))
                return LocalGguf.ModelName;
            if (!string.IsNullOrEmpty(LocalOnnx?.ModelName))
                return LocalOnnx.ModelName;
            return _modelName;
        }
        set
        {
            _modelName = value;
            if (ActiveBackend == "OpenAiApi" && OpenAiApi != null) OpenAiApi.ModelName = value;
            else if (ActiveBackend == "LocalGguf" && LocalGguf != null) LocalGguf.ModelName = value;
            else if (LocalOnnx != null) LocalOnnx.ModelName = value;
        }
    }

    /// <summary>统一硬件加速配置</summary>
    public HardwareAccelerationConfig Acceleration
    {
        get => LocalOnnx?.Acceleration ?? _acceleration;
        set
        {
            _acceleration = value ?? new HardwareAccelerationConfig();
            if (LocalOnnx != null) LocalOnnx.Acceleration = _acceleration;
            if (LocalGguf != null) LocalGguf.Acceleration = _acceleration;
        }
    }

    [Obsolete("请使用 Acceleration.Backend 配置硬件加速")]
    public bool UseCuda
    {
        get => Acceleration.IsEnabled;
        set
        {
            if (!value) Acceleration.Backend = AccelerationBackend.Cpu;
            else if (Acceleration.Backend == AccelerationBackend.Cpu) Acceleration.Backend = AccelerationBackend.Auto;
        }
    }

    [Obsolete("请使用 Acceleration.DeviceId")]
    public int CudaDeviceId
    {
        get => Acceleration.DeviceId;
        set => Acceleration.DeviceId = value;
    }

    [Obsolete("请使用 Acceleration.CustomLibraryPath")]
    public string? CudaLibraryPath
    {
        get => Acceleration.CustomLibraryPath;
        set => Acceleration.CustomLibraryPath = value;
    }

    /// <summary>最大序列长度</summary>
    public int MaxSequenceLength
    {
        get => LocalOnnx?.MaxSequenceLength ?? _maxSequenceLength;
        set
        {
            _maxSequenceLength = value;
            if (LocalOnnx != null) LocalOnnx.MaxSequenceLength = value;
        }
    }

    /// <summary>批处理大小</summary>
    public int BatchSize
    {
        get => LocalOnnx?.BatchSize ?? _batchSize;
        set
        {
            _batchSize = value;
            if (LocalOnnx != null) LocalOnnx.BatchSize = value;
        }
    }

    /// <summary>推理模式：onnx（本地）| openai（兼容 API）</summary>
    public string Mode
    {
        get => ActiveBackend == "OpenAiApi" ? "openai" : "onnx";
        set
        {
            _mode = value;
            if (string.Equals(value, "openai", StringComparison.OrdinalIgnoreCase))
            {
                ActiveBackend = "OpenAiApi";
            }
            else if (string.Equals(value, "llama", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "gguf", StringComparison.OrdinalIgnoreCase))
            {
                ActiveBackend = "LocalGguf";
            }
            else
            {
                ActiveBackend = "LocalOnnx";
            }
        }
    }

    /// <summary>API 基础地址</summary>
    public string? ApiBaseUrl
    {
        get => OpenAiApi?.BaseUrl ?? _apiBaseUrl;
        set
        {
            _apiBaseUrl = value;
            if (OpenAiApi != null) OpenAiApi.BaseUrl = value;
        }
    }

    /// <summary>API Key</summary>
    public string? ApiKey
    {
        get => OpenAiApi?.ApiKey ?? _apiKey;
        set
        {
            _apiKey = value;
            if (OpenAiApi != null) OpenAiApi.ApiKey = value;
        }
    }

    /// <summary>API 模式的向量维度</summary>
    public int? ApiDimension
    {
        get => OpenAiApi?.Dimension ?? _apiDimension;
        set
        {
            _apiDimension = value;
            if (OpenAiApi != null) OpenAiApi.Dimension = value;
        }
    }

    /// <summary>OpenAI 兼容 API 单条输入的最大 token 数</summary>
    public int? ApiMaxInputTokens
    {
        get => OpenAiApi?.MaxInputTokens ?? _apiMaxInputTokens;
        set
        {
            _apiMaxInputTokens = value;
            if (OpenAiApi != null) OpenAiApi.MaxInputTokens = value;
        }
    }
}

/// <summary>
/// 分段配置
/// </summary>
public class ChunkingConfig
{
    /// <summary>
    /// 最大 Token 数
    /// </summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>
    /// 最小 Token 数
    /// </summary>
    public int MinTokens { get; set; } = 50;

    /// <summary>
    /// 重叠 Token 数
    /// </summary>
    public int OverlapTokens { get; set; } = 50;

    /// <summary>
    /// 是否保留标题
    /// </summary>
    public bool PreserveHeadings { get; set; } = true;

    /// <summary>
    /// 是否保留代码块
    /// </summary>
    public bool PreserveCodeBlocks { get; set; } = true;

    /// <summary>
    /// 分段模式：Fixed（固定）| Adaptive（自适应）
    /// </summary>
    public string Mode { get; set; } = "Fixed";

    /// <summary>
    /// 自适应等级：Level1（模型感知）| Level2（结构句法）| Level3（深度语义）
    /// </summary>
    public string AdaptiveLevel { get; set; } = "Level2";

    /// <summary>
    /// 自适应风格类型：Balanced（通用平衡）| PrecisionDense（高精度）| DeepNarrative（长文叙事）| CodeFirst（代码优先）
    /// </summary>
    public string AdaptiveStrategyType { get; set; } = "Balanced";

    /// <summary>
    /// 目标模型窗口利用率百分比（50~90，默认 70）
    /// </summary>
    public int TargetWindowUtilizationPercent { get; set; } = 70;

    /// <summary>
    /// 动态重叠率百分比（0~25，默认 10）
    /// </summary>
    public int AdaptiveOverlapPercent { get; set; } = 10;
}

/// <summary>
/// 搜索配置
/// </summary>
public class SearchConfig
{
    /// <summary>
    /// 默认返回数量
    /// </summary>
    public int DefaultTopK { get; set; } = 10;

    /// <summary>
    /// 上下文窗口大小
    /// </summary>
    public int ContextWindow { get; set; } = 1;

    /// <summary>
    /// 相似度阈值
    /// </summary>
    public float SimilarityThreshold { get; set; } = 0.5f;

    /// <summary>
    /// 是否启用 MMR 多样性
    /// </summary>
    public bool EnableMmr { get; set; } = true;

    /// <summary>
    /// MMR lambda 参数
    /// </summary>
    public float MmrLambda { get; set; } = 0.7f;

    /// <summary>
    /// 默认搜索的源（空=全部）
    /// </summary>
    public List<string> DefaultSources { get; set; } = new();

    /// <summary>
    /// BM25 提供者类型：fts5
    /// </summary>
    public string BM25Provider { get; set; } = "fts5";

    /// <summary>
    /// 是否启用知识图谱扩展召回（基于 wiki-link 邻居节点）
    /// </summary>
    public bool EnableGraphExpansion { get; set; } = false;

    /// <summary>
    /// 图扩展遍历深度（1-2，默认 1）
    /// </summary>
    public int GraphExpansionDepth { get; set; } = 1;

    /// <summary>
    /// 每个结果最多扩展的邻居节点数（默认 3）
    /// </summary>
    public int GraphExpansionMaxNodes { get; set; } = 3;
}

/// <summary>
/// 服务配置
/// </summary>
public class ServiceConfig
{
    /// <summary>
    /// 监听端口
    /// </summary>
    public int Port { get; set; } = 5000;

    /// <summary>
    /// 是否允许来自局域网/外部网络的访问。
    /// true → 绑定 0.0.0.0（全网可访问）；false → 仅绑定 localhost（默认）。
    /// 修改后需要重启服务生效。
    /// </summary>
    public bool AllowNetworkAccess { get; set; } = false;

    /// <summary>
    /// 绑定的主机地址（可选，优先于 AllowNetworkAccess）
    /// </summary>
    public string? Host { get; set; }

    /// <summary>
    /// 实际绑定的主机地址
    /// </summary>
    public string EffectiveHost => Host ?? (AllowNetworkAccess ? "0.0.0.0" : "localhost");

    /// <summary>
    /// 是否启用 HTTP 服务（仅桌面端有意义）。
    /// 桌面端默认关闭：前后端通过进程内 IPC 通信，不监听端口，避免端口占用与外部访问。
    /// 开启后额外监听 HTTP（地址由 AllowNetworkAccess 决定），便于用浏览器/curl/Swagger 调试。
    /// 修改后需要重启生效。
    /// </summary>
    public bool EnableHttpService { get; set; } = false;

    /// <summary>
    /// 是否启用 CORS
    /// </summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>
    /// 是否启用 Swagger
    /// </summary>
    public bool EnableSwagger { get; set; } = true;

    /// <summary>
    /// 日志级别
    /// </summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// API Key 用于认证（为空则不启用认证）
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// 受控临时诊断密钥 DIAG_KEY（为空则不启用诊断中间件，长度需 >= 8 位。配置文件优先级最高，其次是环境变量）
    /// </summary>
    public string? DiagKey { get; set; }

    /// <summary>
    /// 临时诊断路由前缀（默认为 /__diag）
    /// </summary>
    public string? DiagRoutePrefix { get; set; }
}

/// <summary>
/// 受控临时诊断配置
/// </summary>
public class DiagnosticsConfig
{
    /// <summary>
    /// 诊断访问密钥（为空则不启动，需 >= 8 位。配置文件优先级高于环境变量）
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// 诊断路由前缀（默认为 /__diag）
    /// </summary>
    public string RoutePrefix { get; set; } = "/__diag";
}

/// <summary>
/// 本地 ONNX 重排 Profile
/// </summary>
public class LocalOnnxRerankProfile
{
    public string ModelName { get; set; } = "bge-reranker-base";
    public string? CurrentModel { get; set; }
    public string? ModelPath { get; set; }
    public HardwareAccelerationConfig Acceleration { get; set; } = new();
}

/// <summary>
/// 本地 GGUF 重排 Profile
/// </summary>
public class LocalGgufRerankProfile
{
    public string ModelName { get; set; } = "bge-reranker-v2-m3-gguf";
    public string? CurrentModel { get; set; }
    public string? ModelPath { get; set; }
    public int GpuLayers { get; set; } = 28;
    public HardwareAccelerationConfig Acceleration { get; set; } = new();
}

/// <summary>
/// OpenAI 兼容 API 重排 Profile
/// </summary>
public class OpenAiApiRerankProfile
{
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string ModelName { get; set; } = "bge-reranker-v2-m3";
}

/// <summary>
/// 重排模型配置
/// </summary>
public class RerankConfig
{
    /// <summary>是否启用重排功能</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>当前激活后端方案：LocalOnnx | LocalGguf | OpenAiApi</summary>
    public string ActiveBackend { get; set; } = "LocalOnnx";

    public LocalOnnxRerankProfile LocalOnnx { get; set; } = new();
    public LocalGgufRerankProfile LocalGguf { get; set; } = new();
    public OpenAiApiRerankProfile OpenAiApi { get; set; } = new();

    public int TopN { get; set; } = 10;
    public int RecallFactor { get; set; } = 3;
    public bool AutoRerankInHybrid { get; set; } = true;
    public float ScoreThreshold { get; set; } = 0.05f;

    private string _modelName = "bge-reranker-base";
    private string? _currentModel;
    private string? _modelPath;
    private HardwareAccelerationConfig _acceleration = new();
    private string _mode = "onnx";
    private string? _apiBaseUrl;
    private string? _apiKey;

    /// <summary>模型名称</summary>
    public string ModelName
    {
        get
        {
            if (ActiveBackend == "OpenAiApi" && !string.IsNullOrEmpty(OpenAiApi?.ModelName))
                return OpenAiApi.ModelName;
            if (ActiveBackend == "LocalGguf" && !string.IsNullOrEmpty(LocalGguf?.ModelName))
                return LocalGguf.ModelName;
            if (!string.IsNullOrEmpty(LocalOnnx?.ModelName))
                return LocalOnnx.ModelName;
            return _modelName;
        }
        set
        {
            _modelName = value;
            if (ActiveBackend == "OpenAiApi" && OpenAiApi != null) OpenAiApi.ModelName = value;
            else if (ActiveBackend == "LocalGguf" && LocalGguf != null) LocalGguf.ModelName = value;
            else if (LocalOnnx != null) LocalOnnx.ModelName = value;
        }
    }

    /// <summary>当前使用的重排模型</summary>
    public string? CurrentModel
    {
        get => LocalOnnx?.CurrentModel ?? _currentModel;
        set
        {
            _currentModel = value;
            if (LocalOnnx != null) LocalOnnx.CurrentModel = value;
        }
    }

    /// <summary>模型路径</summary>
    public string? ModelPath
    {
        get => LocalOnnx?.ModelPath ?? _modelPath;
        set
        {
            _modelPath = value;
            if (LocalOnnx != null) LocalOnnx.ModelPath = value;
        }
    }

    /// <summary>统一硬件加速配置</summary>
    public HardwareAccelerationConfig Acceleration
    {
        get => LocalOnnx?.Acceleration ?? _acceleration;
        set
        {
            _acceleration = value ?? new HardwareAccelerationConfig();
            if (LocalOnnx != null) LocalOnnx.Acceleration = _acceleration;
            if (LocalGguf != null) LocalGguf.Acceleration = _acceleration;
        }
    }

    [Obsolete("请使用 Acceleration.Backend 配置硬件加速")]
    public bool UseCuda
    {
        get => Acceleration.IsEnabled;
        set
        {
            if (!value) Acceleration.Backend = AccelerationBackend.Cpu;
            else if (Acceleration.Backend == AccelerationBackend.Cpu) Acceleration.Backend = AccelerationBackend.Auto;
        }
    }

    [Obsolete("请使用 Acceleration.DeviceId")]
    public int CudaDeviceId
    {
        get => Acceleration.DeviceId;
        set => Acceleration.DeviceId = value;
    }

    [Obsolete("请使用 Acceleration.CustomLibraryPath")]
    public string? CudaLibraryPath
    {
        get => Acceleration.CustomLibraryPath;
        set => Acceleration.CustomLibraryPath = value;
    }

    /// <summary>推理模式：onnx（本地）| openai（兼容 API）</summary>
    public string Mode
    {
        get => ActiveBackend == "OpenAiApi" ? "openai" : "onnx";
        set
        {
            _mode = value;
            if (string.Equals(value, "openai", StringComparison.OrdinalIgnoreCase))
            {
                ActiveBackend = "OpenAiApi";
            }
            else if (string.Equals(value, "llama", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "gguf", StringComparison.OrdinalIgnoreCase))
            {
                ActiveBackend = "LocalGguf";
            }
            else
            {
                ActiveBackend = "LocalOnnx";
            }
        }
    }

    /// <summary>API 基础地址</summary>
    public string? ApiBaseUrl
    {
        get => OpenAiApi?.BaseUrl ?? _apiBaseUrl;
        set
        {
            _apiBaseUrl = value;
            if (OpenAiApi != null) OpenAiApi.BaseUrl = value;
        }
    }

    /// <summary>API Key</summary>
    public string? ApiKey
    {
        get => OpenAiApi?.ApiKey ?? _apiKey;
        set
        {
            _apiKey = value;
            if (OpenAiApi != null) OpenAiApi.ApiKey = value;
        }
    }
}

/// <summary>
/// 索引配置
/// </summary>
public class IndexingConfig
{
    /// <summary>
    /// 是否启用自动索引
    /// </summary>
    public bool AutoIndexEnabled { get; set; } = true;

    /// <summary>
    /// 防抖延迟（毫秒）
    /// 用于合并短时间内多次文件变动事件
    /// </summary>
    public int DebounceMs { get; set; } = 500;

    /// <summary>
    /// 启动时是否同步索引
    /// </summary>
    public bool SyncOnStartup { get; set; } = true;

    /// <summary>
    /// 文件处理最大重试次数
    /// </summary>
    public int MaxFileRetries { get; set; } = 3;

    /// <summary>
    /// 上下文窗口大小（用于搜索结果的上下文展示）
    /// </summary>
    public int ContextWindowSize { get; set; } = 1;
}

/// <summary>
/// 模型中心与下载配置（HuggingFace / 国内镜像源）
/// </summary>
public class ModelHubConfig
{
    /// <summary>
    /// Hugging Face API 访问令牌 (User Access Token，用于访问 Gated 协议限制或私有模型)
    /// </summary>
    public string? HfToken { get; set; }

    /// <summary>
    /// Hugging Face 端点/镜像源地址（默认 https://hf-mirror.com）
    /// </summary>
    public string HfEndpoint { get; set; } = "https://hf-mirror.com";

    /// <summary>
    /// 元数据与下载连接超时时间（秒，默认 30）
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// 本地 GGUF 对话模型 Profile
/// </summary>
public class LocalGgufChatProfile
{
    public string ModelName { get; set; } = "Qwen2.5-7B-Instruct-GGUF";
    public string? Variant { get; set; } = "qwen2.5-7b-instruct-q4_k_m.gguf";
    public string? ModelPath { get; set; }

    private string _performanceMode = "Adaptive";
    private bool _isExplicitModeSet;

    /// <summary>
    /// 性能规划模式：Adaptive（自适应规划 - 智能推荐）| Fixed（固定参数 - 手动精调）
    /// </summary>
    public string PerformanceMode
    {
        get
        {
            if (_isExplicitModeSet)
                return _performanceMode;
            if (ContextSize > 0)
                return "Fixed";
            return _performanceMode;
        }
        set
        {
            _performanceMode = value;
            _isExplicitModeSet = !string.IsNullOrWhiteSpace(value);
        }
    }

    /// <summary>
    /// 自适应策略类型：Balanced（通用平衡）| HighThroughput（高吞吐）| LongContext（长窗口）| LowMemory（轻量低显存）
    /// </summary>
    public string AdaptiveStrategy { get; set; } = "Balanced";

    public int GpuLayers { get; set; } = -1;
    public int ContextSize { get; set; } = 0; // 0 表示自适应
    public int BatchSize { get; set; } = 0;   // 0 表示自适应
    public int UBatchSize { get; set; } = 0;  // 0 表示自适应

    /// <summary>重复惩罚因子重载（为 null 时继承全局配置）</summary>
    public float? RepeatPenalty { get; set; }
    /// <summary>频率惩罚因子重载（为 null 时继承全局配置）</summary>
    public float? FrequencyPenalty { get; set; }
    /// <summary>存在惩罚因子重载（为 null 时继承全局配置）</summary>
    public float? PresencePenalty { get; set; }
    /// <summary>惩罚 Token 窗口重载（为 null 时继承全局配置）</summary>
    public int? PenaltyCount { get; set; }

    public HardwareAccelerationConfig Acceleration { get; set; } = new();
}

/// <summary>
/// 本地 ONNX GenAI 对话模型 Profile
/// </summary>
public class LocalOnnxGenAiChatProfile
{
    public string ModelName { get; set; } = "Phi-3.5-mini-instruct-onnx";
    public string? ModelPath { get; set; }
    public HardwareAccelerationConfig Acceleration { get; set; } = new();
}

/// <summary>
/// OpenAI 兼容 API 对话模型 Profile
/// </summary>
public class OpenAiApiChatProfile
{
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-4o-mini";
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 是否已配置有效密钥（脱敏传输标志）
    /// </summary>
    public bool ApiKeyConfigured { get; set; }
}

/// <summary>
/// 聊天对话模型配置
/// </summary>
public class ChatModelConfig
{
    /// <summary>当前激活后端方案：LocalGguf | LocalOnnxGenAi | OpenAiApi</summary>
    public string ActiveBackend { get; set; } = "OpenAiApi";

    public LocalGgufChatProfile LocalGguf { get; set; } = new();
    public LocalOnnxGenAiChatProfile LocalOnnxGenAi { get; set; } = new();
    public OpenAiApiChatProfile OpenAiApi { get; set; } = new();

    /// <summary>是否开启意图识别与语义路由（开启后自动进行意图预判与动态工具优化）</summary>
    public bool EnableIntentRouting { get; set; } = true;

    public string SystemPrompt { get; set; } = "你是 ReferenceRAG 智能助手。";
    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.9f;

    /// <summary>重复惩罚因子（1.0 为无惩罚，推荐 1.05~1.15）</summary>
    public float RepeatPenalty { get; set; } = 1.1f;

    /// <summary>频率惩罚因子（基于 token 出现频次施加线性惩罚，推荐 0.0~0.5）</summary>
    public float FrequencyPenalty { get; set; } = 0.2f;

    /// <summary>存在惩罚因子（只要 token 出现过就施加固定惩罚，推荐 0.0~0.3）</summary>
    public float PresencePenalty { get; set; } = 0.1f;

    /// <summary>重复惩罚考虑的最近 Token 数量（默认 128）</summary>
    public int PenaltyCount { get; set; } = 128;

    /// <summary>是否开启流式实时重复熔断器（检测到自回归死循环时自动截断，避免算力浪费与无限刷屏）</summary>
    public bool EnableRepetitionCircuitBreaker { get; set; } = true;

    public int IdleTimeoutSeconds { get; set; } = 300;

    /// <summary>API 端点地址</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Endpoint
    {
        get => OpenAiApi?.Endpoint ?? "";
        set
        {
            if (OpenAiApi != null) OpenAiApi.Endpoint = value;
        }
    }

    /// <summary>API Key</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ApiKey
    {
        get => OpenAiApi?.ApiKey ?? "";
        set
        {
            if (OpenAiApi != null) OpenAiApi.ApiKey = value;
        }
    }

    /// <summary>模型名称</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Model
    {
        get
        {
            if (ActiveBackend == "LocalGguf") return LocalGguf?.ModelName ?? "";
            if (ActiveBackend == "LocalOnnxGenAi") return LocalOnnxGenAi?.ModelName ?? "";
            return OpenAiApi?.Model ?? "";
        }
        set
        {
            if (ActiveBackend == "LocalGguf" && LocalGguf != null) LocalGguf.ModelName = value;
            else if (ActiveBackend == "LocalOnnxGenAi" && LocalOnnxGenAi != null) LocalOnnxGenAi.ModelName = value;
            else if (OpenAiApi != null) OpenAiApi.Model = value;
        }
    }
}

