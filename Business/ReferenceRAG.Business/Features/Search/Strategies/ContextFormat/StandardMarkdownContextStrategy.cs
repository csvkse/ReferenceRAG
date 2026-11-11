using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Business.Features.Search.Strategies.ContextFormat;

/// <summary>
/// 标准 Markdown 检索上下文策略（推荐默认）
/// 专为生产级 RAG 设计：提供规范标号、文件名、相对路径、行号范围、章节层级及引用来源
/// 便于人类阅读和大模型进行高可信度引用（Footnote Citations）
/// </summary>
public class StandardMarkdownContextStrategy : IContextFormatStrategy
{
    public ReferenceRAG.Core.Models.ContextFormat Format => ReferenceRAG.Core.Models.ContextFormat.Standard;

    public string FormatContext(IReadOnlyList<ChunkResult> chunks, int maxTokens)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# 相关内容\n");

        var currentTokens = 0;
        int index = 1;

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

            sb.AppendLine($"### [{index}] 《{title}》");
            if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                sb.AppendLine($"- 文件: {normalizedPath}");
            }

            if (chunk.StartLine > 0 && chunk.EndLine >= chunk.StartLine)
            {
                var lineDesc = chunk.StartLine == chunk.EndLine
                    ? $"第 {chunk.StartLine} 行"
                    : $"第 {chunk.StartLine}-{chunk.EndLine} 行";
                sb.AppendLine($"- 范围: {lineDesc}");
            }

            if (!string.IsNullOrWhiteSpace(chunk.HeadingPath))
            {
                sb.AppendLine($"- 章节: {chunk.HeadingPath}");
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
            index++;
        }

        return sb.ToString();
    }

    public string FormatPrompt(string query, string context)
    {
        return $@"基于以下知识库内容回答问题：

{context}

问题：{query}

请根据上述内容提供准确、详细的回答，并在回答中引用来源（包括文件名和行号）。";
    }
}
