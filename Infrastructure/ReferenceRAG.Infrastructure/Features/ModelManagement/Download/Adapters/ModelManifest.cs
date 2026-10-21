namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;

/// <summary>
/// 远程仓库中的单个文件元数据
/// </summary>
public class ModelFileItem
{
    /// <summary>
    /// 仓库内相对路径 (例如: "model.onnx", "onnx/model.onnx", "config.json")
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 文件大小 (字节数)，若上游 API 未提供，则可能初始为 0，需通过 HEAD 请求补齐
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// 文件校验哈希 (如有)
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// 获取单纯的文件名
    /// </summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// 是否属于外部权重数据文件 (.onnx.data 或 .onnx_data)
    /// </summary>
    public bool IsExternalData =>
        Path.EndsWith(".onnx.data", StringComparison.OrdinalIgnoreCase) ||
        Path.EndsWith(".onnx_data", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否为 ONNX 主模型文件
    /// </summary>
    public bool IsOnnx => Path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否为 GGUF 模型文件
    /// </summary>
    public bool IsGguf => Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否为多模态视觉投影器/编码器文件 (mmproj)
    /// </summary>
    public bool IsMmproj => IsGguf && FileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否为纯语言文本 GGUF 模型（非伴生视觉投影器）
    /// </summary>
    public bool IsTextGguf => IsGguf && !IsMmproj;

    public override string ToString() => $"{Path} ({Size / 1024.0 / 1024.0:F2} MB)";
}

/// <summary>
/// 模型仓库文件清单
/// </summary>
public class ModelManifest
{
    public string ModelId { get; set; } = string.Empty;
    public string SourceEndpoint { get; set; } = string.Empty;
    public List<ModelFileItem> Files { get; set; } = new();

    public bool HasOnnx => Files.Any(f => f.IsOnnx);
    public bool HasGguf => Files.Any(f => f.IsGguf);
    public bool HasMmproj => Files.Any(f => f.IsMmproj);

    public List<ModelFileItem> GetOnnxFiles() => Files.Where(f => f.IsOnnx).ToList();
    public List<ModelFileItem> GetGgufFiles() => Files.Where(f => f.IsGguf).ToList();
    public List<ModelFileItem> GetTextGgufFiles() => Files.Where(f => f.IsTextGguf).ToList();
    public List<ModelFileItem> GetMmprojFiles() => Files.Where(f => f.IsMmproj).ToList();
}
