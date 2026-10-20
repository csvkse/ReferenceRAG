namespace ReferenceRAG.Business.Features.Chat.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// 多模态消息适配器接口：负责将多模态请求（文本、Base64 图片、图片 URL 等）标准化转换为 Microsoft.Extensions.AI 的 ChatMessage
/// </summary>
public interface IMultimodalMessageAdapter
{
    /// <summary>
    /// 将前端传入的用户文本和图片列表组装为统一的 ChatMessage
    /// </summary>
    ChatMessage AdaptUserMessage(string? text, IReadOnlyList<string>? rawImages);

    /// <summary>
    /// 解析单张图片数据为 AIContent
    /// </summary>
    AIContent? ParseImageContent(string rawImage);

    /// <summary>
    /// 校验图片有效性（单图大小限制，数量限制）
    /// </summary>
    (bool IsValid, string? Error) ValidateImages(IReadOnlyList<string>? rawImages, int maxImages = 4, long maxBytesPerImage = 8 * 1024 * 1024);
}

/// <summary>
/// 多模态消息适配器默认实现
/// 支持 Base64 Data URI (data:image/png;base64,...)、纯 Base64、HTTP/HTTPS URL 以及本地物理文件路径
/// </summary>
public class MultimodalMessageAdapter : IMultimodalMessageAdapter
{
    private readonly ILogger? _logger;
    private static readonly Regex DataUriRegex = new(
        @"^data:(?<mime>[\w\/\.\+\-]+);base64,(?<data>.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public MultimodalMessageAdapter(ILogger? logger = null)
    {
        _logger = logger;
    }

    public ChatMessage AdaptUserMessage(string? text, IReadOnlyList<string>? rawImages)
    {
        var safeText = text ?? string.Empty;

        if (rawImages == null || rawImages.Count == 0)
        {
            return new ChatMessage(ChatRole.User, safeText);
        }

        var contents = new List<AIContent>();

        if (!string.IsNullOrWhiteSpace(safeText))
        {
            contents.Add(new TextContent(safeText));
        }

        foreach (var raw in rawImages)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var imageContent = ParseImageContent(raw);
            if (imageContent != null)
            {
                contents.Add(imageContent);
            }
        }

        if (contents.Count == 0)
        {
            return new ChatMessage(ChatRole.User, safeText);
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    public AIContent? ParseImageContent(string rawImage)
    {
        if (string.IsNullOrWhiteSpace(rawImage)) return null;

        try
        {
            var trimmed = rawImage.Trim();

            // 1. Data URI 格式: data:image/png;base64,...
            var match = DataUriRegex.Match(trimmed);
            if (match.Success)
            {
                var mime = match.Groups["mime"].Value;
                var base64 = match.Groups["data"].Value;
                var bytes = Convert.FromBase64String(base64);
                return new DataContent(bytes, mime);
            }

            // 2. HTTP / HTTPS URL
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
                {
                    return new UriContent(uri, "image/jpeg");
                }
            }

            // 3. 本地磁盘物理路径
            if (File.Exists(trimmed))
            {
                var bytes = File.ReadAllBytes(trimmed);
                var ext = Path.GetExtension(trimmed).ToLowerInvariant();
                var mime = ext switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    _ => "image/jpeg"
                };
                return new DataContent(bytes, mime);
            }

            // 4. 纯 Base64 字符串（尝试兜底解码）
            if (trimmed.Length >= 32 && !trimmed.Contains(' ') && !trimmed.Contains('\n') && !trimmed.Contains('\r'))
            {
                var bytes = Convert.FromBase64String(trimmed);
                return new DataContent(bytes, "image/jpeg");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[MultimodalMessageAdapter] 解析图片内容失败，已跳过损坏的图像数据");
        }

        return null;
    }

    public (bool IsValid, string? Error) ValidateImages(
        IReadOnlyList<string>? rawImages,
        int maxImages = 4,
        long maxBytesPerImage = 8 * 1024 * 1024)
    {
        if (rawImages == null || rawImages.Count == 0)
        {
            return (true, null);
        }

        if (rawImages.Count > maxImages)
        {
            return (false, $"单次发送图片数量不能超过 {maxImages} 张（当前为 {rawImages.Count} 张）");
        }

        for (int i = 0; i < rawImages.Count; i++)
        {
            var raw = rawImages[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;

            long approximateBytes = 0;
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var commaIdx = raw.IndexOf(',');
                if (commaIdx >= 0)
                {
                    approximateBytes = (long)((raw.Length - commaIdx - 1) * 0.75);
                }
            }
            else if (File.Exists(raw))
            {
                approximateBytes = new FileInfo(raw).Length;
            }

            if (approximateBytes > maxBytesPerImage)
            {
                var maxMb = maxBytesPerImage / (1024 * 1024);
                return (false, $"第 {i + 1} 张图片大小超过上限 ({maxMb} MB)");
            }
        }

        return (true, null);
    }
}
