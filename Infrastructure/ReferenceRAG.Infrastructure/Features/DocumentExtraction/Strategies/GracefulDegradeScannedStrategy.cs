using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;
using UglyToad.PdfPig;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Strategies;

/// <summary>
/// 扫描版 PDF 优雅降级策略（阶段一）
/// 识别无数字文本层的扫描图书或纯图片 PDF，优雅跳过并生成可诊断状态，绝不向向量库或词法库灌入垃圾切片
/// </summary>
public sealed class GracefulDegradeScannedStrategy : IScannedPdfHandlingStrategy
{
    private readonly ILogger<GracefulDegradeScannedStrategy>? _logger;

    public GracefulDegradeScannedStrategy(ILogger<GracefulDegradeScannedStrategy>? logger = null)
    {
        _logger = logger;
    }

    public Task<DocumentExtractResult> HandleAsync(PdfDocument document, string sourceKey, CancellationToken ct = default)
    {
        _logger?.LogWarning("[PdfParser] 文档判定为扫描版/纯图片PDF，当前未开启OCR引擎，执行安全跳过: {Key}", sourceKey);

        return Task.FromResult(new DocumentExtractResult(
            Status: DocumentParseStatus.ScannedRequiresOcr,
            ExtractedText: string.Empty,
            PageCount: document.NumberOfPages,
            ExtractedCharCount: 0,
            IsScannedPdf: true,
            DiagnosticMessage: "该文档为扫描版或图片型PDF，无数字文本层。请配置OCR引擎或提供文字版文档。"
        ));
    }
}
