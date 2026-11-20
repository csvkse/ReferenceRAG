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

    public async Task<DocumentExtractResult> ExtractAsync(Stream stream, string sourceKey, CancellationToken ct = default)
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

            // 扫描件启发式判定门禁 (G-PDF-DENSITY-03)：
            // 若整本 PDF 提取字符为 0，或平均每页有效文字 < 15 且存在光栅图像，则认定为扫描版 PDF
            bool isScanned = (avgLettersPerPage < 15 && pagesWithImages > 0) || totalLetters == 0;

            if (isScanned)
            {
                _logger?.LogInformation("[PdfPigDocumentExtractor] 判定为扫描版 PDF: {Key}, 总页数={Pages}, 总字数={Letters}, 含图页数={Images}",
                    sourceKey, pageCount, totalLetters, pagesWithImages);
                return await _scannedStrategy.HandleAsync(document, sourceKey, ct);
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[PdfPigDocumentExtractor] PDF 解析异常: {Key}", sourceKey);
            return new DocumentExtractResult(
                Status: DocumentParseStatus.ExtractionFailed,
                ExtractedText: string.Empty,
                DiagnosticMessage: $"PDF 解析失败: {ex.Message}"
            );
        }
    }
}
