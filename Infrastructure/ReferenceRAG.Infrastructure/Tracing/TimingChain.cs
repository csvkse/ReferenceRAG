using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ReferenceRAG.Core.Tracing;

/// <summary>
/// 耗时链 Span 分类：用于前端着色与"冷启动/排队"归因。
/// </summary>
public enum SpanCategory
{
    /// <summary>请求根节点</summary>
    Request = 0,
    /// <summary>流水线阶段</summary>
    Phase = 1,
    /// <summary>子阶段</summary>
    SubPhase = 2,
    /// <summary>磁盘/数据库 IO</summary>
    Io = 3,
    /// <summary>锁等待 / 队列等待</summary>
    LockWait = 4,
    /// <summary>模型加载（冷启动）</summary>
    ModelLoad = 5,
    /// <summary>网络调用</summary>
    Network = 6,
    /// <summary>纯 CPU 计算（分词、打分、融合）</summary>
    Cpu = 7,
    /// <summary>结果装配</summary>
    Assembly = 8
}

/// <summary>
/// 单个耗时 Span。采用"扁平数组 + ParentId"表达树结构（对齐 OpenTelemetry / Chrome Trace 语义），
/// 避免深层嵌套 JSON 与递归渲染。
/// </summary>
public sealed class TimingSpan
{
    public string Id { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public SpanCategory Category { get; init; }
    /// <summary>相对请求起点的偏移（毫秒）</summary>
    public long StartMs { get; init; }
    public long DurationMs { get; init; }
    /// <summary>自身耗时（扣除子 Span），由 Complete 阶段回填</summary>
    public long SelfMs { get; set; }
    /// <summary>锁等待 / 排队耗时（包含在 DurationMs 内，单独标注）</summary>
    public long WaitMs { get; set; }
    public bool ColdStart { get; set; }
    public int? InputCount { get; set; }
    public int? OutputCount { get; set; }
    public string? Detail { get; set; }
    public Dictionary<string, string>? Attributes { get; set; }

    internal long StartTimestamp { get; init; }
}

/// <summary>
/// 归因摘要：让用户一眼看出"这 16 秒是冷启动还是算法慢"。
/// </summary>
public sealed class TimingChainSummary
{
    public long ColdStartMs { get; set; }
    public long IndexBuildMs { get; set; }
    public long LockWaitMs { get; set; }
    public long IoMs { get; set; }
    public long CpuMs { get; set; }
    /// <summary>
    /// 模型加载/推理耗时（SpanCategory.ModelLoad 的 SelfMs 合计）。
    /// 必须单独成项：向量推理常占单次检索 90% 以上，若只落进明细行而不进摘要，
    /// 用户会看到"总 6227ms，但 IO 589 + CPU 0"，剩下的时间等于凭空消失。
    /// </summary>
    public long ModelLoadMs { get; set; }
    /// <summary>扣除冷启动/索引构建/模型推理后的"算法真实耗时"</summary>
    public long PureRetrievalMs { get; set; }
    /// <summary>人类可读判定：正常 / 词法索引构建 / 冷启动 / 模型推理 / 锁竞争</summary>
    public string? Verdict { get; set; }
}

/// <summary>
/// 单次请求的完整耗时链。
/// </summary>
public sealed class TimingChain
{
    public string TraceId { get; init; } = string.Empty;
    public long TotalMs { get; init; }
    /// <summary>顶层耗时与子层之和的差额（"孤儿时间"），强制暴露未归因耗时</summary>
    public long UnattributedMs { get; init; }
    public List<TimingSpan> Spans { get; init; } = new();
    public TimingChainSummary? Summary { get; init; }
}

/// <summary>
/// 请求级耗时链构建器（并发安全：Span 列表加锁，父节点通过 AsyncLocal 传递，
/// 因此 Task.WhenAll 分叉出去的并行分支各自持有正确的父节点）。
/// </summary>
public sealed class TimingChainBuilder
{
    private readonly List<TimingSpan> _spans = new();
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _seq;
    private long _lockWaitMs;
    private long _coldStartMs;

    public string TraceId { get; } = Guid.NewGuid().ToString("N")[..12];

    internal long ElapsedMs => _clock.ElapsedMilliseconds;

    internal string AddSpan(TimingSpan span)
    {
        lock (_gate) { _spans.Add(span); }
        return span.Id;
    }

    internal string NextId() => $"s{Interlocked.Increment(ref _seq):D2}";

    internal void AddLockWait(long ms) => Interlocked.Add(ref _lockWaitMs, ms);
    internal void AddColdStart(long ms) => Interlocked.Add(ref _coldStartMs, ms);

