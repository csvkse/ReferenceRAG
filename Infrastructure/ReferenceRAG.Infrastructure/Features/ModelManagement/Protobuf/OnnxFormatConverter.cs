using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Core.Features.ModelManagement.Protobuf;

/// <summary>
/// 纯 C# 原生 ONNX 格式转换器（无需 Python / onnx 库）
/// 支持在 Embedded（单文件内嵌）与 External Data（外置数据文件 model.onnx + model.onnx_data）之间进行毫秒级转换
/// </summary>
public static class OnnxFormatConverter
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("OnnxFormatConverter");

    // ONNX Protobuf Wire Tags
    private const ulong Tag_Model_Graph = (7 << 3) | 2;
    private const ulong Tag_Graph_Initializer = (5 << 3) | 2;

    private const ulong Tag_Tensor_RawData = (9 << 3) | 2;
    private const ulong Tag_Tensor_ExternalData = (13 << 3) | 2;
    private const ulong Tag_Tensor_DataLocation = (14 << 3) | 0;

    private const ulong Tag_StringEntry_Key = (1 << 3) | 2;
    private const ulong Tag_StringEntry_Value = (2 << 3) | 2;

    /// <summary>
    /// 转换模型格式（自动定位模型目录下的 model.onnx 并执行转换）
    /// </summary>
    public static async Task<(bool Success, string? Error)> ConvertFormatAsync(
        string modelDir,
        string targetFormat,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(modelDir))
        {
            return (false, $"模型目录不存在: {modelDir}");
        }

        var onnxPath = Path.Combine(modelDir, "model.onnx");
        if (!File.Exists(onnxPath))
        {
            var subOnnx = Directory.GetFiles(modelDir, "model.onnx", SearchOption.AllDirectories).FirstOrDefault();
            if (subOnnx != null)
            {
                onnxPath = subOnnx;
            }
            else
            {
                return (false, "未找到 model.onnx 文件");
            }
        }

        var dir = Path.GetDirectoryName(onnxPath)!;
        var onnxDataPath = Path.Combine(dir, "model.onnx_data");

        try
        {
            progress?.Report(10);
            if (string.Equals(targetFormat, "external", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(onnxDataPath))
                {
                    return (false, "模型已是 external 格式（已存在 model.onnx_data）");
                }

                _logger.LogInformation("[OnnxFormatConverter] 正在将模型转换为 External 格式: {Path}", onnxPath);
                await ConvertEmbeddedToExternalAsync(onnxPath, onnxDataPath, 128, progress, cancellationToken);
                progress?.Report(100);
                return (true, null);
            }
            else if (string.Equals(targetFormat, "embedded", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(onnxDataPath))
                {
                    var altDataPath = Path.Combine(dir, "model.onnx.data");
                    if (File.Exists(altDataPath))
                    {
                        onnxDataPath = altDataPath;
                    }
                    else
                    {
                        return (false, "模型已是 embedded 格式（不存在外部数据文件）");
                    }
                }

                _logger.LogInformation("[OnnxFormatConverter] 正在将模型转换为 Embedded 格式: {Path}", onnxPath);
                await ConvertExternalToEmbeddedAsync(onnxPath, dir, progress, cancellationToken);
                progress?.Report(100);
                return (true, null);
            }
            else
            {
                return (false, $"未知目标格式: {targetFormat} (仅支持 embedded 或 external)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OnnxFormatConverter] 转换异常: {Message}", ex.Message);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 将内嵌单文件转换为外置数据文件格式 (Embedded -> External)
    /// </summary>
    public static async Task ConvertEmbeddedToExternalAsync(
        string inputOnnxPath,
        string outputDataPath,
        long thresholdBytes = 128,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tempOnnxPath = inputOnnxPath + ".tmp_" + Guid.NewGuid().ToString("N");
        var tempDataPath = outputDataPath + ".tmp_" + Guid.NewGuid().ToString("N");

        try
        {
            using (var inStream = new FileStream(inputOnnxPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            using (var outOnnxStream = new FileStream(tempOnnxPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            using (var outDataStream = new FileStream(tempDataPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                var dataFileName = Path.GetFileName(outputDataPath);
                var totalBytes = inStream.Length;

                while (inStream.Position < inStream.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tag = ReadVarint(inStream);
                    var wireType = (int)(tag & 0x07);

                    if (tag == Tag_Model_Graph)
                    {
                        // 处理 GraphProto
                        var graphLen = (long)ReadVarint(inStream);
                        var graphEnd = inStream.Position + graphLen;

                        using var tempGraphStream = new MemoryStream();
                        while (inStream.Position < graphEnd)
                        {
                            var gTag = ReadVarint(inStream);
                            var gWireType = (int)(gTag & 0x07);

                            if (gTag == Tag_Graph_Initializer)
                            {
                                var initLen = (long)ReadVarint(inStream);
                                var initEnd = inStream.Position + initLen;

                                var tensorBytes = TransformTensorToExternal(inStream, initEnd, outDataStream, dataFileName, thresholdBytes);
                                WriteVarint(tempGraphStream, Tag_Graph_Initializer);
                                WriteVarint(tempGraphStream, (ulong)tensorBytes.Length);
                                tempGraphStream.Write(tensorBytes, 0, tensorBytes.Length);
                            }
                            else
                            {
                                CopyField(inStream, tempGraphStream, gTag, gWireType);
                            }

                            if (totalBytes > 0 && progress != null)
                            {
                                var pct = 10f + (float)inStream.Position / totalBytes * 80f;
                                progress.Report(pct);
                            }
                        }

                        // 写入转换后的 GraphProto
                        WriteVarint(outOnnxStream, Tag_Model_Graph);
                        WriteVarint(outOnnxStream, (ulong)tempGraphStream.Length);
                        tempGraphStream.Position = 0;
                        await tempGraphStream.CopyToAsync(outOnnxStream, cancellationToken);
                    }
                    else
                    {
                        CopyField(inStream, outOnnxStream, tag, wireType);
                    }
                }
            }

            // 替换原始文件
            File.Move(tempOnnxPath, inputOnnxPath, true);
            File.Move(tempDataPath, outputDataPath, true);
        }
        finally
        {
            if (File.Exists(tempOnnxPath)) try { File.Delete(tempOnnxPath); } catch { }
            if (File.Exists(tempDataPath)) try { File.Delete(tempDataPath); } catch { }
        }
    }

    /// <summary>
    /// 将外置数据文件格式转换为内嵌单文件 (External -> Embedded)
    /// </summary>
    public static async Task ConvertExternalToEmbeddedAsync(
        string inputOnnxPath,
        string externalDataDir,
        IProgress<float>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tempOnnxPath = inputOnnxPath + ".tmp_" + Guid.NewGuid().ToString("N");
        string? dataFileToDelete = null;

        try
        {
            using (var inStream = new FileStream(inputOnnxPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            using (var outOnnxStream = new FileStream(tempOnnxPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                var totalBytes = inStream.Length;

                while (inStream.Position < inStream.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tag = ReadVarint(inStream);
                    var wireType = (int)(tag & 0x07);

                    if (tag == Tag_Model_Graph)
                    {
                        var graphLen = (long)ReadVarint(inStream);
                        var graphEnd = inStream.Position + graphLen;

                        using var tempGraphStream = new MemoryStream();
                        while (inStream.Position < graphEnd)
                        {
                            var gTag = ReadVarint(inStream);
                            var gWireType = (int)(gTag & 0x07);

                            if (gTag == Tag_Graph_Initializer)
                            {
                                var initLen = (long)ReadVarint(inStream);
                                var initEnd = inStream.Position + initLen;

                                var tensorBytes = TransformTensorToEmbedded(inStream, initEnd, externalDataDir, ref dataFileToDelete);
                                WriteVarint(tempGraphStream, Tag_Graph_Initializer);
                                WriteVarint(tempGraphStream, (ulong)tensorBytes.Length);
                                tempGraphStream.Write(tensorBytes, 0, tensorBytes.Length);
                            }
                            else
                            {
                                CopyField(inStream, tempGraphStream, gTag, gWireType);
                            }

                            if (totalBytes > 0 && progress != null)
                            {
                                var pct = 10f + (float)inStream.Position / totalBytes * 80f;
                                progress.Report(pct);
                            }
                        }

                        WriteVarint(outOnnxStream, Tag_Model_Graph);
                        WriteVarint(outOnnxStream, (ulong)tempGraphStream.Length);
                        tempGraphStream.Position = 0;
                        await tempGraphStream.CopyToAsync(outOnnxStream, cancellationToken);
                    }
                    else
                    {
                        CopyField(inStream, outOnnxStream, tag, wireType);
                    }
                }
            }

            File.Move(tempOnnxPath, inputOnnxPath, true);

            // 删除外部数据文件
            if (!string.IsNullOrEmpty(dataFileToDelete) && File.Exists(dataFileToDelete))
            {
                try { File.Delete(dataFileToDelete); } catch { }
            }
            else
            {
                var defaultData1 = Path.Combine(externalDataDir, "model.onnx_data");
                var defaultData2 = Path.Combine(externalDataDir, "model.onnx.data");
                if (File.Exists(defaultData1)) try { File.Delete(defaultData1); } catch { }
                if (File.Exists(defaultData2)) try { File.Delete(defaultData2); } catch { }
            }
        }
        finally
        {
            if (File.Exists(tempOnnxPath)) try { File.Delete(tempOnnxPath); } catch { }
        }
    }

    private static byte[] TransformTensorToExternal(
        Stream inStream,
        long tensorEnd,
        Stream outDataStream,
        string dataFileName,
        long thresholdBytes)
    {
        using var outTensor = new MemoryStream();
        byte[]? rawData = null;

        while (inStream.Position < tensorEnd)
        {
            var tag = ReadVarint(inStream);
            var wireType = (int)(tag & 0x07);

            if (tag == Tag_Tensor_RawData)
            {
                var len = (int)ReadVarint(inStream);
                rawData = new byte[len];
                inStream.ReadExactly(rawData, 0, len);
            }
            else if (tag == Tag_Tensor_ExternalData)
            {
                // 跳过已存在的外部数据配置
                SkipField(inStream, wireType);
            }
            else if (tag == Tag_Tensor_DataLocation)
            {
                SkipField(inStream, wireType);
            }
            else
            {
                CopyField(inStream, outTensor, tag, wireType);
            }
        }

        if (rawData != null && rawData.Length >= thresholdBytes)
        {
            // 写入外部数据文件
            var offset = outDataStream.Position;
            outDataStream.Write(rawData, 0, rawData.Length);
            var length = rawData.Length;

            // 写入 data_location = 1 (EXTERNAL)
            WriteVarint(outTensor, Tag_Tensor_DataLocation);
            WriteVarint(outTensor, 1);

            // 写入 location
            WriteExternalDataEntry(outTensor, "location", dataFileName);
            WriteExternalDataEntry(outTensor, "offset", offset.ToString());
            WriteExternalDataEntry(outTensor, "length", length.ToString());
        }
        else if (rawData != null)
        {
            // 小于阈值的保持内嵌
            WriteVarint(outTensor, Tag_Tensor_RawData);
            WriteVarint(outTensor, (ulong)rawData.Length);
            outTensor.Write(rawData, 0, rawData.Length);
        }

        return outTensor.ToArray();
    }

    private static byte[] TransformTensorToEmbedded(
        Stream inStream,
        long tensorEnd,
        string externalDataDir,
        ref string? resolvedDataFilePath)
    {
        using var outTensor = new MemoryStream();
        var entries = new List<(string Key, string Value)>();
        var isExternal = false;

        while (inStream.Position < tensorEnd)
        {
            var tag = ReadVarint(inStream);
            var wireType = (int)(tag & 0x07);

            if (tag == Tag_Tensor_DataLocation)
            {
                var loc = ReadVarint(inStream);
                if (loc == 1) isExternal = true;
            }
            else if (tag == Tag_Tensor_ExternalData)
            {
                var len = (long)ReadVarint(inStream);
                var end = inStream.Position + len;
                string key = "";
                string val = "";

                while (inStream.Position < end)
                {
                    var eTag = ReadVarint(inStream);
                    var eWire = (int)(eTag & 0x07);
                    if (eTag == Tag_StringEntry_Key) key = ReadString(inStream);
                    else if (eTag == Tag_StringEntry_Value) val = ReadString(inStream);
                    else SkipField(inStream, eWire);
                }

                if (!string.IsNullOrEmpty(key))
                {
                    entries.Add((key, val));
                }
            }
            else
            {
                CopyField(inStream, outTensor, tag, wireType);
            }
        }

        if (isExternal && entries.Count > 0)
        {
            var location = entries.FirstOrDefault(e => e.Key == "location").Value;
            var offsetStr = entries.FirstOrDefault(e => e.Key == "offset").Value;
            var lengthStr = entries.FirstOrDefault(e => e.Key == "length").Value;

            if (string.IsNullOrEmpty(location)) location = "model.onnx_data";
            var offset = string.IsNullOrEmpty(offsetStr) ? 0L : long.Parse(offsetStr);
            var length = string.IsNullOrEmpty(lengthStr) ? 0L : long.Parse(lengthStr);

            var dataFilePath = Path.Combine(externalDataDir, location);
            if (!File.Exists(dataFilePath))
            {
                // 兼容 model.onnx.data 和 model.onnx_data 两种命名
                var alt1 = Path.Combine(externalDataDir, "model.onnx_data");
                var alt2 = Path.Combine(externalDataDir, "model.onnx.data");
                if (File.Exists(alt1)) dataFilePath = alt1;
                else if (File.Exists(alt2)) dataFilePath = alt2;
            }

            resolvedDataFilePath = dataFilePath;

            if (File.Exists(dataFilePath))
            {
                using var fs = new FileStream(dataFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(offset, SeekOrigin.Begin);
                var readLen = length > 0 ? (int)length : (int)(fs.Length - offset);
                var rawData = new byte[readLen];
                fs.ReadExactly(rawData, 0, readLen);

                // 写回内嵌 raw_data
                WriteVarint(outTensor, Tag_Tensor_RawData);
                WriteVarint(outTensor, (ulong)rawData.Length);
                outTensor.Write(rawData, 0, rawData.Length);
            }
        }

        return outTensor.ToArray();
    }

    private static void WriteExternalDataEntry(Stream stream, string key, string value)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var valBytes = Encoding.UTF8.GetBytes(value);

        var entryLen = GetVarintLength((1 << 3) | 2) + GetVarintLength((ulong)keyBytes.Length) + keyBytes.Length +
                       GetVarintLength((2 << 3) | 2) + GetVarintLength((ulong)valBytes.Length) + valBytes.Length;

        WriteVarint(stream, Tag_Tensor_ExternalData);
        WriteVarint(stream, (ulong)entryLen);

        WriteVarint(stream, Tag_StringEntry_Key);
        WriteVarint(stream, (ulong)keyBytes.Length);
        stream.Write(keyBytes, 0, keyBytes.Length);

        WriteVarint(stream, Tag_StringEntry_Value);
        WriteVarint(stream, (ulong)valBytes.Length);
        stream.Write(valBytes, 0, valBytes.Length);
    }

    #region Protobuf Wire Primitives

    private static ulong ReadVarint(Stream stream)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static int GetVarintLength(ulong value)
    {
        int count = 1;
        while (value >= 0x80)
        {
            count++;
            value >>= 7;
        }
        return count;
    }

    private static string ReadString(Stream stream)
    {
        var len = (int)ReadVarint(stream);
        var bytes = new byte[len];
        stream.ReadExactly(bytes, 0, len);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void CopyField(Stream input, Stream output, ulong tag, int wireType)
    {
        WriteVarint(output, tag);
        switch (wireType)
        {
            case 0:
                var v = ReadVarint(input);
                WriteVarint(output, v);
                break;
            case 1:
                CopyBytes(input, output, 8);
                break;
            case 2:
                var len = ReadVarint(input);
                WriteVarint(output, len);
                CopyBytes(input, output, (long)len);
                break;
            case 5:
                CopyBytes(input, output, 4);
                break;
            default:
                throw new InvalidDataException($"Unsupported protobuf wire type: {wireType}");
        }
    }

    private static void SkipField(Stream stream, int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint(stream);
                break;
            case 1:
                stream.Seek(8, SeekOrigin.Current);
                break;
            case 2:
                var len = (long)ReadVarint(stream);
                stream.Seek(len, SeekOrigin.Current);
                break;
            case 5:
                stream.Seek(4, SeekOrigin.Current);
                break;
            default:
                throw new InvalidDataException($"Unsupported protobuf wire type: {wireType}");
        }
    }

    private static void CopyBytes(Stream input, Stream output, long count)
    {
        byte[] buffer = new byte[Math.Min(65536, count)];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = input.Read(buffer, 0, toRead);
            if (read <= 0) throw new EndOfStreamException();
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    #endregion
}
