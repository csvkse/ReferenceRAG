using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;
using ReferenceRAG.Core.Features.DocumentExtraction.Guards;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Extractors;

/// <summary>
/// 未受支持二进制文档兜底抽取器（熔断兜底）
/// 捕获所有未受支持的二进制文件流，安全阻断，防止产生垃圾切片污染存储
/// </summary>
public sealed class UnsupportedBinaryDocumentExtractor : IDocumentContentExtractor
{
    public bool CanHandle(string extension, ReadOnlySpan<byte> magicHeader) =>
        BinaryMagicGuard.IsKnownBinary(magicHeader);

    public Task<DocumentExtractResult> ExtractAsync(
        Stream stream,
        string sourceKey,
        DocumentExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        return Task.FromResult(new DocumentExtractResult(
            Status: DocumentParseStatus.UnsupportedBinary,
            ExtractedText: string.Empty,
            DiagnosticMessage: $"未受支持的二进制文件格式，已安全阻断: {sourceKey}"
        ));
    }
}