    /// <summary>完成并物化耗时链（计算 SelfMs / 孤儿时间 / 归因摘要）。</summary>
    public TimingChain Complete(long? totalMsOverride = null)
    {
        _clock.Stop();
        var total = totalMsOverride ?? _clock.ElapsedMilliseconds;

        List<TimingSpan> spans;
        lock (_gate) { spans = _spans.OrderBy(s => s.StartMs).ThenBy(s => s.Id, StringComparer.Ordinal).ToList(); }

        var childSum = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var s in spans)
        {
            if (string.IsNullOrEmpty(s.ParentId)) continue;
            childSum.TryGetValue(s.ParentId, out var acc);
            childSum[s.ParentId] = acc + s.DurationMs;
        }
        foreach (var s in spans)
        {
            childSum.TryGetValue(s.Id, out var children);
            s.SelfMs = Math.Max(0, s.DurationMs - children);
        }

        // 孤儿时间：顶层 Span 耗时与请求总耗时的差 + 顶层 Span 内部未被子 Span 覆盖的部分
        var topSpans = spans.Where(s => string.IsNullOrEmpty(s.ParentId)).ToList();
        var topSum = topSpans.Sum(s => s.DurationMs);
        var unattributed = Math.Max(0, total - topSum) + topSpans.Sum(s => s.SelfMs);

        // 只统计真正的构建/补齐耗时：kb.lexical-index.status 是每次查询都要做的 O(1) 状态裁决，
        // 若计入"索引构建"会让正常查询也挂上「冷启动构建」的误判标签。
        var indexBuild = spans
            .Where(s => s.Name.StartsWith("kb.lexical-index.", StringComparison.Ordinal)
                        && !s.Name.EndsWith(".status", StringComparison.Ordinal))
            .Sum(s => s.SelfMs);
        var coldStart = Interlocked.Read(ref _coldStartMs) > 0
            ? Interlocked.Read(ref _coldStartMs)
            : spans.Where(s => s.ColdStart).Sum(s => s.SelfMs);
        // 模型加载/推理：向量推理是单次检索里最容易被忽略的大头（首包 5s+），
        // 必须按分类聚合进摘要，否则"总耗时"与各项归因之和会对不上。
        var modelLoad = spans.Where(s => s.Category == SpanCategory.ModelLoad).Sum(s => s.SelfMs);
        var lockWait = Interlocked.Read(ref _lockWaitMs);

        var summary = new TimingChainSummary
        {
            ColdStartMs = coldStart,
            IndexBuildMs = indexBuild,
            LockWaitMs = lockWait,
            // 分类聚合一律用 SelfMs：父子同类 Span 若用 DurationMs 会重复计数，
            // 出现「IO 合计 > 请求总耗时」这种自相矛盾的归因。
            IoMs = spans.Where(s => s.Category == SpanCategory.Io).Sum(s => s.SelfMs),
            CpuMs = spans.Where(s => s.Category == SpanCategory.Cpu).Sum(s => s.SelfMs),
            ModelLoadMs = modelLoad,
            // 冷启动标记通常就是模型首次加载（ColdStart 是 ModelLoad 的子集），
            // 取 max 而不是相加，避免同一段时间被扣两次、把"纯检索"扣成负数。
            PureRetrievalMs = Math.Max(0, total - indexBuild - Math.Max(coldStart, modelLoad)),
            Verdict = ResolveVerdict(total, coldStart, indexBuild, lockWait, modelLoad)
        };

        return new TimingChain
        {
            TraceId = TraceId,
            TotalMs = total,
            UnattributedMs = unattributed,
            Spans = spans,
            Summary = summary
        };
    }

    private static string ResolveVerdict(long total, long coldStart, long indexBuild, long lockWait, long modelLoad)
    {
        if (indexBuild > 0 && indexBuild >= total * 0.5) return "词法索引构建（冷启动）";
        if (coldStart > 0 && coldStart >= total * 0.5) return "冷启动";
        // 模型推理排在锁竞争之前：它是"外部模型慢"，不是"系统在排队"，两者处置方式完全不同
        if (modelLoad > 0 && modelLoad >= total * 0.3) return "模型推理 / 加载";
        if (lockWait > 0 && lockWait >= total * 0.3) return "锁竞争 / 排队";
        if (total < 120) return "正常";
        return "正常（长尾）";
    }
}

/// <summary>
/// 请求级耗时链的 AsyncLocal 载体。
/// 说明：这里使用 AsyncLocal 仅用于"环境传递"，不依赖任何编译期 AOP 织入；
/// 采集点全部显式写在业务代码里，因此不会出现代理类绕过导致的静默丢数据。
/// </summary>
public static class SearchTimeline
{
    private static readonly AsyncLocal<TimingChainBuilder?> _current = new();
    private static readonly AsyncLocal<string?> _parentSpan = new();

