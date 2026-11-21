using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;
using ReferenceRAG.Core.Features.DocumentExtraction.Extractors;
using ReferenceRAG.Core.Features.DocumentExtraction.Guards;
using ReferenceRAG.Core.Features.DocumentExtraction.Services;
using ReferenceRAG.Core.Features.DocumentExtraction.Strategies;
using ReferenceRAG.Core.Features.KnowledgeBase.Connectors;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Jobs;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.Storage.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace ReferenceRAG.Tests;

public class PdfParsingAndScannedDocumentGateTests : IDisposable
{
    private readonly string _tempDir;

    public PdfParsingAndScannedDocumentGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "referencerag_pdf_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static readonly byte[] TinyPngBytes = [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    private static byte[] CreateDigitalPdfBytes(string text)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, 12, new PdfPoint(50, 700), font);
        return builder.Build();
    }

    private static byte[] CreateScannedPdfBytes()
    {
        // 构造一个包含真实光栅图片但无任何文本字形的 PDF（真实模拟扫描版 PDF）
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        page.AddPng(TinyPngBytes, new PdfRectangle(50, 50, 200, 200));
        return builder.Build();
    }

    private static byte[] CreateEmptyPdfBytes()
    {
        // 构造一个无文字也无图像的纯空白 PDF
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        return builder.Build();
    }

    [Fact]
    public void Gate1_BinaryMagicGuard_ShouldDetectBinarySignatures()
    {
        // PDF Magic: %PDF-
        byte[] pdfHeader = "%PDF-1.7\r\n"u8.ToArray();
        Assert.True(BinaryMagicGuard.IsPdf(pdfHeader));
        Assert.True(BinaryMagicGuard.IsKnownBinary(pdfHeader));

        // Zip / Docx Magic: PK\x03\x04
        byte[] zipHeader = [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00];
        Assert.False(BinaryMagicGuard.IsPdf(zipHeader));
        Assert.True(BinaryMagicGuard.IsKnownBinary(zipHeader));

        // Null-byte stream
        byte[] nullStream = [0x41, 0x42, 0x00, 0x43];
        Assert.True(BinaryMagicGuard.IsKnownBinary(nullStream));

        // Plain Text Markdown
        byte[] plainMarkdown = "# Title\nThis is normal markdown text."u8.ToArray();
        Assert.False(BinaryMagicGuard.IsPdf(plainMarkdown));
        Assert.False(BinaryMagicGuard.IsKnownBinary(plainMarkdown));
    }

    [Fact]
    public async Task Gate2_PlainTextExtractor_ShouldRejectBinaryAndPdfStreams()
    {
        var extractor = new PlainTextDocumentExtractor();

        byte[] pdfBytes = CreateDigitalPdfBytes("Test plain rejection");
        using var ms = new MemoryStream(pdfBytes);

        // CanHandle 应直接拒接
        Assert.False(extractor.CanHandle(".pdf", pdfBytes.AsSpan(0, 16)));

        // 即使强行调用 ExtractAsync，也必须被内部二进制门禁阻断
        var result = await extractor.ExtractAsync(ms, "corrupted.txt");
        Assert.Equal(DocumentParseStatus.UnsupportedBinary, result.Status);
        Assert.Empty(result.ExtractedText);
    }

    [Fact]
    public async Task Gate3_PdfPigExtractor_ShouldExtractDigitalPdfText()
    {
        var extractor = new PdfPigDocumentExtractor();
        byte[] pdfBytes = CreateDigitalPdfBytes("Hello ReferenceRAG KnowledgeBase");

        using var ms = new MemoryStream(pdfBytes);
        var result = await extractor.ExtractAsync(ms, "digital_document.pdf");

        Assert.Equal(DocumentParseStatus.Success, result.Status);
        Assert.False(result.IsScannedPdf);
        Assert.Equal(1, result.PageCount);
        Assert.Contains("Hello ReferenceRAG KnowledgeBase", result.ExtractedText);
        Assert.Contains("<!-- Page 1 -->", result.ExtractedText);
    }

    [Fact]
    public async Task Gate4_PdfPigExtractor_ShouldDetectScannedPdf_AndDegradeGracefully()
    {
        var degradeStrategy = new GracefulDegradeScannedStrategy();
        var extractor = new PdfPigDocumentExtractor(degradeStrategy);

        byte[] scannedPdfBytes = CreateScannedPdfBytes();
        using var ms = new MemoryStream(scannedPdfBytes);

        var result = await extractor.ExtractAsync(ms, "scanned_book.pdf");

        // 验证状态机与策略生效：状态标记为 ScannedRequiresOcr，文本严格为空
        Assert.Equal(DocumentParseStatus.ScannedRequiresOcr, result.Status);
        Assert.True(result.IsScannedPdf);
        Assert.Empty(result.ExtractedText);
        Assert.NotNull(result.DiagnosticMessage);
    }

    [Fact]
    public void Gate5_NoiseChunkDetector_ShouldCatchPdfSyntaxAndHighCorruptedRatio()
    {
        // 1. 模拟现场泄露的 PDF 容器元语法
        string chunkWithTrailer = "0000002075 00000 n\r\ntrailer\r\n<</Size 1504>>\r\nstartxref\r\n0\r\n%%EOF";
        string chunkWithStream = "1503 0 obj<</Length 876/Filter/FlateDecode/I 2722>>stream\r\nrandombytes";

        Assert.True(NoiseChunkDetector.IsPdfSyntaxNoise(chunkWithTrailer));
        Assert.True(NoiseChunkDetector.IsPdfSyntaxNoise(chunkWithStream));
        Assert.True(NoiseChunkDetector.IsCorruptedOrPdfSyntaxNoise(chunkWithTrailer));
        Assert.True(NoiseChunkDetector.IsNoiseOrSeparator(chunkWithTrailer));

        // 2. 模拟高乱码率切片 (超过 2% 的 \uFFFD)
        string textWithReplacements = "正常开头" + new string('\uFFFD', 10) + "正常结尾";
        Assert.True(NoiseChunkDetector.IsCorruptedNoise(textWithReplacements));
        Assert.True(NoiseChunkDetector.IsNoiseOrSeparator(textWithReplacements));

        // 3. 正常文本切片
        string normalText = "这是一段非常正常的知识库技术文档切片，阐述了 ReferenceRAG 的核心架构。";
        Assert.False(NoiseChunkDetector.IsPdfSyntaxNoise(normalText));
        Assert.False(NoiseChunkDetector.IsCorruptedNoise(normalText));
        Assert.False(NoiseChunkDetector.IsNoiseOrSeparator(normalText));

        // 4. 验证 SeparatorNoiseAdapter 将 PDF 语法与乱码噪音丢弃 (Discard)
        var adapter = new SeparatorNoiseAdapter();
        var chunkRecord = new ChunkRecord
        {
            Content = chunkWithStream,
            FileId = "doc1",
            TokenCount = 20
        };
        var evalContext = new ChunkEvaluationContext
        {
            ChunkIndex = 1,
            TotalChunks = 5,
            HasPreviousChunk = true,
            HasNextChunk = true
        };

        var evalResult = adapter.Evaluate(chunkRecord, evalContext);
        Assert.False(evalResult.IsQualified);
        Assert.Equal(IngestionActionType.Discard, evalResult.RecommendedAction);
    }

    [Fact]
    public void Gate6_TextEncodingAdapter_ShouldNotFallbackToGb18030ForBinary()
    {
        // 构造以 %PDF- 开头的二进制流
        byte[] binaryPdfBytes = "%PDF-1.7\r\n\x80\x81\x82\x83\x84\x85\x86\x87"u8.ToArray();

        // 原先的缺陷是严格 UTF-8 失败后回退至 GB18030 将其转为乱码汉字
        // 现在的门禁是直接返回 string.Empty
        string decoded = TextEncodingAdapter.DetectAndDecode(binaryPdfBytes);
        Assert.Empty(decoded);
    }

    [Fact]
    public async Task Gate7_KnowledgeBaseSyncWorker_EndToEnd_WithPdfAndScannedPdf()
    {
        var testDocsDir = Path.Combine(_tempDir, "mixed_source");
        Directory.CreateDirectory(testDocsDir);

        // 1. 放入一份正常的 Markdown 文档
        File.WriteAllText(Path.Combine(testDocsDir, "readme.md"), "# Welcome\nThis is a standard markdown document.");

        // 2. 放入一份正常的电子版 PDF
        byte[] digitalPdfBytes = CreateDigitalPdfBytes("Electronic PDF Content for ReferenceRAG");
        File.WriteAllBytes(Path.Combine(testDocsDir, "guide.pdf"), digitalPdfBytes);

        // 3. 放入一份扫描版 PDF
        byte[] scannedPdfBytes = CreateScannedPdfBytes();
        File.WriteAllBytes(Path.Combine(testDocsDir, "scanned_book.pdf"), scannedPdfBytes);

        var catalogDb = Path.Combine(_tempDir, "catalog.db");
        using var catalog = new CatalogStore(catalogDb);
        await catalog.InitializeAsync();

        using var storageManager = new KnowledgeBaseStorageManager(_tempDir);

        var kb = new KnowledgeBaseDefinition(
            Id: "kb_pdf_test",
            Name: "PDF Gatekeeper Test KB",
            Description: null,
            Enabled: true,
            DesiredConfigRevision: 1,
            EffectiveConfigRevision: 1,
            EmbeddingSpecId: "default",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow
        );
        await catalog.SaveKnowledgeBaseAsync(kb);

        using var connector = new LocalFolderConnector("conn_mixed", "kb_pdf_test", testDocsDir, "*.*");

        var job = new JobDefinition(
            Id: "job_sync_pdf",
            Operation: "sync",
            KnowledgeBaseId: "kb_pdf_test",
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Queued,
            Phase: OperationPhase.None,
            TotalUnits: 0,
            ProcessedUnits: 0,
            IdempotencyKey: null,
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow,
            FinishedAt: null
        );

        var worker = new KnowledgeBaseSyncWorker(catalog, storageManager, enableEmbedding: false);
        var finalJob = await worker.ExecuteSyncAsync(job, connector);

        Assert.Equal(JobStatus.Succeeded, finalJob.Status);
        Assert.Equal(3, finalJob.ProcessedUnits);

        // 验证存储中切片质量：
        await using var lease = await storageManager.AcquireLeaseAsync("kb_pdf_test", StorageAccess.ReadOnly);

        var (docs, _) = await lease.Store.GetDocumentsAsync(page: 1, pageSize: 10);
        var digitalDoc = docs.FirstOrDefault(d => d.SourceKey.EndsWith("guide.pdf"));
        Assert.NotNull(digitalDoc);
        Assert.True(digitalDoc.ChunkCount > 0);

        var (digitalChunks, digitalTotal) = await lease.Store.GetChunksAsync(documentId: digitalDoc.Id, page: 1, pageSize: 10);
        Assert.True(digitalTotal > 0);
        Assert.All(digitalChunks, c => Assert.Contains("Electronic PDF Content", c.Text));

        // 扫描版 PDF 坚决不产生任何切片（0 切片），彻底杜绝乱码污染！
        var scannedDoc = docs.FirstOrDefault(d => d.SourceKey.EndsWith("scanned_book.pdf"));
        Assert.NotNull(scannedDoc);
        Assert.Equal(0, scannedDoc.ChunkCount);

        var (scannedChunks, scannedTotal) = await lease.Store.GetChunksAsync(documentId: scannedDoc.Id, page: 1, pageSize: 10);
        Assert.Equal(0, scannedTotal);
        Assert.Empty(scannedChunks);

        // 全库切片中绝无 trailer, startxref, %%EOF, FlateDecode 等语法垃圾
        var (allChunks, _) = await lease.Store.GetChunksAsync(documentId: null, page: 1, pageSize: 100);
        Assert.All(allChunks, c =>
        {
            Assert.False(NoiseChunkDetector.IsPdfSyntaxNoise(c.Text));
            Assert.False(NoiseChunkDetector.IsCorruptedNoise(c.Text));
        });
    }

    [Fact]
    public void Gate8_MagicGuard_ShouldNotRejectUtf16TextWithBomAsBinary()
    {
        // 构造带 BOM 的 UTF-16 LE 中文字符串: "这是一段合法的UTF-16中文测试文本"
        string sample = "这是一段合法的UTF-16中文测试文本";
        byte[] utf16LeBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(sample)).ToArray();

        // 验证 BinaryMagicGuard 绝不将带 BOM 的 UTF-16 误判为二进制
        Assert.False(BinaryMagicGuard.IsKnownBinary(utf16LeBytes));

        // 验证 TextEncodingAdapter 能成功探测并解码为正确文本
        string decoded = TextEncodingAdapter.DetectAndDecode(utf16LeBytes);
        Assert.Equal(sample, decoded);

        // 验证 UTF-16 BE
        byte[] utf16BeBytes = Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(sample)).ToArray();
        Assert.False(BinaryMagicGuard.IsKnownBinary(utf16BeBytes));
        string decodedBe = TextEncodingAdapter.DetectAndDecode(utf16BeBytes);
        Assert.Equal(sample, decodedBe);
    }

    [Fact]
    public async Task Gate9_PdfPigExtractor_ShouldClassifyEmptyPdf_AsEmptyDocument()
    {
        var extractor = new PdfPigDocumentExtractor();
        byte[] emptyPdfBytes = CreateEmptyPdfBytes();

        using var ms = new MemoryStream(emptyPdfBytes);
        var result = await extractor.ExtractAsync(ms, "blank.pdf");

        // 验证纯空白 PDF（无字也无图）被精准分类为 EmptyDocument，而不是误判为 ScannedRequiresOcr
        Assert.Equal(DocumentParseStatus.EmptyDocument, result.Status);
        Assert.False(result.IsScannedPdf);
        Assert.Empty(result.ExtractedText);
    }

    [Fact]
    public async Task Gate10_PdfPigExtractor_RichTextBookWithImages_ShouldBeDigitalNotScanned()
    {
        // 模拟《资本论》类文档：每页都有光栅图像，但全书拥有丰富文本内容（>500字）
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        page.AddPng(TinyPngBytes, new PdfRectangle(10, 10, 100, 100));
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(new string('A', 600), 10, new PdfPoint(50, 700), font);
        byte[] pdfWithImageAndRichText = builder.Build();

        var extractor = new PdfPigDocumentExtractor();
        using var ms = new MemoryStream(pdfWithImageAndRichText);
        var result = await extractor.ExtractAsync(ms, "capital_volume_one.pdf");

        // 验证即使包含光栅图片，只要文字量丰富，必须判定为 Success 电子版 PDF，绝不误判为扫描件！
        Assert.Equal(DocumentParseStatus.Success, result.Status);
        Assert.False(result.IsScannedPdf);
        Assert.Equal(600, result.ExtractedCharCount);
        Assert.NotEmpty(result.ExtractedText);
    }

    [Fact]
    public async Task Gate11_OnnxOcrStrategy_WhenEngineAvailable_ShouldExtractText()
    {
        var mockOcr = new MockOcrEngine("识别出的扫描件图书文字内容");
        var strategy = new OnnxOcrScannedStrategy(mockOcr);
        var extractor = new PdfPigDocumentExtractor(strategy);

        byte[] scannedPdfBytes = CreateScannedPdfBytes();
        using var ms = new MemoryStream(scannedPdfBytes);

        // 默认不开启 OCR：极速跳过返回 ScannedRequiresOcr，保护全库同步吞吐
        var defaultResult = await extractor.ExtractAsync(ms, "scanned_with_ocr.pdf", DocumentExtractionOptions.Default);
        Assert.Equal(DocumentParseStatus.ScannedRequiresOcr, defaultResult.Status);
        Assert.True(defaultResult.IsScannedPdf);

        // 显式开启按需 OCR：成功提取出文字并标记为 Success
        ms.Position = 0;
        var result = await extractor.ExtractAsync(ms, "scanned_with_ocr.pdf", DocumentExtractionOptions.WithOcr());

        Assert.Equal(DocumentParseStatus.Success, result.Status);
        Assert.True(result.IsScannedPdf);
        Assert.Contains("识别出的扫描件图书文字内容", result.ExtractedText);
    }

    [Fact]
    public void Gate12_NoiseChunkDetector_RealWorldCapitalVol1Syntax_ShouldBeIntercepted()
    {
        // 真实截图中《资本论 第一卷》的现场样本切片
        string realCorruptedChunk = "%PDF-1.6\r\n12691 0 obj<</Filter/FlateDecode/First 2142/Length 4649/N 200/Type/ObjStm>>stream\r\nh[~}pfH\"?EP\x00\x01\x02";

        Assert.True(NoiseChunkDetector.IsPdfSyntaxNoise(realCorruptedChunk));
        Assert.True(NoiseChunkDetector.IsCorruptedOrPdfSyntaxNoise(realCorruptedChunk));
    }

    private class MockOcrEngine : IOcrInferenceEngine
    {
        private readonly string _mockText;
        public MockOcrEngine(string mockText) => _mockText = mockText;
        public bool IsAvailable => true;
        public string EngineName => "MockPP-OCRv4";
        public Task<OcrRecognitionResult> RecognizeImageAsync(ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
        {
            return Task.FromResult(new OcrRecognitionResult(_mockText, 0.99f, "MockPP-OCRv4"));
        }
    }
}
