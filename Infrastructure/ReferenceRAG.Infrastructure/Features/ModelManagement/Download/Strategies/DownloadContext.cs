using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Adapters;

namespace ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Strategies;

/// <summary>
/// 模型下载执行上下文
/// </summary>
public class DownloadContext
{
    public string ModelName { get; set; } = string.Empty;
    public string TargetDir { get; set; } = string.Empty;
    public IModelSourceAdapter SourceAdapter { get; set; } = default!;
    public ModelInfo? ModelInfo { get; set; }
    public string? OnnxVariantPath { get; set; }
    public string? TargetFormat { get; set; } // "embedded" | "external"
    public string? SourceRepository { get; set; }

    /// <summary>
    /// 镜像源探查器工厂（在需要探查社区镜像时使用）
    /// </summary>
    public Func<string, Task<IModelSourceAdapter?>>? SourceAdapterResolver { get; set; }

    /// <summary>
    /// Python 与 Optimum 环境是否可用
    /// </summary>
    public bool IsPythonAvailable { get; set; }
}
