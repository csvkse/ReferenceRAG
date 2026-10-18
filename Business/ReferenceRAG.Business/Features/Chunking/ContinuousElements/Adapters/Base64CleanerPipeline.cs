using System.Text.RegularExpressions;

namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Adapters;

/// <summary>
/// 巨型内联 Base64 与多媒体数据前置清洗管道
/// 杜绝成千上万字符的无意义 Base64 编码挤爆 Token 预算与向量库
/// </summary>
public static class Base64CleanerPipeline
{
    private static readonly Regex InlineBase64ImageRegex = new(
        @"data:image\/[a-zA-Z0-9\+\-\.]+;base64,[A-Za-z0-9+/=]{100,}",
        RegexOptions.Compiled);

    /// <summary>
    /// 对文档内容执行 Base64 清洗，将巨型二进制编码替换为高信息量占位符
    /// </summary>
    public static string Clean(string content)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains("data:image/"))
        {
            return content;
        }

        return InlineBase64ImageRegex.Replace(content, match =>
        {
            var sizeKb = match.Length / 1024.0;
            return $"[内联图片数据: 大小约 {sizeKb:F1} KB]";
        });
    }
}
