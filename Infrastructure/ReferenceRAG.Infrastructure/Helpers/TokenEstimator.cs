using System.Text;

namespace ReferenceRAG.Core.Helpers;

/// <summary>
/// Token 估算工具类 - 统一的 token 数量估算逻辑（唯一实现）。
/// SimpleTokenizer 委托本类，避免两套口径漂移。
/// 口径对齐 BERT/BGE 类 tokenizer 的保守估算：
///   中文 1 字符/token，英文 ~4 字符/token，数字 ~3 字符/token，其它字符 ~2 字符/token。
/// </summary>
public static class TokenEstimator
{
    /// <summary>
    /// 估算文本的 token 数量（中英混合）
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var chineseCount = 0;
        var englishChars = 0;
        var numberChars = 0;
        var otherChars = 0;

        foreach (var c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF)
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
            else if (!char.IsWhiteSpace(c))
            {
                // 空白/换行不产生独立 token（对齐 BERT/BGE 类 tokenizer 行为），只统计有效其它字符
                otherChars++;
            }
        }

        return chineseCount + englishChars / 4 + numberChars / 3 + otherChars / 2;
    }
}