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
/// 基于端侧与云端 OCR 推理引擎的按需扫描件与混合型 PDF 处置策略（策略模式）
/// 1. 默认状态 (EnableOcr=false) 时秒级降级跳过，保护全库同步吞吐；
/// 2. 按需状态 (EnableOcr=true) 时，逐页智能识别：
///    - 若页面已具有足够数字文本层（如双层搜索 PDF），直接提取文字（0ms，不调用 OCR）；
///    - 若页面为纯图像，调用注入的 IOcrInferenceEngine 进行端侧/云端文字转录，并通过 IProgress 上报页级进度；
/// 3. 若引擎未配置或不可用，安全回退至 ScannedRequiresOcr，杜绝假死与垃圾数据。
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

    public async Task<DocumentExtractResult> HandleAsync(
        PdfDocument document,
        string sourceKey,
        DocumentExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        // 1. 若调用方未显式开启 OCR，走默认极速跳过策略
        if (options?.EnableOcr != true)
        {
            _logger?.LogDebug("[OnnxOcrScannedStrategy] 未开启 EnableOcr 选项，执行默认极速跳过: {Key}", sourceKey);
            return await _fallbackStrategy.HandleAsync(document, sourceKey, options, ct);
        }

        // 2. 检查 OCR 引擎是否就绪
        if (_ocrEngine == null || !_ocrEngine.IsAvailable)
        {
            _logger?.LogWarning("[OnnxOcrScannedStrategy] OCR 引擎未装配或不可用，返回扫描件需 OCR 状态: {Key}", sourceKey);
            return new DocumentExtractResult(
                Status: DocumentParseStatus.ScannedRequiresOcr,
                ExtractedText: string.Empty,
                PageCount: document.NumberOfPages,
                ExtractedCharCount: 0,
                IsScannedPdf: true,
                DiagnosticMessage: "文档为扫描版，但当前系统尚未装配端侧 OCR 模型或云端多模态推理服务（需放置 ONNX 模型或配置 VLM 密钥）"
            );
        }

        _logger?.LogInformation("[OnnxOcrScannedStrategy] 启动按需 OCR 转录 (引擎: {Engine}): {Key}, 总页数={Pages}",
            _ocrEngine.EngineName, sourceKey, document.NumberOfPages);

        var sb = new StringBuilder();
        int totalExtractedChars = 0;
        int ocrPagesCount = 0;
        int digitalPagesCount = 0;
        int totalPages = document.NumberOfPages;

        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            // 逐页混合自适应策略 (Hybrid Page Adaptive):
            // 若该页已有足够的矢量文字（如双层搜索 PDF 或混合排版文档），直接使用已有文本，节省 90% 以上算力
            var pageText = page.Text;
            int letterCount = page.Letters?.Count ?? 0;
            if (!string.IsNullOrWhiteSpace(pageText) && letterCount >= 30)
            {
                sb.AppendLine($"<!-- Page {page.Number} -->");
                sb.AppendLine(pageText.Trim());
                sb.AppendLine();
                totalExtractedChars += pageText.Length;
                digitalPagesCount++;

                options.Progress?.Report(new OcrProgressInfo(
                    CurrentPage: page.Number,
                    TotalPages: totalPages,
                    StatusMessage: $"第 {page.Number}/{totalPages} 页已有文本层，快速提取完成",
                    ExtractedChars: totalExtractedChars));
                continue;
            }

            // 图像提取与端侧 OCR 识别
            var images = page.GetImages().ToList();
            if (images.Count == 0)
            {
                options.Progress?.Report(new OcrProgressInfo(
                    CurrentPage: page.Number,
                    TotalPages: totalPages,
                    StatusMessage: $"第 {page.Number}/{totalPages} 页为空白页，已跳过",
                    ExtractedChars: totalExtractedChars));
                continue;
            }

            sb.AppendLine($"<!-- Page {page.Number} (OCR) -->");
            bool pageRecognizedAny = false;

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
                    pageRecognizedAny = true;
                }
            }

            if (pageRecognizedAny)
            {
                ocrPagesCount++;
            }
            sb.AppendLine();

            options.Progress?.Report(new OcrProgressInfo(
                CurrentPage: page.Number,
                TotalPages: totalPages,
                StatusMessage: $"已完成第 {page.Number}/{totalPages} 页 OCR 转录 (累计 {totalExtractedChars} 字)",
                ExtractedChars: totalExtractedChars));
        }

        if (totalExtractedChars == 0)
        {
            return new DocumentExtractResult(
                Status: DocumentParseStatus.EmptyDocument,
                ExtractedText: string.Empty,
                PageCount: totalPages,
                ExtractedCharCount: 0,
                IsScannedPdf: true,
                DiagnosticMessage: $"OCR 识别执行完毕，但在 {totalPages} 页图像中未检出有效文字内容"
            );
        }

        string resultMessage = digitalPagesCount > 0
            ? $"混合自适应 OCR 转录完成：数字提取 {digitalPagesCount} 页，OCR 识别 {ocrPagesCount} 页，共转录 {totalExtractedChars} 字符"
            : $"端侧 OCR 识别成功：共识别 {ocrPagesCount} 页、{totalExtractedChars} 字符（引擎: {_ocrEngine.EngineName}）";

        return new DocumentExtractResult(
            Status: DocumentParseStatus.Success,
            ExtractedText: sb.ToString(),
            PageCount: totalPages,
            ExtractedCharCount: totalExtractedChars,
            IsScannedPdf: true,
            DiagnosticMessage: resultMessage
        );
    }
}
