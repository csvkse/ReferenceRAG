using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Business.Features.Search.Strategies.ContextFormat;

/// <summary>
/// 结构化 XML 检索上下文策略
/// 专为 LLM Agent、工具调用与代码生成大模型设计，具有最高解析确定性与属性字段清晰度
/// </summary>
public class StructuredXmlContextStrategy : IContextFormatStrategy
{
    public ReferenceRAG.Core.Models.ContextFormat Format => ReferenceRAG.Core.Models.ContextFormat.StructuredXml;

    public string FormatContext(IReadOnlyList<ChunkResult> chunks, int maxTokens)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return "<context></context>";
        }

        var sb = new StringBuilder();
        sb.AppendLine("<context>");

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

            sb.Append($"  <chunk id=\"{index}\" file=\"{SecurityElement.Escape(normalizedPath)}\"");
            if (!string.IsNullOrWhiteSpace(title))
            {
                sb.Append($" title=\"{SecurityElement.Escape(title)}\"");
            }
            if (chunk.StartLine > 0 && chunk.EndLine >= chunk.StartLine)
            {
                sb.Append($" start_line=\"{chunk.StartLine}\" end_line=\"{chunk.EndLine}\"");
            }
            if (!string.IsNullOrWhiteSpace(chunk.HeadingPath))
            {
                sb.Append($" heading=\"{SecurityElement.Escape(chunk.HeadingPath)}\"");
            }
            sb.AppendLine(">");

            sb.AppendLine(chunk.Content);
            sb.AppendLine("  </chunk>");

            currentTokens += chunkTokens;
            index++;
        }

        sb.Append("</context>");
        return sb.ToString();
    }

    public string FormatPrompt(string query, string context)
    {
        return $@"参考以下 XML 格式的知识库上下文证据回答问题：

{context}

用户问题：{query}

请基于提供的分块证据给出客观、严谨的回答，并在引用依据时明确指明对应 chunk 的 file、title 及行号。";
    }
}
