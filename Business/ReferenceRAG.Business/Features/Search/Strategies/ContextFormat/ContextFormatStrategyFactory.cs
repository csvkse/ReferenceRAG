using System.Collections.Generic;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Search.Strategies.ContextFormat;

/// <summary>
/// 上下文格式化策略工厂（Factory Pattern）
/// </summary>
public static class ContextFormatStrategyFactory
{
    private static readonly Dictionary<ReferenceRAG.Core.Models.ContextFormat, IContextFormatStrategy> Strategies = new()
    {
        [ReferenceRAG.Core.Models.ContextFormat.Standard] = new StandardMarkdownContextStrategy(),
        [ReferenceRAG.Core.Models.ContextFormat.Obsidian] = new ObsidianWikiLinkContextStrategy(),
        [ReferenceRAG.Core.Models.ContextFormat.StructuredXml] = new StructuredXmlContextStrategy(),
        [ReferenceRAG.Core.Models.ContextFormat.Compact] = new CompactAgentContextStrategy(),
    };

    /// <summary>
    /// 根据格式类型获取对应的策略实现，未命中时默认回退到 Standard
    /// </summary>
    public static IContextFormatStrategy GetStrategy(ReferenceRAG.Core.Models.ContextFormat format)
    {
        return Strategies.TryGetValue(format, out var strategy)
            ? strategy
            : Strategies[ReferenceRAG.Core.Models.ContextFormat.Standard];
    }
}
