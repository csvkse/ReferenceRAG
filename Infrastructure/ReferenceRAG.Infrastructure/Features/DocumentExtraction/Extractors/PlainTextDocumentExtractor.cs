using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;
using ReferenceRAG.Core.Features.DocumentExtraction.Guards;
using ReferenceRAG.Infrastructure.Features.Storage.Services;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Extractors;

/// <summary>
/// 纯文本与 Markdown 文档内容抽取器
/// 支持常见文本后缀，并带有二进制魔数拒止保护
/// </summary>
public sealed class PlainTextDocumentExtractor : IDocumentContentExtractor
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".json", ".log", ".yaml", ".yml", ".xml", ".html", ".htm", ".rst", ".sql"
    };

    public bool CanHandle(string extension, ReadOnlySpan<byte> magicHeader)
    {
        // 门禁：只要匹配二进制魔数，纯文本抽取器坚决拒接
        if (BinaryMagicGuard.IsKnownBinary(magicHeader)) return false;
        if (string.IsNullOrEmpty(extension)) return true;
        return SupportedExtensions.Contains(extension);
    }

    public async Task<DocumentExtractResult> ExtractAsync(
        Stream stream,
        string sourceKey,
        DocumentExtractionOptions? options = null,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        byte[] bytes = ms.ToArray();

        if (bytes.Length == 0)
        {
            return new DocumentExtractResult(
                Status: DocumentParseStatus.EmptyDocument,
                ExtractedText: string.Empty,
                DiagnosticMessage: "纯文本文件内容为空"
            );
        }

        // 二进制嗅探门禁校验
        if (BinaryMagicGuard.IsKnownBinary(bytes.AsSpan(0, Math.Min(bytes.Length, 512))))
        {
            return new DocumentExtractResult(
                Status: DocumentParseStatus.UnsupportedBinary,
                ExtractedText: string.Empty,
                DiagnosticMessage: "检测到二进制内容流，纯文本抽取器已拒绝读取"
            );
        }

        string text = TextEncodingAdapter.DetectAndDecode(bytes);
        return new DocumentExtractResult(
            Status: string.IsNullOrWhiteSpace(text) ? DocumentParseStatus.EmptyDocument : DocumentParseStatus.Success,
            ExtractedText: text,
            ExtractedCharCount: text.Length
        );
    }
}
