// ============================================================
// SearchTrace.cs — 向量搜索链路追踪（全量 AOP，单文件）
//
// 架构：
//   SearchPhaseTimings      — 每次请求的可变阶段耗时（AsyncLocal 携带）
//   SearchTraceContext       — AsyncLocal 容器（静态）
//   SearchPhaseCollector     — 历史统计聚合（静态单例，BoundedHistogram）
//   SearchPhaseReport        — 查询端点返回模型
//   SearchTraceAttribute     — Rougamo IL 织入属性（入口/出口 hook）
//
// 拦截目标（无需修改目标方法源码）：
//   SearchService            → SearchAsync / TryTitleFirstSearchAsync /
//                              ExecuteStandardSearchAsync / ExpandWithGraphAsync
//   HybridSearchService      → SearchAsync
//   EmbeddingService         → EncodeAsync
//
// 对接方式：
//   目标类加一行接口声明：IRougamo<SearchTraceAttribute>
//   查询统计：GET /api/performance/search-trace
//   重置统计：DELETE /api/performance/search-trace
// ============================================================

using System.Diagnostics;
using Rougamo;
using Rougamo.Context;
using Rougamo.Metadatas;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Tracing;

// ------------------------------------------------------------------
// 每次搜索请求的阶段耗时（字段直接赋值，通过 Interlocked 保证并发安全）
// ------------------------------------------------------------------
internal sealed class SearchPhaseTimings
{
    public long ModelLoadMs;
    public long EmbedMs;
    public long Bm25Ms;
    public long TitleMs;
    public long VectorMs;
    public long HybridMs;
    public long GraphMs;
    public long RerankMs;
}

// ------------------------------------------------------------------
// AsyncLocal 容器 — 随 async 调用链自动传递，无需手动传参
// ------------------------------------------------------------------
public static class SearchTraceContext
{
    internal static readonly AsyncLocal<SearchPhaseTimings?> Current = new();

    public static void RecordModelLoad(long ms)
    {
        var t = Current.Value;
        if (t != null && ms > 0) Interlocked.Add(ref t.ModelLoadMs, ms);
    }

    public static void RecordEmbed(long ms)
    {
        var t = Current.Value;
        if (t != null && ms > 0) Interlocked.Add(ref t.EmbedMs, ms);
    }

    public static void RecordBm25(long ms)
    {
        var t = Current.Value;
        if (t != null && ms > 0) Interlocked.Add(ref t.Bm25Ms, ms);
    }

    public static void RecordVector(long ms)
    {
        var t = Current.Value;
        if (t != null && ms > 0) Interlocked.Add(ref t.VectorMs, ms);
    }

    public static void RecordHybrid(long ms)
    {
        var t = Current.Value;
        if (t != null && ms > 0) Interlocked.Add(ref t.HybridMs, ms);
    }

    public static void RecordTitle(long ms)
    {
        var t = Current.Value;
        if (t != null && ms > 0) Interlocked.Add(ref t.TitleMs, ms);
    }

    public static void RecordGraph(long ms)
    {
        var t = Current.Value;
        if (t != null && ms > 0) Interlocked.Add(ref t.GraphMs, ms);
    }

    public static void RecordRerank(long ms)
    {
        var t = Current.Value;
        if (t != null && ms > 0) Interlocked.Add(ref t.RerankMs, ms);
    }
}

// ------------------------------------------------------------------
// 历史统计收集器（静态单例，使用现有 BoundedHistogram 聚合）
// ------------------------------------------------------------------
public static class SearchPhaseCollector
{
    private static readonly Services.BoundedHistogram _modelLoad = new(2000);
    private static readonly Services.BoundedHistogram _embed     = new(2000);
    private static readonly Services.BoundedHistogram _bm25      = new(2000);
    private static readonly Services.BoundedHistogram _title     = new(2000);
    private static readonly Services.BoundedHistogram _vector    = new(2000);
    private static readonly Services.BoundedHistogram _hybrid    = new(2000);
    private static readonly Services.BoundedHistogram _graph     = new(2000);
    private static readonly Services.BoundedHistogram _rerank    = new(2000);
    private static long _totalSearches;

    internal static void Record(SearchPhaseStats phases)
    {
        Interlocked.Increment(ref _totalSearches);
        if (phases.ModelLoadMs > 0) _modelLoad.Add(phases.ModelLoadMs);
        if (phases.EmbedMs     > 0) _embed    .Add(phases.EmbedMs);
        if (phases.Bm25Ms      > 0) _bm25     .Add(phases.Bm25Ms);
        if (phases.TitleMs     > 0) _title    .Add(phases.TitleMs);
        if (phases.VectorMs    > 0) _vector   .Add(phases.VectorMs);
        if (phases.HybridMs    > 0) _hybrid   .Add(phases.HybridMs);
        if (phases.GraphMs     > 0) _graph    .Add(phases.GraphMs);
        if (phases.RerankMs    > 0) _rerank   .Add(phases.RerankMs);
    }

    public static SearchPhaseReport GetReport() => new()
    {
        TotalSearches = Interlocked.Read(ref _totalSearches),
        ModelLoad = _modelLoad.GetStatistics(),
        Embed     = _embed    .GetStatistics(),
        Bm25      = _bm25     .GetStatistics(),
        Title     = _title    .GetStatistics(),
        Vector    = _vector   .GetStatistics(),
        Hybrid    = _hybrid   .GetStatistics(),
        Graph     = _graph    .GetStatistics(),
        Rerank    = _rerank   .GetStatistics(),
    };

