using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;
using ReferenceRAG.Core.Features.DocumentExtraction.Engines;
using ReferenceRAG.Core.Features.DocumentExtraction.Extractors;
using ReferenceRAG.Core.Features.DocumentExtraction.Services;
using ReferenceRAG.Core.Features.DocumentExtraction.Strategies;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace ReferenceRAG.Tests;

public class PdfOcrAndStrategyGateTests
{
    private static readonly byte[] TinyPngBytes = [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    private static byte[] CreateScannedPdfBytes()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        page.AddPng(TinyPngBytes, new PdfRectangle(50, 50, 200, 200));
        return builder.Build();
    }

    private static byte[] CreateHybridPdfBytes(string text)
    {
        var builder = new PdfDocumentBuilder();
        // Page 1: Digital text
        var p1 = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        p1.AddText(text, 12, new PdfPoint(50, 700), font);

        // Page 2: Scanned image only
        var p2 = builder.AddPage(PageSize.A4);
        p2.AddPng(TinyPngBytes, new PdfRectangle(50, 50, 200, 200));
        return builder.Build();
    }

    [Fact]
    public void DocumentExtractionOptions_DefaultsAndFactories_AreCorrect()
    {
        var defaultOpt = DocumentExtractionOptions.Default;
        Assert.False(defaultOpt.EnableOcr);
        Assert.Null(defaultOpt.Progress);

        var ocrOpt = DocumentExtractionOptions.WithOcr();
        Assert.True(ocrOpt.EnableOcr);
        Assert.Null(ocrOpt.Progress);

        var progress = new Progress<OcrProgressInfo>();
        var ocrWithProg = DocumentExtractionOptions.WithOcr(progress);
        Assert.True(ocrWithProg.EnableOcr);
        Assert.Same(progress, ocrWithProg.Progress);
    }

    [Fact]
    public async Task OnnxOcrScannedStrategy_WhenOcrDisabled_PerformsFastSkip()
    {
        var mockEngine = new MockOcrEngine(isAvailable: true);
        var strategy = new OnnxOcrScannedStrategy(mockEngine);

        var pdfBytes = CreateScannedPdfBytes();
        using var pdfDoc = PdfDocument.Open(pdfBytes);

        // Options is null or EnableOcr = false
        var res1 = await strategy.HandleAsync(pdfDoc, "scanned.pdf", null);
        Assert.Equal(DocumentParseStatus.ScannedRequiresOcr, res1.Status);
        Assert.True(res1.IsScannedPdf);
        Assert.Empty(res1.ExtractedText);

        var res2 = await strategy.HandleAsync(pdfDoc, "scanned.pdf", DocumentExtractionOptions.Default);
        Assert.Equal(DocumentParseStatus.ScannedRequiresOcr, res2.Status);
        Assert.True(res2.IsScannedPdf);
    }

    [Fact]
    public async Task OnnxOcrScannedStrategy_WhenOcrEnabled_NoEngineAvailable_ReturnsScannedRequiresOcr()
    {
        var mockEngine = new MockOcrEngine(isAvailable: false);
        var strategy = new OnnxOcrScannedStrategy(mockEngine);

        var pdfBytes = CreateScannedPdfBytes();
        using var pdfDoc = PdfDocument.Open(pdfBytes);

        var res = await strategy.HandleAsync(pdfDoc, "scanned.pdf", DocumentExtractionOptions.WithOcr());
        Assert.Equal(DocumentParseStatus.ScannedRequiresOcr, res.Status);
        Assert.True(res.IsScannedPdf);
        Assert.Contains("尚未装配端侧 OCR 模型", res.DiagnosticMessage);
    }

    [Fact]
    public async Task OnnxOcrScannedStrategy_WhenOcrEnabled_WithAvailableEngine_PerformsRecognition()
    {
        var mockEngine = new MockOcrEngine(isAvailable: true, _ => "识别出的第一页文本内容。");
        var strategy = new OnnxOcrScannedStrategy(mockEngine);

        var pdfBytes = CreateScannedPdfBytes();
        using var pdfDoc = PdfDocument.Open(pdfBytes);

        var progressReports = new List<OcrProgressInfo>();
        var progress = new Progress<OcrProgressInfo>(info => progressReports.Add(info));

        var res = await strategy.HandleAsync(pdfDoc, "scanned.pdf", DocumentExtractionOptions.WithOcr(progress));

        Assert.Equal(DocumentParseStatus.Success, res.Status);
        Assert.True(res.IsScannedPdf);
        Assert.Contains("识别出的第一页文本内容。", res.ExtractedText);
        Assert.Contains("<!-- Page 1 (OCR) -->", res.ExtractedText);
        Assert.True(res.ExtractedCharCount > 0);
    }

    [Fact]
    public async Task OnnxOcrScannedStrategy_HybridDocument_ExtractsDigitalAndRunsOcrOnImages()
    {
        var mockEngine = new MockOcrEngine(isAvailable: true, _ => "第二页OCR提取的文本。");
        var strategy = new OnnxOcrScannedStrategy(mockEngine);

        string digitalText = "This is a digital page with enough characters to pass the threshold of thirty letters.";
        var pdfBytes = CreateHybridPdfBytes(digitalText);
        using var pdfDoc = PdfDocument.Open(pdfBytes);

        var res = await strategy.HandleAsync(pdfDoc, "hybrid.pdf", DocumentExtractionOptions.WithOcr());

        Assert.Equal(DocumentParseStatus.Success, res.Status);
        Assert.Contains(digitalText, res.ExtractedText);
        Assert.Contains("第二页OCR提取的文本。", res.ExtractedText);
        Assert.Contains("<!-- Page 1 -->", res.ExtractedText);
        Assert.Contains("<!-- Page 2 (OCR) -->", res.ExtractedText);
    }

    [Fact]
    public async Task CompositeOcrEngine_PicksFirstAvailableEngine()
    {
        var engine1 = new MockOcrEngine(isAvailable: false);
        var engine2 = new MockOcrEngine(isAvailable: true, _ => "From Engine 2");
        var composite = new CompositeOcrEngine(new[] { engine1, engine2 });

        Assert.True(composite.IsAvailable);
        Assert.Contains("MockOcrEngine", composite.EngineName);

        var res = await composite.RecognizeImageAsync(new byte[10]);
        Assert.Equal("From Engine 2", res.Text);
    }

    [Fact]
    public async Task DocumentExtractorRouter_WithOcrOptions_RoutesProperly()
    {
        var mockEngine = new MockOcrEngine(isAvailable: true, _ => "文档路由器OCR文字");
        var scannedStrategy = new OnnxOcrScannedStrategy(mockEngine);
        var extractor = new PdfPigDocumentExtractor(scannedStrategy);
        var router = new DocumentExtractorRouter(new[] { extractor });

        var pdfBytes = CreateScannedPdfBytes();
        using var stream = new MemoryStream(pdfBytes);

        // 1. Without OCR -> ScannedRequiresOcr
        var resSkip = await router.ExtractAsync(stream, "test.pdf", DocumentExtractionOptions.Default);
        Assert.Equal(DocumentParseStatus.ScannedRequiresOcr, resSkip.Status);

        // 2. With OCR -> Success
        stream.Position = 0;
        var resOcr = await router.ExtractAsync(stream, "test.pdf", DocumentExtractionOptions.WithOcr());
        Assert.Equal(DocumentParseStatus.Success, resOcr.Status);
        Assert.Contains("文档路由器OCR文字", resOcr.ExtractedText);
    }
}
