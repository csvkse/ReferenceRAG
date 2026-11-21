using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Domain;

/// <summary>
/// 扫描版 PDF 处置策略接口（策略模式）
/// 隔离默认同步（快速跳过降级）与按需触发（端侧/混合 OCR 识别）
/// </summary>
public interface IScannedPdfHandlingStrategy
{
    /// <summary>
    /// 处理判定为扫描件的 PDF 文档
    /// </summary>
    Task<DocumentExtractResult> HandleAsync(
        PdfDocument document,
        string sourceKey,
        DocumentExtractionOptions? options = null,
        CancellationToken ct = default);
}
