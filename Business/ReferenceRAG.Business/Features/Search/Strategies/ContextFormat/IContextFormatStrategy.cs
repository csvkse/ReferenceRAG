using System.Collections.Generic;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Search.Strategies.ContextFormat;

/// <summary>
/// 上下文与 Prompt 格式化策略接口（Strategy Pattern）
/// </summary>
public interface IContextFormatStrategy
{
    /// <summary>
    /// 当前策略对应的格式枚举
    /// </summary>
    ReferenceRAG.Core.Models.ContextFormat Format { get; }

    /// <summary>
    /// 将命中的分块集格式化为适合当前场景的上下文文本
    /// </summary>
    string FormatContext(IReadOnlyList<ChunkResult> chunks, int maxTokens);

    /// <summary>
    /// 基于上下文与提问生成最终 Prompt
    /// </summary>
    string FormatPrompt(string query, string context);
}
