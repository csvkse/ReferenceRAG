using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Domain;

/// <summary>
/// 扫描版 PDF 处置策略接口（策略模式）
/// 隔离阶段一（优雅跳过降级）与阶段二（本地端侧 OCR 识别）
/// </summary>
public interface IScannedPdfHandlingStrategy
{
    /// <summary>
    /// 处理判定为扫描件的 PDF 文档
    /// </summary>
    Task<DocumentExtractResult> HandleAsync(PdfDocument document, string sourceKey, CancellationToken ct = default);
}
