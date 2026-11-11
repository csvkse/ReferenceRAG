using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Business.Features.Search.Strategies.ContextFormat;

/// <summary>
/// 紧凑高密度上下文策略
/// 去除装饰性 Markdown 与冗长引用，最大化 Token 利用率，适用于狭窄 Context Window 场景
/// </summary>
public class CompactAgentContextStrategy : IContextFormatStrategy
{
    public ReferenceRAG.Core.Models.ContextFormat Format => ReferenceRAG.Core.Models.ContextFormat.Compact;

    public string FormatContext(IReadOnlyList<ChunkResult> chunks, int maxTokens)
    {
        if (chunks == null || chunks.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var currentTokens = 0;
        int index = 1;

        foreach (var chunk in chunks)
        {
            var chunkTokens = TokenEstimator.EstimateTokens(chunk.Content);
            if (currentTokens + chunkTokens > maxTokens && currentTokens > 0)
            {
                break;
            }

            var normalizedPath = (chunk.FilePath ?? string.Empty).Replace('\\', '/');
            var lineAnchor = (chunk.StartLine > 0 && chunk.EndLine >= chunk.StartLine)
                ? (chunk.StartLine == chunk.EndLine ? $" L{chunk.StartLine}" : $" L{chunk.StartLine}-L{chunk.EndLine}")
                : string.Empty;

            sb.AppendLine($"[{index}] {normalizedPath}{lineAnchor}:");
            sb.AppendLine(chunk.Content);
            sb.AppendLine("---");

            currentTokens += chunkTokens;
            index++;
        }

        return sb.ToString().TrimEnd();
    }

    public string FormatPrompt(string query, string context)
    {
        return $@"参考内容：
{context}

问：{query}
请简洁准确回答并注明引用文件及行号。";
    }
}
