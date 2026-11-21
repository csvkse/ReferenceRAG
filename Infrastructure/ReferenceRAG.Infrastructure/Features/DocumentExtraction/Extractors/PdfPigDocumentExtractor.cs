using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;
using ReferenceRAG.Core.Features.DocumentExtraction.Guards;
using ReferenceRAG.Core.Features.DocumentExtraction.Strategies;
using UglyToad.PdfPig;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Extractors;

/// <summary>
/// 基于 UglyToad.PdfPig 的专业 PDF 内容抽取器（Apache 2.0 纯托管实现）
/// 支持电子 PDF 文本抽取、版面页码组织以及扫描件启发式密度检测
/// </summary>
public sealed class PdfPigDocumentExtractor : IDocumentContentExtractor
{
    private readonly IScannedPdfHandlingStrategy _scannedStrategy;
    private readonly ILogger<PdfPigDocumentExtractor>? _logger;

    public PdfPigDocumentExtractor(
        IScannedPdfHandlingStrategy? scannedStrategy = null,
        ILogger<PdfPigDocumentExtractor>? logger = null)
    {
        _scannedStrategy = scannedStrategy ?? new GracefulDegradeScannedStrategy();
        _logger = logger;
    }

    public bool CanHandle(string extension, ReadOnlySpan<byte> magicHeader)
    {
        if (BinaryMagicGuard.IsPdf(magicHeader)) return true;
        return string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<DocumentExtractResult> ExtractAsync(
        Stream stream,
        string sourceKey,
        DocumentExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        try
        {
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            if (ms.Length == 0)
            {
                return new DocumentExtractResult(
                    Status: DocumentParseStatus.EmptyDocument,
                    ExtractedText: string.Empty,
                    DiagnosticMessage: "PDF 文件流为空 (0 字节)"
                );
            }

            ms.Position = 0;
            using var document = PdfDocument.Open(ms);
            int pageCount = document.NumberOfPages;
            if (pageCount == 0)
            {
                return new DocumentExtractResult(
                    Status: DocumentParseStatus.EmptyDocument,
                    ExtractedText: string.Empty,
                    PageCount: 0,
                    DiagnosticMessage: "PDF 页面数量为 0"
                );
            }

            var sb = new StringBuilder();
            int totalLetters = 0;
            int pagesWithImages = 0;

            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var letters = page.Letters;
                int letterCount = letters?.Count ?? 0;
                totalLetters += letterCount;

                bool hasImages = false;
                try
                {
                    hasImages = page.GetImages().Any();
                }
                catch
                {
                    // 容错处理异常图像流解析
                }

                if (hasImages)
                {
                    pagesWithImages++;
                }

                var text = page.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine($"<!-- Page {page.Number} -->");
                    sb.AppendLine(text.Trim());
                    sb.AppendLine();
                }
            }

            double avgLettersPerPage = pageCount > 0 ? (double)totalLetters / pageCount : 0;

            // 1. 空白文档判定（既无文字也无图像）
            if (totalLetters == 0 && pagesWithImages == 0)
            {
                return new DocumentExtractResult(
                    Status: DocumentParseStatus.EmptyDocument,
                    ExtractedText: string.Empty,
                    PageCount: pageCount,
                    ExtractedCharCount: 0,
                    IsScannedPdf: false,
                    DiagnosticMessage: "PDF 文档内容为空（未检测到有效文本或页面图像）"
                );
            }

            // 2. 扫描件启发式判定门禁 (G-PDF-DENSITY-03)：
            // - 纯图片扫描件：全书文字数为 0 且包含光栅图像（如《新政治学概要》等363页纯图）；
            // - 稀疏文字扫描件：存在图像，且全书总文字极少 (< 200 字) 且平均每页文字 < 15 字
            // 注意：若全书文字量丰富（如《资本论》含95.5万字），即便每页都包含光栅插图或背景图，也严禁判为扫描件
            bool isScanned = (totalLetters == 0 && pagesWithImages > 0)
                || (pagesWithImages > 0 && totalLetters < 200 && avgLettersPerPage < 15);

            if (isScanned)
            {
                _logger?.LogInformation("[PdfPigDocumentExtractor] 判定为扫描版 PDF: {Key}, 总页数={Pages}, 总字数={Letters}, 含图页数={Images}",
                    sourceKey, pageCount, totalLetters, pagesWithImages);
                return await _scannedStrategy.HandleAsync(document, sourceKey, options, ct);
            }

            string extractedText = sb.ToString();
            return new DocumentExtractResult(
                Status: DocumentParseStatus.Success,
                ExtractedText: extractedText,
                PageCount: pageCount,
                ExtractedCharCount: totalLetters,
                IsScannedPdf: false
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[PdfPigDocumentExtractor] PDF 解析异常: {Key}", sourceKey);
            return new DocumentExtractResult(
                Status: DocumentParseStatus.ExtractionFailed,
                ExtractedText: string.Empty,
                PageCount: 0,
                ExtractedCharCount: 0,
                IsScannedPdf: false,
                DiagnosticMessage: $"PDF 解析失败: {ex.Message}"
            );
        }
    }
}
