namespace ReferenceRAG.Core.Features.DocumentExtraction.Domain;

/// <summary>
/// 文档内容抽取结果
/// </summary>
public sealed record DocumentExtractResult(
    DocumentParseStatus Status,
    string ExtractedText,
    int PageCount = 0,
    int ExtractedCharCount = 0,
    bool IsScannedPdf = false,
    string? DiagnosticMessage = null
);
