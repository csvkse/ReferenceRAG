using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Domain;

/// <summary>
/// 文档内容抽取适配器接口（适配器模式）
/// 负责针对特定格式或魔数的文件流执行安全的文本解析与内容抽取
/// </summary>
public interface IDocumentContentExtractor
{
    /// <summary>
    /// 判断当前抽取器是否支持处理指定扩展名与文件头魔数
    /// </summary>
    bool CanHandle(string extension, ReadOnlySpan<byte> magicHeader);

    /// <summary>
    /// 从文件流中提取文本内容
    /// </summary>
    Task<DocumentExtractResult> ExtractAsync(Stream stream, string sourceKey, CancellationToken ct = default);
}
