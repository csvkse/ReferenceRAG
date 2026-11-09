using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using JiebaNet.Segmenter;

namespace ReferenceRAG.Storage;

/// <summary>
/// 结巴分词有界内存 LRU 缓存与噪音过滤代理 (借鉴 SAG-Wiki TokenCache & indexable_ranges)
/// 提供：
/// 1. 毫秒级 LRU 词表缓存，避免高频重复分词 CPU 开销；
/// 2. Markdown/HTML 源码内嵌 Base64 数据与 SVG 噪音过滤；
/// 3. 中英文统一停用词过滤与空格归一化。
/// </summary>
public sealed class TokenizationCacheProxy
{
    private static readonly Lazy<TokenizationCacheProxy> _defaultInstance =
        new(() => new TokenizationCacheProxy());

    public static TokenizationCacheProxy Default => _defaultInstance.Value;

    private readonly JiebaSegmenter _segmenter = new();
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly int _maxEntries;
    private long _accessCounter = 0;

    // 过滤 Markdown / HTML 中的内嵌 Base64 数据 URI (data:image/...;base64,...)
    private static readonly Regex Base64DataUriRegex = new(
        @"data:(?:image|font|audio|video|application)/[a-zA-Z0-9.+-]+;base64,[a-zA-Z0-9+/=]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 过滤 SVG 标签块 (<svg ...>...</svg>)
    private static readonly Regex SvgBlockRegex = new(
        @"<svg\b[^>]*>[\s\S]*?</svg>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // 英文停用词
        "a", "an", "the", "and", "or", "but", "in", "on", "at", "to", "for",
        "of", "with", "by", "from", "is", "are", "was", "were", "be", "been",
        "being", "have", "has", "had", "do", "does", "did", "will", "would",
        "could", "should", "may", "might", "must", "shall", "can", "need",
        "it", "its", "this", "that", "these", "those", "i", "you", "he",
        "she", "we", "they", "what", "which", "who", "when", "where", "why",
        "how", "all", "each", "every", "both", "few", "more", "most", "other",
        "some", "such", "no", "not", "only", "same", "so", "than", "too",
        "very", "just", "as", "if", "then", "because", "while", "although",
        // 中文停用词
        "的", "了", "在", "是", "我", "有", "和", "就", "不", "人", "都",
        "一", "一个", "上", "也", "很", "到", "说", "要", "去", "你", "会",
        "着", "没有", "看", "好", "自己", "这", "那", "里", "以", "来",
        "对", "与", "或", "但", "因为", "所以", "如果", "虽然", "但是",
        "然后", "而且", "而", "及", "等", "被", "把", "让", "使", "为",
        "将", "于", "之", "其", "此", "该", "各", "某", "哪", "什么", "怎么"
    };

    private sealed class CacheEntry
    {
        public required IReadOnlyList<string> Tokens { get; init; }
        public required string IndexedString { get; init; }
        public long LastAccess { get; set; }
    }

    public TokenizationCacheProxy(int maxEntries = 10000)
    {
        _maxEntries = Math.Max(maxEntries, 500);
    }

    /// <summary>
    /// 对输入文本进行清洗、分词、停用词剔除，并利用 LRU 缓存加速
    /// </summary>
    public IReadOnlyList<string> Tokenize(string text, bool deduplicate = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        // 超大文本（>8KB）不驻留 LRU 缓存，直接即时分词，防止大对象长期占据 Gen2 内存
        if (text.Length > 8192)
        {
            var sanitizedLarge = SanitizeText(text);
            return TokenizeCore(sanitizedLarge, deduplicate);
        }

        var currentAccess = System.Threading.Interlocked.Increment(ref _accessCounter);

        if (_cache.TryGetValue(text, out var entry))
        {
            entry.LastAccess = currentAccess;
            return deduplicate ? entry.Tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : entry.Tokens;
        }

        var sanitized = SanitizeText(text);
        var tokens = TokenizeCore(sanitized, false);
        var indexedString = string.Join(" ", tokens);

        EnsureCapacity();

        _cache[text] = new CacheEntry
        {
            Tokens = tokens,
            IndexedString = indexedString,
            LastAccess = currentAccess
        };

        return deduplicate ? tokens.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : tokens;
    }

    /// <summary>
    /// 获取适用于 FTS 索引插入的空格拼接分词串
    /// </summary>
    public string TokenizeForIndex(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        if (text.Length > 8192)
        {
            var tokens = TokenizeCore(SanitizeText(text), false);
            return string.Join(" ", tokens);
        }

        var currentAccess = System.Threading.Interlocked.Increment(ref _accessCounter);

        if (_cache.TryGetValue(text, out var entry))
        {
            entry.LastAccess = currentAccess;
            return entry.IndexedString;
        }

        var sanitized = SanitizeText(text);
        var calculatedTokens = TokenizeCore(sanitized, false);
        var calculatedString = string.Join(" ", calculatedTokens);

        EnsureCapacity();

        _cache[text] = new CacheEntry
        {
            Tokens = calculatedTokens,
            IndexedString = calculatedString,
            LastAccess = currentAccess
        };

        return calculatedString;
    }

    /// <summary>
    /// 清理 Markdown/HTML 中的内嵌 Base64 和 SVG 噪音块
    /// </summary>
    public static string SanitizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var cleaned = text;
        if (cleaned.Contains("data:image", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains(";base64,", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = Base64DataUriRegex.Replace(cleaned, " ");
        }

        if (cleaned.Contains("<svg", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = SvgBlockRegex.Replace(cleaned, " ");
        }

        return cleaned;
    }

    private IReadOnlyList<string> TokenizeCore(string text, bool deduplicate)
    {
        var rawTokens = _segmenter.CutForSearch(text)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0 && !string.IsNullOrWhiteSpace(t) && !StopWords.Contains(t));

        if (deduplicate)
        {
            rawTokens = rawTokens.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        return rawTokens.ToArray();
    }

    private void EnsureCapacity()
    {
        if (_cache.Count <= _maxEntries) return;

        // 达到上限时，批量淘汰最久未访问的 20% 条目
        var toRemoveCount = _maxEntries / 5;
        var oldestKeys = _cache
            .OrderBy(kv => kv.Value.LastAccess)
            .Take(toRemoveCount)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in oldestKeys)
        {
            _cache.TryRemove(key, out _);
        }
    }

    /// <summary>清空缓存（主要供单元测试使用）</summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>获取当前缓存项数量</summary>
    public int CacheCount => _cache.Count;
}
