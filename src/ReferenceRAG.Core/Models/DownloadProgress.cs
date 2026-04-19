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
    /// 是否需要从 PyTorch 转换
    /// </summary>
    public bool NeedsConversion { get; set; }

    /// <summary>
    /// 根目录的 ONNX 文件选项
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
/// 下载进度状态
/// </summary>
public class DownloadProgress
{
    public string ModelName { get; set; } = string.Empty;
    public string Status { get; set; } = "idle"; // idle, downloading, completed, failed
    public float Progress { get; set; } // 0-100
    public long BytesReceived { get; set; }
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
