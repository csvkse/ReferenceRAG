using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;
using ReferenceRAG.Core.Features.DocumentExtraction.Extractors;
using ReferenceRAG.Core.Features.DocumentExtraction.Strategies;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Services;

/// <summary>
/// 文档抽取路由器实现（策略与适配器路由器）
/// </summary>
public sealed class DocumentExtractorRouter : IDocumentExtractorRouter
{
    private readonly IReadOnlyList<IDocumentContentExtractor> _extractors;
    private readonly ILogger<DocumentExtractorRouter>? _logger;

    public DocumentExtractorRouter(
        IEnumerable<IDocumentContentExtractor>? extractors = null,
        ILogger<DocumentExtractorRouter>? logger = null)
    {
        _logger = logger;
        if (extractors != null && extractors.Any())
        {
            _extractors = extractors.ToList();
        }
        else
        {
            _extractors = new List<IDocumentContentExtractor>
            {
                new PdfPigDocumentExtractor(new GracefulDegradeScannedStrategy()),
                new PlainTextDocumentExtractor(),
                new UnsupportedBinaryDocumentExtractor()
            };
        }
    }

    public static DocumentExtractorRouter CreateDefault(
        ILogger<DocumentExtractorRouter>? logger = null,
        ILogger<PdfPigDocumentExtractor>? pdfLogger = null,
        ILogger<GracefulDegradeScannedStrategy>? degradeLogger = null)
    {
        return new DocumentExtractorRouter(new List<IDocumentContentExtractor>
        {
            new PdfPigDocumentExtractor(new GracefulDegradeScannedStrategy(degradeLogger), pdfLogger),
            new PlainTextDocumentExtractor(),
            new UnsupportedBinaryDocumentExtractor()
        }, logger);
    }

    public IDocumentContentExtractor Route(string sourceKey, Stream stream)
    {
        string extension = Path.GetExtension(sourceKey) ?? string.Empty;
        byte[] header = new byte[512];
        int read = 0;
        if (stream.CanSeek)
        {
            long originalPos = stream.Position;
            read = stream.Read(header, 0, header.Length);
            stream.Position = originalPos;
        }

        ReadOnlySpan<byte> headerSpan = header.AsSpan(0, read);

        foreach (var extractor in _extractors)
        {
            if (extractor.CanHandle(extension, headerSpan))
            {
                return extractor;
            }
        }

        return new UnsupportedBinaryDocumentExtractor();
    }

    public async Task<DocumentExtractResult> ExtractAsync(Stream stream, string sourceKey, CancellationToken ct = default)
    {
        string extension = Path.GetExtension(sourceKey) ?? string.Empty;
        byte[] header = new byte[512];
        int read = 0;

        if (stream.CanSeek)
        {
            long originalPos = stream.Position;
            read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct);
            stream.Position = originalPos;
        }
        else
        {
            var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            ms.Position = 0;
            return await ExtractAsync(ms, sourceKey, ct);
        }

        ReadOnlySpan<byte> headerSpan = header.AsSpan(0, read);

        foreach (var extractor in _extractors)
        {
            if (extractor.CanHandle(extension, headerSpan))
            {
                _logger?.LogDebug("[DocumentExtractorRouter] 命中抽取器 [{Extractor}] 负责处理: {Key}", extractor.GetType().Name, sourceKey);
                return await extractor.ExtractAsync(stream, sourceKey, ct);
            }
        }

        return new DocumentExtractResult(
            Status: DocumentParseStatus.UnsupportedBinary,
            ExtractedText: string.Empty,
            DiagnosticMessage: $"未找到能处理文件格式的抽取器: {sourceKey}"
        );
    }
}
