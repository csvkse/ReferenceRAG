using System;
using System.Collections.Generic;

namespace ReferenceRAG.Core.Models;

/// <summary>
/// 文件高效关键字/正则检索请求参数
/// </summary>
public sealed class FileSearchQuery
{
    /// <summary>
    /// 搜索模式（关键词明文或正则表达式）
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// 是否作为正则表达式解析（默认 false，按极速字串检索）
    /// </summary>
    public bool IsRegex { get; set; } = false;

    /// <summary>
    /// 是否大小写敏感（默认 false）
    /// </summary>
    public bool CaseSensitive { get; set; } = false;

    /// <summary>
    /// 限定搜索的数据源名称列表（可选；空则在调用方权限允许的所有源内检索）
    /// </summary>
    public List<string>? Sources { get; set; }

    /// <summary>
    /// 相对子目录或路径限定（可选，如 "docs/api"）
    /// </summary>
    public string? SubPath { get; set; }

    /// <summary>
    /// 文件名通配符过滤（可选，如 "*.md", "*.cs"）
    /// </summary>
    public string? FilePattern { get; set; }

    /// <summary>
    /// 前后文行数（0 ~ 5，默认 1）
    /// </summary>
    public int ContextLines { get; set; } = 1;

    /// <summary>
    /// 最大匹配命中条数（默认 100，最大上限 500）
    /// </summary>
    public int MaxMatches { get; set; } = 100;
}

/// <summary>
/// 匹配行详细信息
/// </summary>
public sealed record SearchMatchLine(
    int LineNumber,
    string Text,
    bool IsMatch,
    int MatchIndex = -1,
    int MatchLength = 0
);

/// <summary>
/// 连续上下文代码块（已合并重叠行）
/// </summary>
public sealed record SearchHunk(
    int StartLine,
    int EndLine,
    List<SearchMatchLine> Lines
);

/// <summary>
/// 单个文件中的命中集合
/// </summary>
public sealed record FileSearchMatchGroup(
    string SourceName,
    string RelativePath,
    string FullPath,
    int MatchCount,
    List<SearchHunk> Hunks
);

/// <summary>
/// 搜索最终聚合结果
/// </summary>
public sealed record FileSearchResult(
    bool Success,
    string QueryPattern,
    bool IsRegex,
    int TotalMatches,
    int MatchedFilesCount,
    int TotalFilesScanned,
    long ElapsedMilliseconds,
    bool Truncated,
    List<FileSearchMatchGroup> Files,
    string? Error = null
);
