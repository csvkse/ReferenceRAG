using System;
using System.Text.RegularExpressions;

namespace ReferenceRAG.Infrastructure.Features.FileSystem;

/// <summary>
/// 单行文本匹配策略接口
/// </summary>
public interface IFileSearchStrategy
{
    bool MatchesLine(ReadOnlySpan<char> line, out int matchIndex, out int matchLength);
}

/// <summary>
/// 极速纯字串匹配策略（SIMD 向量化，零堆分配，比正则快 10~50 倍）
/// </summary>
public sealed class ExactSubstringStrategy : IFileSearchStrategy
{
    private readonly string _pattern;
    private readonly StringComparison _comparison;

    public ExactSubstringStrategy(string pattern, bool caseSensitive)
    {
        _pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    public bool MatchesLine(ReadOnlySpan<char> line, out int matchIndex, out int matchLength)
    {
        var idx = line.IndexOf(_pattern.AsSpan(), _comparison);
        if (idx >= 0)
        {
            matchIndex = idx;
            matchLength = _pattern.Length;
            return true;
        }

        matchIndex = -1;
        matchLength = 0;
        return false;
    }
}

/// <summary>
/// 安全非回溯正则表达式匹配策略（线性时间复杂度 O(N)，杜绝 ReDoS 攻击，并挂载 3 秒硬性熔断）
/// </summary>
public sealed class NonBacktrackingRegexStrategy : IFileSearchStrategy
{
    private readonly Regex _regex;

    public NonBacktrackingRegexStrategy(string pattern, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new ArgumentException("搜索正则模式不能为空", nameof(pattern));

        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        if (!caseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        // 绑定 3 秒硬性超时断路器
        _regex = new Regex(pattern, options, TimeSpan.FromSeconds(3));
    }

    public bool MatchesLine(ReadOnlySpan<char> line, out int matchIndex, out int matchLength)
    {
        var enumerator = _regex.EnumerateMatches(line);
        if (enumerator.MoveNext())
        {
            matchIndex = enumerator.Current.Index;
            matchLength = enumerator.Current.Length;
            return true;
        }

        matchIndex = -1;
        matchLength = 0;
        return false;
    }
}

/// <summary>
/// 策略工厂：根据请求参数动态分发最佳匹配引擎
/// </summary>
public static class SearchStrategyFactory
{
    public static IFileSearchStrategy CreateStrategy(string pattern, bool isRegex, bool caseSensitive)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            throw new ArgumentException("搜索关键词或正则模式不能为空", nameof(pattern));
        }

        if (isRegex)
        {
            try
            {
                return new NonBacktrackingRegexStrategy(pattern, caseSensitive);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"正则表达式语法错误: {ex.Message}", ex);
            }
        }

        return new ExactSubstringStrategy(pattern, caseSensitive);
    }
}
