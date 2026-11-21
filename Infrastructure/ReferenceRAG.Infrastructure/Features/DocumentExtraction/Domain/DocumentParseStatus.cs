namespace ReferenceRAG.Core.Features.DocumentExtraction.Domain;

/// <summary>
/// 文档解析与内容抽取状态
/// </summary>
public enum DocumentParseStatus
{
    /// <summary>
    /// 成功提取出正常纯文本
    /// </summary>
    Success,

    /// <summary>
    /// 文档内容为空 (0 字节或纯空白)
    /// </summary>
    EmptyDocument,

    /// <summary>
    /// 扫描版或纯图片型 PDF，无数字文本层，需 OCR 或优雅跳过
    /// </summary>
    ScannedRequiresOcr,

    /// <summary>
    /// 未受支持的二进制格式 (如 zip, exe, bin, office 压缩包等)，已安全阻断
    /// </summary>
    UnsupportedBinary,

    /// <summary>
    /// 解析器发生未知异常或格式损坏
    /// </summary>
    ExtractionFailed,

    /// <summary>
    /// 切片未通过质量门禁（切片包含过多底层语法噪音或乱码）
    /// </summary>
    QualityRejected
}
