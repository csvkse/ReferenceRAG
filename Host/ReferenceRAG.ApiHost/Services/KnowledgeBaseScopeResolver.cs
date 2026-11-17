using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;

namespace ReferenceRAG.ApiHost.Services;

/// <summary>
/// 目标作用域种类：Single = 指定单库；All = 全部启用库。
/// </summary>
public enum KnowledgeBaseScopeKind
{
    Single = 0,
    All = 1
}

/// <summary>
/// 一次请求解析出的知识库作用域。
/// <see cref="RequestedRaw"/> 保留调用方原值，用于响应回显（让作用域可见而不是靠猜）。
/// </summary>
public sealed record KnowledgeBaseScope(
    KnowledgeBaseScopeKind Kind,
    IReadOnlyList<string> Ids,
    string? RequestedRaw)
{
    public bool IsAll => Kind == KnowledgeBaseScopeKind.All;

    /// <summary>协议层面的显式「全部」哨兵：前端下拉用它表达全量意图。</summary>
    public const string AllSentinel = "__all__";

    public string KindText => Kind == KnowledgeBaseScopeKind.All ? "all" : "single";
}

/// <summary>作用域解析失败：映射为 HTTP 400（调用方未给出作用域）。</summary>
public sealed class KnowledgeBaseScopeRequiredException : Exception
{
    public KnowledgeBaseScopeRequiredException()
        : base("必须显式指定 kbId 或 \"__all__\"：写操作不接受「默认第一个知识库」的隐式作用域")
    {
    }
}

/// <summary>作用域解析失败：映射为 HTTP 404（知识库不存在）。</summary>
public sealed class KnowledgeBaseScopeNotFoundException : Exception
{
    public KnowledgeBaseScopeNotFoundException(string kbId)
        : base($"知识库 '{kbId}' 不存在")
    {
        KbId = kbId;
    }

    public string KbId { get; }
}

/// <summary>
/// 知识库作用域解析器（Adapter）：把协议侧四种写法收敛成两个明确结果。
///
/// 存在理由：此前 <c>ResolveKbIdAsync</c> 把「未指定」和「__all__」都解释成
/// <c>kbs.FirstOrDefault(k => k.Enabled)</c> —— 于是「全部知识库」显示的是第一个库的数字，
/// 而「不传参」又静默改动了作用域。这里只有一种解释：
/// <list type="bullet">
///   <item>读接口：null / "" / "__all__" → <see cref="KnowledgeBaseScopeKind.All"/></item>
///   <item>写接口：null / "" → <see cref="KnowledgeBaseScopeRequiredException"/>（不替调用方做选择）</item>
///   <item>未知 ID → <see cref="KnowledgeBaseScopeNotFoundException"/>，且**不触碰存储层**（不会建出空 kb.db）</item>
/// </list>
/// </summary>
public static class KnowledgeBaseScopeResolver
{
    /// <summary>
    /// 读接口用法。省略作用域读全部启用库（与 Elasticsearch 省略索引目标等价）。
    /// </summary>
    public static async Task<KnowledgeBaseScope> ResolveReadAsync(
        ICatalogStore? catalog, string? raw, CancellationToken ct = default)
    {
        var trimmed = raw?.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed == KnowledgeBaseScope.AllSentinel)
        {
            return new KnowledgeBaseScope(KnowledgeBaseScopeKind.All, await EnabledIdsAsync(catalog, ct), raw);
        }

        return await SingleAsync(catalog, trimmed, raw, ct);
    }

    /// <summary>
    /// 写接口用法（重建 / 清空 / 建索引）。省略作用域直接拒绝：写操作必须能回答「到底动哪个库」。
    /// </summary>
    public static async Task<KnowledgeBaseScope> ResolveWriteAsync(
        ICatalogStore? catalog, string? raw, CancellationToken ct = default)
    {
        var trimmed = raw?.Trim();

        if (string.IsNullOrEmpty(trimmed))
            throw new KnowledgeBaseScopeRequiredException();

        if (trimmed == KnowledgeBaseScope.AllSentinel)
        {
            return new KnowledgeBaseScope(KnowledgeBaseScopeKind.All, await EnabledIdsAsync(catalog, ct), raw);
        }

        return await SingleAsync(catalog, trimmed, raw, ct);
    }

    private static async Task<KnowledgeBaseScope> SingleAsync(
        ICatalogStore? catalog, string id, string? raw, CancellationToken ct)
    {
        // 只在目录里校验存在性，绝不先碰 IKnowledgeBaseStorageManager：
        // 后者按 id 惰性建库（GetOrAdd），未知 ID 会凭空生成 kbs/<id>/kb.db。
        // 用一次 ListKnowledgeBasesAsync 同时服务「存在性」与「启用清单」，避免两次往返。
        if (catalog is not null)
        {
            var kbs = await catalog.ListKnowledgeBasesAsync(ct);
            if (!kbs.Any(k => string.Equals(k.Id, id, StringComparison.Ordinal)))
                throw new KnowledgeBaseScopeNotFoundException(id);
        }

        return new KnowledgeBaseScope(KnowledgeBaseScopeKind.Single, new[] { id }, raw);
    }

    private static async Task<IReadOnlyList<string>> EnabledIdsAsync(ICatalogStore? catalog, CancellationToken ct)
    {
        if (catalog is null) return Array.Empty<string>();
        var kbs = await catalog.ListKnowledgeBasesAsync(ct);
        return kbs.Where(k => k.Enabled).Select(k => k.Id).ToList();
    }
}
