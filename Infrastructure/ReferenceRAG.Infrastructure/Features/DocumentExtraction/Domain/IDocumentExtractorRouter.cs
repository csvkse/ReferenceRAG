using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Domain;

/// <summary>
/// 文档抽取路由接口
/// 负责根据文件魔数与扩展名将数据流路由至最匹配的内容抽取器
/// </summary>
public interface IDocumentExtractorRouter
{
    /// <summary>
    /// 为指定的数据流路由最佳抽取器
    /// </summary>
    IDocumentContentExtractor Route(string sourceKey, Stream stream);

    /// <summary>
    /// 直接从流中安全提取文档内容
    /// </summary>
    Task<DocumentExtractResult> ExtractAsync(Stream stream, string sourceKey, CancellationToken ct = default);
}