    public static TimingChainBuilder? Current => _current.Value;

    public static bool IsActive => _current.Value != null;

    /// <summary>开启一次请求级耗时链；若已存在则复用（嵌套调用安全）。</summary>
    public static IDisposable BeginRequest()
    {
        if (_current.Value != null) return EmptyDisposable.Instance;
        _current.Value = new TimingChainBuilder();
        _parentSpan.Value = null;
        return new RequestScope();
    }

    /// <summary>开启一次请求级耗时链并返回构建器（调用方负责 Complete）。</summary>
    public static TimingChainBuilder BeginRequest(out IDisposable scope)
    {
        if (_current.Value != null)
        {
            scope = EmptyDisposable.Instance;
            return _current.Value;
        }
        _current.Value = new TimingChainBuilder();
        _parentSpan.Value = null;
        scope = new RequestScope();
        return _current.Value;
    }

    internal static string? ParentSpan
    {
        get => _parentSpan.Value;
        set => _parentSpan.Value = value;
    }

    internal sealed class RequestScope : IDisposable
    {
        public void Dispose()
        {
            _current.Value = null;
            _parentSpan.Value = null;
        }
    }

    internal sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// 计时作用域。关闭耗时链时为 default 结构体（零分配、Dispose 为空，可被 JIT 内联消除）。
/// 用法：using var _ = TimingScope.Begin("kb.fts.match", SpanCategory.Io, "FTS5 匹配");
/// </summary>
public readonly struct TimingScope : IDisposable
{
    private readonly SpanState? _state;

    private TimingScope(SpanState state) => _state = state;

    public static TimingScope Begin(string name, SpanCategory category, string? displayName = null)
    {
        var builder = SearchTimeline.Current;
        if (builder == null) return default;

        var id = builder.NextId();
        var parent = SearchTimeline.ParentSpan;
        var state = new SpanState
        {
            Builder = builder,
            Id = id,
            Name = name,
            DisplayName = displayName ?? name,
            Category = category,
            ParentId = parent,
            StartTimestamp = Stopwatch.GetTimestamp(),
            StartMs = builder.ElapsedMs
        };
        SearchTimeline.ParentSpan = id;
        return new TimingScope(state);
    }

    public bool IsActive => _state != null;

    public void SetInput(int count) { if (_state != null) _state.Input = count; }
    public void SetOutput(int count) { if (_state != null) _state.Output = count; }
    public void SetDetail(string detail) { if (_state != null) _state.Detail = detail; }
    public void MarkColdStart() { if (_state != null) _state.ColdStart = true; }

    public void AddAttribute(string key, string? value)
    {
        if (_state == null || string.IsNullOrEmpty(value)) return;
        _state.Attributes ??= new Dictionary<string, string>(StringComparer.Ordinal);
        _state.Attributes[key] = value!;
    }

    /// <summary>标注锁等待耗时（已包含在总耗时内，单独呈现）。</summary>
    public void SetWait(long ms) { if (_state != null) _state.WaitMs = ms; }

    public void Fail(Exception ex) { if (_state != null) _state.Detail = $"异常: {ex.Message}"; }

    public void Dispose()
    {
        if (_state == null) return;
        var elapsed = (long)Stopwatch.GetElapsedTime(_state.StartTimestamp).TotalMilliseconds;
        SearchTimeline.ParentSpan = _state.ParentId;

        if (_state.ColdStart) _state.Builder.AddColdStart(elapsed);
        if (_state.WaitMs > 0) _state.Builder.AddLockWait(_state.WaitMs);

        _state.Builder.AddSpan(new TimingSpan
        {
            Id = _state.Id,
            ParentId = _state.ParentId,
            Name = _state.Name,
            DisplayName = _state.DisplayName,
            Category = _state.Category,
            StartMs = _state.StartMs,
            DurationMs = Math.Max(0, elapsed),
            WaitMs = _state.WaitMs,
            ColdStart = _state.ColdStart,
            InputCount = _state.Input,
            OutputCount = _state.Output,
            Detail = _state.Detail,
            Attributes = _state.Attributes,
            StartTimestamp = _state.StartTimestamp
        });
    }

    private sealed class SpanState
    {
        public TimingChainBuilder Builder = null!;
        public string Id = string.Empty;
        public string Name = string.Empty;
        public string DisplayName = string.Empty;
        public SpanCategory Category;
        public string? ParentId;
        public long StartTimestamp;
        public long StartMs;
        public int? Input;
        public int? Output;
        public long WaitMs;
        public bool ColdStart;
        public string? Detail;
        public Dictionary<string, string>? Attributes;
    }
}
