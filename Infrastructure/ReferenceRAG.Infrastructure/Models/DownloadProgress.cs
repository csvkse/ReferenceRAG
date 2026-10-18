namespace ReferenceRAG.Core.Models;

/// <summary>
/// 下载错误码
/// </summary>
public enum DownloadErrorCode
{
    None = 0,
    ModelNotFound = 1,          // 模型不存在
    AlreadyDownloading = 2,     // 已在下载中
    FileIncomplete = 3,         // 文件下载不完整
    ConversionFailed = 4,       // ONNX转换失败
    PythonNotFound = 5,         // Python环境不可用
    DependenciesMissing = 6,     // Python依赖缺失
    ModelTooLargeForEmbedded = 7,// 模型过大不适合嵌入式
    NetworkError = 8,           // 网络错误
    StorageError = 9,           // 存储错误
    AuthorizationRequired = 10, // 需要授权/Token (401/403)
    Unknown = 99                 // 未知错误
}

/// <summary>
/// ONNX 模型文件选项（用于让用户选择下载哪个版本）
/// </summary>
public class OnnxFileOption
{
    /// <summary>
    /// 文件路径 (如: model.onnx, onnx/model.onnx, onnx/model_O1.onnx)
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称 (如: 标准版本, INT8量化, O4优化)
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 描述信息
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 文件大小 (字节)
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// 是否为量化版本
    /// </summary>
    public bool IsQuantized { get; set; }

    /// <summary>
    /// 目标硬件平台 (如: arm64, avx512, avx2)
    /// </summary>
    public string? TargetPlatform { get; set; }

    /// <summary>
    /// 是否为外部数据格式（有配套的 .data 文件）
    /// </summary>
    public bool HasExternalData { get; set; }

    /// <summary>
    /// 外部数据文件路径（如果有）
    /// </summary>
    public string? ExternalDataPath { get; set; }

    /// <summary>
    /// 是否在子目录中
    /// </summary>
    public bool IsInSubfolder { get; set; }

    /// <summary>
    /// 是否推荐使用
    /// </summary>
    public bool IsRecommended { get; set; }

    /// <summary>
    /// 文件格式: "onnx" 或 "gguf"
    /// </summary>
    public string Format { get; set; } = "onnx";

    /// <summary>
    /// 来源模型仓库（主仓库或社区候选源）
    /// </summary>
    public string? Repository { get; set; }
}

/// <summary>
/// 模型下载选项（包含所有可用的下载方式）
/// </summary>
public class ModelDownloadOptions
{
    /// <summary>
    /// 模型名称
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 是否有预转换的 ONNX 文件
    /// </summary>
    public bool HasOnnx { get; set; }

    /// <summary>
    /// 是否有可用的 GGUF 格式文件 (可供 LLamaSharp 原生加载)
    /// </summary>
    public bool HasGguf { get; set; }

    /// <summary>
    /// 是否同时支持 GGUF 与 ONNX 两种格式供用户选择
    /// </summary>
    public bool HasBothFormats => HasGguf && HasOnnx;

    /// <summary>
    /// 是否需要从 PyTorch 转换
    /// </summary>
    public bool NeedsConversion { get; set; }

    /// <summary>
    /// 是否为受限/需授权仓库 (Hugging Face Gated Model, HTTP 401/403)
    /// </summary>
    public bool IsGatedOrUnauthorized { get; set; }

    /// <summary>
    /// GGUF 格式文件选项
    /// </summary>
    public List<OnnxFileOption> GgufOptions { get; set; } = new();

    /// <summary>
    /// ONNX 格式文件选项
    /// </summary>
    public List<OnnxFileOption> OnnxOptions { get; set; } = new();

    /// <summary>
    /// 根目录的 ONNX/GGUF 文件选项
    /// </summary>
    public List<OnnxFileOption> RootOptions { get; set; } = new();

    /// <summary>
    /// 子目录（onnx/）的 ONNX 文件选项
    /// </summary>
    public List<OnnxFileOption> SubfolderOptions { get; set; } = new();

    /// <summary>
    /// 所有可用的选项（合并列表，方便前端展示）
    /// </summary>
    public List<OnnxFileOption> AllOptions { get; set; } = new();

    /// <summary>
    /// 是否需要用户选择（有多个选项时）
    /// </summary>
    public bool NeedsUserSelection => AllOptions.Count > 1;

    /// <summary>
    /// 推荐的选项
    /// </summary>
    public OnnxFileOption? RecommendedOption { get; set; }

    /// <summary>
    /// 预估的模型大小（用于大模型警告）
    /// </summary>
    public long EstimatedSize { get; set; }
}

/// <summary>
/// 模型下载全生命周期细粒度阶段
/// </summary>
public enum DownloadStage
{
    Idle = 0,               // 空闲状态
    ResolvingManifest = 1,  // 正在解析模型清单与元数据
    ProbingMirrors = 2,     // 正在探测社区免转镜像源 (ONNX/GGUF)
    DownloadingFiles = 3,   // 正在传输模型权重及配置文件
    VerifyingFiles = 4,     // 正在校验下载文件结构与完整性
    Converting = 5,         // 正在执行格式转换与优化 (PyTorch -> ONNX / 权重合并)
    Completed = 6,          // 下载与就绪全部完成
    Failed = 7,             // 下载或转换过程失败
    Cancelled = 8           // 用户主动取消
}

/// <summary>
/// 下载进度状态
/// </summary>
public class DownloadProgress
{
    public string ModelName { get; set; } = string.Empty;
    public string Status { get; set; } = "idle"; // idle, downloading, completed, failed, cancelled

    /// <summary>
    /// 细粒度下载阶段
    /// </summary>
    public DownloadStage Stage { get; set; } = DownloadStage.Idle;

    /// <summary>
    /// 阶段详细描述文本（面向前端展示，例如：“正在从 onnx-community 镜像下载 model.onnx (125.4 MB / 612.0 MB)...”）
    /// </summary>
    public string StageMessage { get; set; } = string.Empty;

    /// <summary>
    /// 当前正在传输的文件名称
    /// </summary>
    public string? CurrentFileName { get; set; }

    /// <summary>
    /// 当前文件序号 (从 1 开始)
    /// </summary>
    public int CurrentFileIndex { get; set; }

    /// <summary>
    /// 总文件数
    /// </summary>
    public int TotalFiles { get; set; }

    /// <summary>
    /// 当前文件已传输字节数
    /// </summary>
    public long CurrentFileBytes { get; set; }

    /// <summary>
    /// 当前文件总字节数
    /// </summary>
    public long CurrentFileTotalBytes { get; set; }

    /// <summary>
    /// 综合进度百分比 (0-100)
    /// </summary>
    public float Progress { get; set; }

    /// <summary>
    /// 累计已传输总字节数
    /// </summary>
    public long BytesReceived { get; set; }

    /// <summary>
    /// 准确总字节数
    /// </summary>
    public long TotalBytes { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 错误码，用于前端显示上下文帮助
    /// </summary>
    public DownloadErrorCode ErrorCode { get; set; } = DownloadErrorCode.None;

    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// 下载速度 (bytes/s)
    /// </summary>
    public double SpeedBytesPerSecond { get; set; }

    /// <summary>
    /// 预计剩余时间 (秒)
    /// </summary>
    public int? EstimatedSecondsRemaining { get; set; }

    /// <summary>
    /// 可用的下载选项（用于让用户选择）
    /// </summary>
    public ModelDownloadOptions? DownloadOptions { get; set; }
}
