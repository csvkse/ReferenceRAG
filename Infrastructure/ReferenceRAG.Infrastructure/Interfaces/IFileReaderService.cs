using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// 高性能文件阅读与元数据服务（行范围滑动窗口、Markdown 大纲状态机）
/// </summary>
public interface IFileReaderService
{
    /// <summary>
    /// 流式按行范围读取文件（支持早中断，O(1) 内存占用）
    /// </summary>
    Task<FileLineRangeResult> ReadLineRangeAsync(
        string normalizedPath,
        int startLine = 1,
        int? endLine = null,
        long byteOffset = 0,
        CancellationToken ct = default);

    /// <summary>
    /// 流式提取文件全景元数据（包括总行数、换行符类型、Markdown Frontmatter 和层级标题大纲）
    /// </summary>
    Task<FileMetadataResult> GetMetadataAsync(
        string normalizedPath,
        CancellationToken ct = default);
}
