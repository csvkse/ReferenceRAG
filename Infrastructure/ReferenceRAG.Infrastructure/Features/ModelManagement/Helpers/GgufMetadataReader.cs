using System.Text;

namespace ReferenceRAG.Core.Features.ModelManagement.Helpers;

/// <summary>
/// GGUF 模型元数据轻量化提取器
/// 无需完整加载模型，在毫秒级内从二进制文件头中快速提取 BlockCount（总层数）、架构与嵌入长度
/// </summary>
public static class GgufMetadataReader
{
    private const uint GgufMagic = 0x46554747; // "GGUF" in little-endian

    public record GgufHeaderInfo(
        string? Architecture,
        int? BlockCount,
        int? ContextLength,
        int? EmbeddingLength,
        ulong TensorCount,
        ulong MetadataCount
    );

    /// <summary>
    /// 从 GGUF 文件头快速读取元数据
    /// </summary>
    public static GgufHeaderInfo ReadMetadata(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new GgufHeaderInfo(null, null, null, null, 0, 0);
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

            if (fs.Length < 16)
            {
                return new GgufHeaderInfo(null, null, null, null, 0, 0);
            }

            var magic = reader.ReadUInt32();
            if (magic != GgufMagic)
            {
                return new GgufHeaderInfo(null, null, null, null, 0, 0);
            }

            var version = reader.ReadUInt32();
            var tensorCount = reader.ReadUInt64();
            var metadataCount = reader.ReadUInt64();

            string? arch = null;
            int? blockCount = null;
            int? contextLength = null;
            int? embeddingLength = null;

            // 限制最多扫描 200 个元数据项或 2MB，防止读取过大头部
            var itemsToScan = Math.Min(metadataCount, 200UL);
            for (ulong i = 0; i < itemsToScan; i++)
            {
                if (fs.Position >= fs.Length - 8 || fs.Position > 2 * 1024 * 1024)
                {
                    break;
                }

                var key = ReadGgufString(reader);
                if (string.IsNullOrEmpty(key)) break;

                var valueType = reader.ReadUInt32();

                if (key.Equals("general.architecture", StringComparison.OrdinalIgnoreCase))
                {
                    arch = ReadGgufValueAsString(reader, valueType);
                }
                else if (key.EndsWith(".block_count", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("block_count", StringComparison.OrdinalIgnoreCase))
                {
                    blockCount = ReadGgufValueAsInt(reader, valueType);
                }
                else if (key.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase))
                {
                    contextLength = ReadGgufValueAsInt(reader, valueType);
                }
                else if (key.EndsWith(".embedding_length", StringComparison.OrdinalIgnoreCase))
                {
                    embeddingLength = ReadGgufValueAsInt(reader, valueType);
                }
                else
                {
                    SkipGgufValue(reader, valueType);
                }

                // 如果关键数据已全部获取，可提前退出
                if (blockCount.HasValue && arch != null)
                {
                    break;
                }
            }

            return new GgufHeaderInfo(arch, blockCount, contextLength, embeddingLength, tensorCount, metadataCount);
        }
        catch
        {
            return new GgufHeaderInfo(null, null, null, null, 0, 0);
        }
    }

    private static string? ReadGgufString(BinaryReader reader)
    {
        var len = reader.ReadUInt64();
        if (len == 0 || len > 1024) return "";
        var bytes = reader.ReadBytes((int)len);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string? ReadGgufValueAsString(BinaryReader reader, uint valueType)
    {
        if (valueType == 8) // STRING
        {
            return ReadGgufString(reader);
        }
        SkipGgufValue(reader, valueType);
        return null;
    }

    private static int? ReadGgufValueAsInt(BinaryReader reader, uint valueType)
    {
        return valueType switch
        {
            0 => reader.ReadByte(),
            1 => reader.ReadSByte(),
            2 => reader.ReadUInt16(),
            3 => reader.ReadInt16(),
            4 => (int)reader.ReadUInt32(),
            5 => reader.ReadInt32(),
            10 => (int)reader.ReadUInt64(),
            11 => (int)reader.ReadInt64(),
            _ => (int?)SkipAndReturnNull(reader, valueType)
        };
    }

    private static object? SkipAndReturnNull(BinaryReader reader, uint valueType)
    {
        SkipGgufValue(reader, valueType);
        return null;
    }

    private static void SkipGgufValue(BinaryReader reader, uint valueType)
    {
        switch (valueType)
        {
            case 0: reader.ReadByte(); break; // UINT8
            case 1: reader.ReadSByte(); break; // INT8
            case 2: reader.ReadUInt16(); break; // UINT16
            case 3: reader.ReadInt16(); break; // INT16
            case 4: reader.ReadUInt32(); break; // UINT32
            case 5: reader.ReadInt32(); break; // INT32
            case 6: reader.ReadSingle(); break; // FLOAT32
            case 7: reader.ReadBoolean(); break; // BOOL
            case 8: // STRING
                var strLen = reader.ReadUInt64();
                if (strLen > 0) reader.BaseStream.Seek((long)strLen, SeekOrigin.Current);
                break;
            case 9: // ARRAY
                var itemType = reader.ReadUInt32();
                var itemCount = reader.ReadUInt64();
                for (ulong j = 0; j < itemCount; j++)
                {
                    SkipGgufValue(reader, itemType);
                }
                break;
            case 10: reader.ReadUInt64(); break; // UINT64
            case 11: reader.ReadInt64(); break; // INT64
            case 12: reader.ReadDouble(); break; // FLOAT64
        }
    }
}
