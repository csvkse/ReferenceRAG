using System;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Domain;

/// <summary>
/// 文档抽取选项参数（支持按需启用端侧 OCR 与进度回调）
/// </summary>
public sealed record DocumentExtractionOptions(
    bool EnableOcr = false,
    IProgress<OcrProgressInfo>? Progress = null)
{
    public static DocumentExtractionOptions Default { get; } = new(EnableOcr: false);
    public static DocumentExtractionOptions WithOcr(IProgress<OcrProgressInfo>? progress = null) => new(EnableOcr: true, Progress: progress);
}