    public static void Reset()
    {
        Interlocked.Exchange(ref _totalSearches, 0);
        _modelLoad.Clear(); _embed.Clear(); _bm25.Clear(); _title.Clear(); _vector.Clear();
        _hybrid.Clear(); _graph.Clear(); _rerank.Clear();
    }
}

// ------------------------------------------------------------------
// 查询端点返回模型
// ------------------------------------------------------------------
public sealed class SearchPhaseReport
{
    public long TotalSearches { get; init; }
    public Services.HistogramStatistics ModelLoad { get; init; } = new();
    public Services.HistogramStatistics Embed     { get; init; } = new();
    public Services.HistogramStatistics Bm25      { get; init; } = new();
    public Services.HistogramStatistics Title     { get; init; } = new();
    public Services.HistogramStatistics Vector    { get; init; } = new();
    public Services.HistogramStatistics Hybrid    { get; init; } = new();
    public Services.HistogramStatistics Graph     { get; init; } = new();
    public Services.HistogramStatistics Rerank    { get; init; } = new();
}

// ------------------------------------------------------------------
// Rougamo AOP 属性 — 编译期 IL 织入，覆盖 private 方法 + 无接口类
// 匹配范围由类级 [Pointcut] 声明（Rougamo 5.0 起从 Flags 属性改为特性）
// ------------------------------------------------------------------
[Pointcut(AccessFlags.All | AccessFlags.Method)]
public sealed class SearchTraceAttribute : MoAttribute
{
    public static readonly ActivitySource ActivitySource =
        new("ReferenceRAG.Search", "1.0.0");

    public override void OnEntry(MethodContext ctx)
    {
        ctx.Datas["sw"]  = Stopwatch.StartNew();
        ctx.Datas["act"] = ActivitySource.StartActivity(
            $"{ctx.TargetType?.Name ?? ctx.Method.DeclaringType?.Name}.{ctx.Method.Name}",
            ActivityKind.Internal);

        // 根搜索入口：建立当前请求的阶段追踪上下文
        if (IsRootSearch(ctx))
            SearchTraceContext.Current.Value = new SearchPhaseTimings();
    }

    public override void OnExit(MethodContext ctx)
    {
        var ms = StopStopwatch(ctx);
        StopActivity(ctx, ms);

        // 异常时不记录耗时（避免错误数据污染统计）
        if (ctx.Exception is not null) return;

        RecordPhaseTime(ctx, ms);

        // 根搜索完成：将阶段数据注入 API 响应 + 写入历史统计
        if (IsRootSearch(ctx) && ctx.ReturnValue is AIQueryResponse response)
        {
            var t = SearchTraceContext.Current.Value;
            if (t is not null)
            {
                var phases = new SearchPhaseStats
                {
                    ModelLoadMs = t.ModelLoadMs,
                    EmbedMs     = t.EmbedMs,
                    Bm25Ms      = t.Bm25Ms,
                    TitleMs     = t.TitleMs,
                    VectorMs    = t.VectorMs,
                    HybridMs    = t.HybridMs,
                    GraphMs     = t.GraphMs,
                    RerankMs    = t.RerankMs,
                };
                response.Stats.Phases = phases;
                SearchPhaseCollector.Record(phases);
                SearchTraceContext.Current.Value = null;
            }
        }
    }

    // ---- 内部辅助 ----

    private static long StopStopwatch(MethodContext ctx)
    {
        if (ctx.Datas.Contains("sw") && ctx.Datas["sw"] is Stopwatch sw)
        {
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        return 0;
    }

    private static void StopActivity(MethodContext ctx, long ms)
    {
        if (!ctx.Datas.Contains("act") || ctx.Datas["act"] is not Activity act) return;
        act.SetTag("duration_ms", ms);
        if (ctx.Exception is not null)
            act.SetStatus(ActivityStatusCode.Error, ctx.Exception.Message);
        act.Stop();
    }

    private static void RecordPhaseTime(MethodContext ctx, long ms)
    {
        var t = SearchTraceContext.Current.Value;
        if (t is null) return;

        // TargetType 优先（更可靠），DeclaringType 兜底
        var typeName = ctx.TargetType?.Name ?? ctx.Method.DeclaringType?.Name ?? string.Empty;

        switch (ctx.Method.Name)
        {
            case "EncodeAsync" when t.EmbedMs == 0:
                t.EmbedMs  += ms; break;                   // ONNX 推理兜底（未显式打点时）
            case "TryTitleFirstSearchAsync" when t.TitleMs == 0:
                t.TitleMs   = ms; break;                   // 标题图搜索
            case "ExecuteStandardSearchAsync" when t.VectorMs == 0:
                t.VectorMs  = ms; break;                   // 纯向量检索
            case "ExpandWithGraphAsync" when t.GraphMs == 0:
                t.GraphMs   = ms; break;                   // 图扩展
            case "SearchAsync" when typeName.Contains("Hybrid") && t.HybridMs == 0:
                t.HybridMs  = ms; break;                   // BM25+向量融合兜底
            case "RerankBatchAsync" when t.RerankMs == 0:
                t.RerankMs += ms; break;                   // Cross-Encoder 重排兜底
        }
    }

    // SearchService.SearchAsync = 整个搜索的根入口
    private static bool IsRootSearch(MethodContext ctx) =>
        ctx.Method.Name == "SearchAsync" &&
        (ctx.TargetType?.Name == "SearchService" ||
         ctx.Method.DeclaringType?.Name == "SearchService");
}
