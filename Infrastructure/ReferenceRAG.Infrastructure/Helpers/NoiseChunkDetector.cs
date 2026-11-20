using System;
using System.Text.RegularExpressions;

namespace ReferenceRAG.Core.Helpers;

/// <summary>
/// 纯噪声、乱码与分隔符分块检测器（准入与质量门禁）
/// 负责识别无独立语义内容的分隔符、PDF容器内部语法标记（G-INGEST-TOKEN-02）以及高乱码率切片（G-CHUNK-CORRUPT-04）
/// </summary>
public static class NoiseChunkDetector
{
    private static readonly Regex SeparatorPattern = new(
        @"^(?:[-*#=_~]{3,}|={3,}\s*(?:\(\d+(?:-\d+)?\))?\s*={3,}|[-*]\s*[-*]\s*[-*])$",
        RegexOptions.Compiled);

    private static readonly Regex PdfSyntaxPattern = new(
        @"(?:trailer\s*<<|startxref\s*\d+|%%EOF|\b\d+\s+\d+\s+obj\b|/Filter\s*/FlateDecode|/XObject\b|\bendobj\b|\bstream\s*[\r\n]+|\bxref\s*[\r\n]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 判断切片是否包含 PDF 底层对象容器标记或结构元数据泄露（门禁 G-INGEST-TOKEN-02）
    /// </summary>
    public static bool IsPdfSyntaxNoise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return PdfSyntaxPattern.IsMatch(text);
    }

    /// <summary>
    /// 判断切片是否包含超出阈值的不合法乱码字符或不可打印控制字符（门禁 G-CHUNK-CORRUPT-04）
    /// 若 Unicode 替换符 '\uFFFD' 或 ASCII 控制字符占比超过 2%，直接判定为损坏乱码
    /// </summary>
    public static bool IsCorruptedNoise(string text, double maxCorruptedRatio = 0.02)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;

        int corruptedCount = 0;
        foreach (char c in trimmed)
        {
            if (c == '\uFFFD' || (c < 32 && c != '\t' && c != '\r' && c != '\n'))
            {
                corruptedCount++;
            }
        }

        double ratio = (double)corruptedCount / trimmed.Length;
        return ratio > maxCorruptedRatio;
    }

    /// <summary>
    /// 组合检测：判断切片是否属于 PDF 容器元语法泄露或高乱码率切片
    /// </summary>
    public static bool IsCorruptedOrPdfSyntaxNoise(string text)
    {
        return IsPdfSyntaxNoise(text) || IsCorruptedNoise(text);
    }

    /// <summary>
    /// 判断文本是否为无独立语义的分隔符、装饰线、微小噪声碎片或 PDF 乱码
    /// </summary>
    public static bool IsNoiseOrSeparator(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return true;

        // 包含 PDF 语法泄露或高乱码率的切片，直接视为噪音拦截
        if (IsCorruptedOrPdfSyntaxNoise(trimmed)) return true;

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
