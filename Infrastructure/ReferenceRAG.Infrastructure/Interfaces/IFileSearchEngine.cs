using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// Agent 级文件高效关键字与正则搜索引擎接口
/// </summary>
public interface IFileSearchEngine
{
    /// <summary>
    /// 执行全源或指定源的高效文本/正则扫描
    /// </summary>
    Task<FileSearchResult> SearchAsync(FileSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// 仅按文件名通配模式快速查找定位文件路径（对标 fd-find）
    /// </summary>
    Task<List<string>> FindFilesAsync(string? sourceName, string globPattern, int maxResults = 100, CancellationToken ct = default);
}
