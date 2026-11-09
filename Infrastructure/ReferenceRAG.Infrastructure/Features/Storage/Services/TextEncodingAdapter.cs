using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReferenceRAG.Infrastructure.Features.Storage.Services;

/// <summary>
/// 文本编码适配器（适配器模式）：
/// 自适应探测并转码常见中文文本文件（UTF-8, UTF-8-BOM, GBK, GB18030, UTF-16），
/// 彻底根除因无 BOM 的 GBK 中文文本默认被按 UTF-8 读取而造成的 \uFFFD 乱码问题。
/// </summary>
public static class TextEncodingAdapter
{
    static TextEncodingAdapter()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // 兼容已注册或平台受限场景
        }
    }

    /// <summary>
    /// 读取文本内容并自动探测与适配编码
    /// </summary>
    public static async Task<string> ReadAllTextWithAutoEncodingAsync(string filePath, CancellationToken ct = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath, ct);
        return DetectAndDecode(bytes);
    }

    /// <summary>
    /// 读取文本内容并自动探测与适配编码（带文件共享与指数退避重试）
    /// </summary>
    public static async Task<string?> SafeReadFileAsync(string filePath, CancellationToken ct = default)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms, ct);
                return DetectAndDecode(ms.ToArray());
            }
            catch (IOException) when (i < 2)
            {
                await Task.Delay(100 * (i + 1), ct);
            }
        }
        return null;
    }

    /// <summary>
    /// 核心解码判定逻辑：
    /// 1. BOM 优先探测
    /// 2. 严格模式 UTF-8 校验（若遇非法序列立即触发 Fallback）
    /// 3. 回退至 GB18030（全面向下兼容 GBK 与 GB2312）
    /// </summary>
    public static string DetectAndDecode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;

        // 1. 显式 BOM 探测
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes.Slice(3));
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes.Slice(2));
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes.Slice(2));
        }

        // 2. 尝试严格模式的 UTF-8 解码
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 3. 严格 UTF-8 校验失败，回退至 GB18030 (CP936 / GBK)
            try
            {
                var gbk = Encoding.GetEncoding("GB18030");
                return gbk.GetString(bytes);
            }
            catch
            {
                // 最终保底回退
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }
}
