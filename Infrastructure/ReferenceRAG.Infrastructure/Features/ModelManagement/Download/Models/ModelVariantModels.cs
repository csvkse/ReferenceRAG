namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Models;

/// <summary>
/// 社区模型变种搜索条目
/// </summary>
public class ModelVariantSearchResult
{
    /// <summary>
    /// 仓库唯一标识 (例如: "bartowski/Qwen2.5-Coder-7B-Instruct-GGUF" 或 "onnx-community/bge-m3")
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 作者或组织 (例如: "bartowski", "onnx-community")
    /// </summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// 模型简要名称
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// 社区下载次数
    /// </summary>
    public long Downloads { get; set; }

    /// <summary>
    /// 社区标星 / Likes 数
    /// </summary>
    public int Likes { get; set; }

    /// <summary>
    /// 最近更新时间
    /// </summary>
    public DateTime? LastModified { get; set; }

    /// <summary>
    /// 任务管线分类 (例如: "feature-extraction", "text-classification")
    /// </summary>
    public string PipelineTag { get; set; } = string.Empty;

    /// <summary>
    /// 标签集合
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 是否包含 GGUF 格式文件或标记
    /// </summary>
    public bool HasGguf { get; set; }

    /// <summary>
    /// 是否包含 ONNX 格式文件或标记
    /// </summary>
    public bool HasOnnx { get; set; }

    /// <summary>
    /// 是否为私有仓库
    /// </summary>
    public bool IsPrivate { get; set; }

    /// <summary>
    /// 模型描述或摘要
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// 社区变种文件条目
/// </summary>
public class ModelVariantFileOption
{
    /// <summary>
    /// 仓库内文件路径 (例如 "qwen3-embedding-0.6b-q4_k_m.gguf", "onnx/model.onnx")
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 文件名
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 格式: "gguf" 或 "onnx"
    /// </summary>
    public string Format { get; set; } = "gguf";

    /// <summary>
    /// 量化类型标签 (例如 "Q4_K_M", "Q8_0", "INT8", "FP16")
    /// </summary>
    public string? Quantization { get; set; }

    /// <summary>
    /// 是否推荐使用
    /// </summary>
    public bool IsRecommended { get; set; }

    /// <summary>
    /// 描述文本
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// 社区变种详情
/// </summary>
public class ModelVariantDetail
{
    /// <summary>
    /// 仓库唯一标识
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// 作者或组织
    /// </summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// 社区下载量
    /// </summary>
    public long Downloads { get; set; }

    /// <summary>
    /// 社区 Likes 数
    /// </summary>
    public int Likes { get; set; }

    /// <summary>
    /// 最近更新时间
    /// </summary>
    public DateTime? LastModified { get; set; }

    /// <summary>
    /// 任务管线分类
    /// </summary>
    public string PipelineTag { get; set; } = string.Empty;

    /// <summary>
    /// 标签集合
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// 来源镜像端点
    /// </summary>
    public string SourceEndpoint { get; set; } = string.Empty;

    /// <summary>
    /// 是否包含 GGUF 格式文件
    /// </summary>
    public bool HasGguf { get; set; }

    /// <summary>
    /// 是否包含 ONNX 格式文件
    /// </summary>
    public bool HasOnnx { get; set; }

    /// <summary>
    /// 包含的所有 GGUF 变体文件
    /// </summary>
    public List<ModelVariantFileOption> GgufFiles { get; set; } = new();

    /// <summary>
    /// 包含的所有 ONNX 变体文件
    /// </summary>
    public List<ModelVariantFileOption> OnnxFiles { get; set; } = new();

    /// <summary>
    /// 仓库内文件总数
    /// </summary>
    public int TotalFilesCount { get; set; }

    /// <summary>
    /// 仓库页面 URL (例如 "https://hf-mirror.com/Qwen/Qwen3-Embedding-0.6B-GGUF")
    /// </summary>
    public string WebUrl { get; set; } = string.Empty;

    /// <summary>
    /// 描述说明
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
