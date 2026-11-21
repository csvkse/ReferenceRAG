using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;
using UglyToad.PdfPig;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Strategies;

/// <summary>
/// 基于端侧 OCR 引擎的扫描件与图片型 PDF 处置策略（阶段二）
/// 当注入的 IOcrInferenceEngine 可用时，遍历页面提取图片并调用 OCR 转录；
/// 若引擎未配置或不可用，自动平滑退避至 GracefulDegradeScannedStrategy，返回 ScannedRequiresOcr
/// </summary>
public sealed class OnnxOcrScannedStrategy : IScannedPdfHandlingStrategy
{
    private readonly IOcrInferenceEngine? _ocrEngine;
    private readonly GracefulDegradeScannedStrategy _fallbackStrategy;
    private readonly ILogger<OnnxOcrScannedStrategy>? _logger;

    public OnnxOcrScannedStrategy(
        IOcrInferenceEngine? ocrEngine = null,
        ILogger<OnnxOcrScannedStrategy>? logger = null,
        ILogger<GracefulDegradeScannedStrategy>? fallbackLogger = null)
    {
        _ocrEngine = ocrEngine;
        _fallbackStrategy = new GracefulDegradeScannedStrategy(fallbackLogger);
        _logger = logger;
    }

    public async Task<DocumentExtractResult> HandleAsync(PdfDocument document, string sourceKey, CancellationToken ct = default)
    {
        if (_ocrEngine == null || !_ocrEngine.IsAvailable)
        {
            _logger?.LogInformation("[OnnxOcrScannedStrategy] OCR 引擎未注册或不可用，安全回退至降级跳过策略: {Key}", sourceKey);
            return await _fallbackStrategy.HandleAsync(document, sourceKey, ct);
        }

        _logger?.LogInformation("[OnnxOcrScannedStrategy] 启动端侧 OCR 页面图片识别: {Key}, 总页数={Pages}", sourceKey, document.NumberOfPages);
        var sb = new StringBuilder();
        int totalExtractedChars = 0;
        int ocrPagesCount = 0;

        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var images = page.GetImages().ToList();
            if (images.Count == 0) continue;

            sb.AppendLine($"<!-- Page {page.Number} (OCR) -->");
            foreach (var img in images)
            {
                ct.ThrowIfCancellationRequested();
                byte[] rawBytes;
                if (img.TryGetPng(out var pngBytes))
                {
                    rawBytes = pngBytes;
                }
                else
                {
                    rawBytes = img.RawBytes.ToArray();
                }

                if (rawBytes.Length == 0) continue;

                var ocrRes = await _ocrEngine.RecognizeImageAsync(rawBytes, ct);
                if (!string.IsNullOrWhiteSpace(ocrRes.Text))
                {
                    sb.AppendLine(ocrRes.Text.Trim());
                    totalExtractedChars += ocrRes.Text.Length;
                    ocrPagesCount++;
                }
            }
            sb.AppendLine();
        }

        if (totalExtractedChars == 0)
        {
            return new DocumentExtractResult(
                Status: DocumentParseStatus.EmptyDocument,
                ExtractedText: string.Empty,
                PageCount: document.NumberOfPages,
                ExtractedCharCount: 0,
                IsScannedPdf: true,
                DiagnosticMessage: "OCR 识别完成，但未从页面图像中检测到有效文字"
            );
        }

        return new DocumentExtractResult(
            Status: DocumentParseStatus.Success,
            ExtractedText: sb.ToString(),
            PageCount: document.NumberOfPages,
            ExtractedCharCount: totalExtractedChars,
            IsScannedPdf: true,
            DiagnosticMessage: $"端侧 OCR 识别成功，共识别 {ocrPagesCount} 页、{totalExtractedChars} 字符"
        );
    }
}
