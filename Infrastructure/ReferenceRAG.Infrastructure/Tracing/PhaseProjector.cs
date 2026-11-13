using System;
using System.Collections.Generic;
using System.Linq;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Tracing;

/// <summary>
/// 耗时链 → 旧契约投影器。
/// 目的：新增 Span 树的同时保持 <see cref="SearchPhaseStats"/> 的字段语义不变，
/// 使既有前端与 <c>SearchTraceBreakdownTests</c> 无需修改即可继续工作。
/// </summary>
public static class PhaseProjector
{
    public static SearchPhaseStats Project(TimingChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        long SumPrefix(params string[] prefixes) => chain.Spans
            .Where(s => prefixes.Any(p => s.Name.StartsWith(p, StringComparison.Ordinal)))
            .Sum(s => s.DurationMs);

        // 索引构建只认 backfill/rebuild 这类真实写路径，排除每次查询都会发生的 .status 裁决
        var lexicalIndexBuild = chain.Spans
            .Where(s => s.Name.StartsWith("kb.lexical-index.", StringComparison.Ordinal)
                        && !s.Name.EndsWith(".status", StringComparison.Ordinal))
            .Sum(s => s.SelfMs);

        var bm25 = SumPrefix("kb.fts.match", "kb.lexical.like-fallback", "kb.lexical.match");

        return new SearchPhaseStats
        {
            ModelLoadMs = SumPrefix("model.load"),
            EmbedMs = SumPrefix("embed.infer"),
            Bm25Ms = bm25,
            TitleMs = SumPrefix("title.search"),
            VectorMs = SumPrefix("vector.search"),
            HybridMs = SumPrefix("fusion."),
            GraphMs = SumPrefix("graph."),
            RerankMs = SumPrefix("rerank."),
            LexicalIndexMs = lexicalIndexBuild,
            LockWaitMs = chain.Spans.Sum(s => s.WaitMs),
            // 分类总量用 SelfMs，避免父子同类 Span 重复计数导致「IO 合计 > 总耗时」
            IoMs = chain.Spans.Where(s => s.Category == SpanCategory.Io).Sum(s => s.SelfMs),
            UnattributedMs = chain.UnattributedMs
        };
    }

    /// <summary>把耗时链中的阶段汇总为前端可直接渲染的标签序列（按耗时降序）。</summary>
    public static IReadOnlyList<(string Label, long Ms)> ProjectLabels(TimingChain chain)
    {
        var list = new List<(string, long)>();
        var phases = chain.Spans
            .Where(s => s.Category is SpanCategory.Phase or SpanCategory.ModelLoad or SpanCategory.LockWait)
            .GroupBy(s => s.DisplayName)
            .Select(g => (g.Key, g.Sum(x => x.DurationMs)))
            .Where(x => x.Item2 > 0)
            .OrderByDescending(x => x.Item2)
            .Take(8);
        list.AddRange(phases);
        return list;
    }
}
