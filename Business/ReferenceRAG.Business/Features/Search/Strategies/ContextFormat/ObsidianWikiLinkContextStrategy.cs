using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Business.Features.Search.Strategies.ContextFormat;

/// <summary>
/// Obsidian 双向链接与知识库笔记优先策略
/// 侧重 Obsidian 笔记间无缝引用和双链跳转，并全面补齐文件路径与行号元数据
/// </summary>
public class ObsidianWikiLinkContextStrategy : IContextFormatStrategy
{
    public ReferenceRAG.Core.Models.ContextFormat Format => ReferenceRAG.Core.Models.ContextFormat.Obsidian;

    public string FormatContext(IReadOnlyList<ChunkResult> chunks, int maxTokens)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# 相关内容\n");

        var currentTokens = 0;

        foreach (var chunk in chunks)
        {
            var chunkTokens = TokenEstimator.EstimateTokens(chunk.Content);
            if (currentTokens + chunkTokens > maxTokens && currentTokens > 0)
            {
                break;
            }

            var title = !string.IsNullOrWhiteSpace(chunk.Title)
                ? chunk.Title
                : Path.GetFileNameWithoutExtension(chunk.FilePath);
            var normalizedPath = (chunk.FilePath ?? string.Empty).Replace('\\', '/');

            sb.AppendLine($"## {title}");
            if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                sb.AppendLine($"路径: `{normalizedPath}`");
            }

            if (chunk.StartLine > 0 && chunk.EndLine >= chunk.StartLine)
            {
                var lineAnchor = chunk.StartLine == chunk.EndLine
                    ? $"L{chunk.StartLine}"
                    : $"L{chunk.StartLine}-L{chunk.EndLine}";
                sb.AppendLine($"行号: `{lineAnchor}`");
            }

            if (!string.IsNullOrWhiteSpace(chunk.HeadingPath))
            {
                sb.AppendLine($"章节: {chunk.HeadingPath}");
            }

            sb.AppendLine();
            sb.AppendLine(chunk.Content);
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(chunk.ObsidianLink))
            {
                sb.AppendLine($"来源: {chunk.ObsidianLink}\n");
            }
            sb.AppendLine("---\n");

            currentTokens += chunkTokens;
        }

        return sb.ToString();
    }

    public string FormatPrompt(string query, string context)
    {
        return $@"基于以下知识库内容回答问题：

{context}

问题：{query}

请根据上述内容提供准确、详细的回答，并在回答中引用来源。";
    }
}
