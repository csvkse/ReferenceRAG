using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Core.Services;

/// <summary>
/// 简单 Token 计数器（估算）- 线程安全，支持并行，低内存分配
/// </summary>
public class SimpleTokenizer : ITokenizer
{
    // 预编译正则（线程安全）
    private static readonly Regex ChineseRegex = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex EnglishWordRegex = new(@"[a-zA-Z]+", RegexOptions.Compiled);
    private static readonly Regex NumberRegex = new(@"\d+", RegexOptions.Compiled);

    /// <summary>
    /// 计算 token 数量（低分配版本）
    /// </summary>
    public int CountTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        // 使用 Span 直接遍历，避免正则分配
        return CountTokensSpan(text.AsSpan());
    }

    /// <summary>
    /// Span 版本的 token 计数（零分配）
    /// </summary>
    private static int CountTokensSpan(ReadOnlySpan<char> text)
    {
        var chineseCount = 0;
        var englishChars = 0;
        var numberChars = 0;

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (IsChineseFast(c))
            {
                chineseCount++;
            }
            else if (char.IsAsciiLetter(c))
            {
                englishChars++;
            }
            else if (char.IsAsciiDigit(c))
            {
                numberChars++;
            }
        }

        // 计算各部分 token
        var chineseTokens = (int)(chineseCount / 1.5);
        var englishTokens = (int)(englishChars / 4.0);
        var numberTokens = (int)(numberChars / 3.0);
        var otherChars = text.Length - chineseCount - englishChars - numberChars;
        var otherTokens = (int)(otherChars / 2.0);

        return chineseTokens + englishTokens + numberTokens + otherTokens;
    }

    /// <summary>
    /// 快速判断中文字符（内联优化）
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsChineseFast(char c)
    {
        return (uint)(c - 0x4E00) <= (0x9FFF - 0x4E00);
    }

    /// <summary>
    /// 批量计算 token 数量（并行优化）
    /// </summary>
    public Dictionary<string, int> CountTokensBatch(IEnumerable<string> texts)
    {
        var textList = texts.ToList();
        if (textList.Count == 0) return new Dictionary<string, int>();

        // 小批量直接顺序处理，避免并行开销
        if (textList.Count < 10)
        {
            var result = new Dictionary<string, int>(textList.Count);
            foreach (var text in textList)
            {
                result[text] = CountTokens(text);
            }
            return result;
        }

        // 大批量并行处理
        var concurrentResult = new ConcurrentDictionary<string, int>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8)
        };

        Parallel.ForEach(textList, options, text =>
        {
            concurrentResult[text] = CountTokens(text);
        });

        return new Dictionary<string, int>(concurrentResult);
    }
}
