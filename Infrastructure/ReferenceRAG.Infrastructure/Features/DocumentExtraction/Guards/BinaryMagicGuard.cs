using System;

namespace ReferenceRAG.Core.Features.DocumentExtraction.Guards;

/// <summary>
/// 二进制文件魔数嗅探门禁（G-INGEST-MAGIC-01）
/// 在 I/O 入口拦截将 PDF 或已知二进制流作为纯文本流读取的错误行为
/// </summary>
public static class BinaryMagicGuard
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04]; // docx, xlsx, pptx, zip
    private static readonly byte[] RarMagic = [0x52, 0x61, 0x72, 0x21]; // Rar!
    private static readonly byte[] SevenZMagic = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]; // 7z
    private static readonly byte[] ElfMagic = [0x7F, 0x45, 0x4C, 0x46]; // ELF
    private static readonly byte[] ExeMagic = [0x4D, 0x5A]; // MZ / PE

    /// <summary>
    /// 判断文件头是否匹配标准 PDF 魔数 (%PDF-)
    /// </summary>
    public static bool IsPdf(ReadOnlySpan<byte> header) =>
        header.Length >= 5 && header.StartsWith(PdfMagic);

    /// <summary>
    /// 判断数据头是否包含已知的非纯文本二进制签名或空字节控制符
    /// </summary>
    public static bool IsKnownBinary(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty) return false;
        if (IsPdf(header)) return true;
        if (header.Length >= 4 && header.StartsWith(ZipMagic)) return true;
        if (header.Length >= 4 && header.StartsWith(RarMagic)) return true;
        if (header.Length >= 6 && header.StartsWith(SevenZMagic)) return true;
        if (header.Length >= 4 && header.StartsWith(ElfMagic)) return true;
        if (header.Length >= 2 && header.StartsWith(ExeMagic)) return true;

        // 启发式检测：检查前 512 字节是否存在空字节 \0
        int checkLen = Math.Min(header.Length, 512);
        for (int i = 0; i < checkLen; i++)
        {
            if (header[i] == 0x00) return true;
        }

        return false;
    }
}
