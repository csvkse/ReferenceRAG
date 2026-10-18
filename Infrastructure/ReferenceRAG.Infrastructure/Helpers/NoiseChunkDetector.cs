using System.Text.RegularExpressions;

namespace ReferenceRAG.Core.Helpers;

/// <summary>
/// 纯噪声与分隔符分块检测器
/// 负责识别无独立语义内容的分隔符（如 === (43-48) ===, ---, *** 等）或微小标点碎片
/// </summary>
public static class NoiseChunkDetector
{
    private static readonly Regex SeparatorPattern = new(
        @"^(?:[-*#=_~]{3,}|={3,}\s*(?:\(\d+(?:-\d+)?\))?\s*={3,}|[-*]\s*[-*]\s*[-*])$",
        RegexOptions.Compiled);

    /// <summary>
    /// 判断文本是否为无独立语义的分隔符、装饰线或微小噪声碎片（如 === (43-48) ===, ---, ***, === 等）
    /// </summary>
    public static bool IsNoiseOrSeparator(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return true;

        // 包含代码块围栏的不视为噪音
        if (trimmed.StartsWith("```") || trimmed.EndsWith("```")) return false;

        // 匹配各种形式的分隔符，例如 === (43-48) ===, ---, ***, ===, ___ 等
        if (SeparatorPattern.IsMatch(trimmed)) return true;

        // 统计有效语义字符（汉字、字母、数字）数量
        int semanticCharCount = 0;
        foreach (char c in trimmed)
        {
            if (char.IsLetterOrDigit(c) || (c >= 0x4E00 && c <= 0x9FA5))
            {
                semanticCharCount++;
            }
        }

        // 若有效语义字符极少（少于 4 个）且总文本极短（少于 25 个字符），通常为章节编号标记或标点符号
        if (semanticCharCount < 4 && trimmed.Length < 25)
        {
            return true;
        }

        return false;
    }
}
