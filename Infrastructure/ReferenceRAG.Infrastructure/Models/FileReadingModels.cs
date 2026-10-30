using System;
using System.Collections.Generic;

namespace ReferenceRAG.Core.Models;

/// <summary>
/// 结构化标题大纲节点（用于 Markdown TOC）
/// </summary>
public sealed record HeadingOutlineItem(
    int Level,          // 标题层级 (1 ~ 6)
    string Title,       // 标题纯文本
    int LineNumber      // 所在行号 (1-based)
);

/// <summary>
/// 文件丰富元数据结果
/// </summary>
public sealed record FileMetadataResult(
    string Path,
    string Name,
    string Extension,
    long SizeBytes,
    int TotalLines,
    string LineEnding,  // "CRLF" | "LF" | "Mixed" | "None"
    DateTime ModifiedUtc,
    bool IsBinary,
    bool IsMarkdown,
    Dictionary<string, string> Frontmatter,
    List<HeadingOutlineItem> Headings,
    int? RagChunkCount = null,
    DateTime? RagLastIndexed = null
);

/// <summary>
/// 行范围流式读取结果
/// </summary>
public sealed record FileLineRangeResult(
    string Path,
    string Name,
    int StartLine,
    int EndLine,
    int TotalLines,
    int ReturnedLines,
    bool HasMore,
    int NextStartLine,
    long NextByteOffset,
    List<string> Lines,
    string Content
);
