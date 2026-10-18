using ReferenceRAG.Core.Features.ModelManagement.Protobuf;
using System.Text;
using Xunit;

namespace ReferenceRAG.Tests;

public class OnnxFormatConverterTests : IDisposable
{
    private readonly string _tempDir;

    public OnnxFormatConverterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"onnx-converter-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    #region Helper Methods for Synthetic ONNX Models

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)(value & 0x7F));
    }

    private static void WriteFieldHeader(Stream stream, int fieldNumber, int wireType)
    {
        WriteVarint(stream, (ulong)((fieldNumber << 3) | wireType));
    }

    private static void WriteVarintField(Stream stream, int fieldNumber, ulong value)
    {
        WriteFieldHeader(stream, fieldNumber, 0);
        WriteVarint(stream, value);
    }

    private static void WriteStringField(Stream stream, int fieldNumber, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteFieldHeader(stream, fieldNumber, 2);
        WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteBytesField(Stream stream, int fieldNumber, byte[] bytes)
    {
        WriteFieldHeader(stream, fieldNumber, 2);
        WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static byte[] CreateSyntheticOnnxModel(
        (string Name, byte[] Data)[] embeddedTensors,
        string modelName = "test_graph")
    {
        using var graphMs = new MemoryStream();

        // graph.name (field 2)
        WriteStringField(graphMs, 2, modelName);

        // graph.initializer (field 5)
        foreach (var (tensorName, tensorData) in embeddedTensors)
        {
            using var tensorMs = new MemoryStream();
            // tensor.data_type = 1 (FLOAT, field 2)
            WriteVarintField(tensorMs, 2, 1);
            // tensor.name (field 4)
            WriteStringField(tensorMs, 4, tensorName);
            // tensor.raw_data (field 9)
            WriteBytesField(tensorMs, 9, tensorData);

            var tensorBytes = tensorMs.ToArray();
            WriteFieldHeader(graphMs, 5, 2);
            WriteVarint(graphMs, (ulong)tensorBytes.Length);
            graphMs.Write(tensorBytes, 0, tensorBytes.Length);
        }

        using var modelMs = new MemoryStream();
        // model.ir_version = 8 (field 1)
        WriteVarintField(modelMs, 1, 8);
        // model.producer_name = "ReferenceRAG.Tests" (field 2)
        WriteStringField(modelMs, 2, "ReferenceRAG.Tests");

        // model.graph (field 7)
        var graphBytes = graphMs.ToArray();
        WriteFieldHeader(modelMs, 7, 2);
        WriteVarint(modelMs, (ulong)graphBytes.Length);
        modelMs.Write(graphBytes, 0, graphBytes.Length);

        return modelMs.ToArray();
    }

    #endregion

    [Fact]
    public async Task ConvertEmbeddedToExternal_SplitsLargeTensors_PreservesSmallTensors()
    {
        // Arrange
        var modelDir = Path.Combine(_tempDir, "split_test");
        Directory.CreateDirectory(modelDir);
        var onnxPath = Path.Combine(modelDir, "model.onnx");
        var onnxDataPath = Path.Combine(modelDir, "model.onnx_data");

        var largeData = Encoding.UTF8.GetBytes(new string('L', 1024)); // 1024 bytes > 128 byte threshold
        var smallData = Encoding.UTF8.GetBytes("small_tensor");        // 12 bytes < 128 byte threshold

        var onnxBytes = CreateSyntheticOnnxModel(new[]
        {
            ("large_weight", largeData),
            ("small_bias", smallData)
        });
        await File.WriteAllBytesAsync(onnxPath, onnxBytes);

        // Act
        var (success, error) = await OnnxFormatConverter.ConvertFormatAsync(modelDir, "external");

        // Assert
        Assert.True(success, error);
        Assert.True(File.Exists(onnxPath), "model.onnx should exist");
        Assert.True(File.Exists(onnxDataPath), "model.onnx_data should exist");

        var dataBytes = await File.ReadAllBytesAsync(onnxDataPath);
        Assert.Equal(largeData.Length, dataBytes.Length);
        Assert.Equal(largeData, dataBytes);

        // Verify model.onnx still contains small tensor inlined
        var convertedOnnxBytes = await File.ReadAllBytesAsync(onnxPath);
        var convertedStr = Encoding.UTF8.GetString(convertedOnnxBytes);
        Assert.Contains("small_tensor", convertedStr);
        Assert.Contains("large_weight", convertedStr);
        Assert.Contains("model.onnx_data", convertedStr);
    }

    [Fact]
    public async Task ConvertExternalToEmbedded_InlinesExternalTensors_RemovesDataFile()
    {
        // Arrange
        var modelDir = Path.Combine(_tempDir, "inline_test");
        Directory.CreateDirectory(modelDir);
        var onnxPath = Path.Combine(modelDir, "model.onnx");
        var onnxDataPath = Path.Combine(modelDir, "model.onnx_data");

        var tensorData = Encoding.UTF8.GetBytes(new string('W', 512));
        var onnxBytes = CreateSyntheticOnnxModel(new[]
        {
            ("weights", tensorData)
        });
        await File.WriteAllBytesAsync(onnxPath, onnxBytes);

        // First convert to external
        var (toExtSuccess, toExtError) = await OnnxFormatConverter.ConvertFormatAsync(modelDir, "external");
        Assert.True(toExtSuccess, toExtError);
        Assert.True(File.Exists(onnxDataPath));

        // Act - Convert back to embedded
        var (toEmbSuccess, toEmbError) = await OnnxFormatConverter.ConvertFormatAsync(modelDir, "embedded");

        // Assert
        Assert.True(toEmbSuccess, toEmbError);
        Assert.True(File.Exists(onnxPath));
        Assert.False(File.Exists(onnxDataPath), "model.onnx_data should be cleaned up after embedding");

        var restoredBytes = await File.ReadAllBytesAsync(onnxPath);
        var restoredStr = Encoding.UTF8.GetString(restoredBytes);
        Assert.Contains(new string('W', 512), restoredStr);
    }

    [Fact]
    public async Task ConvertFormat_RoundTrip_PreservesAllTensorBytesExactly()
    {
        // Arrange
        var modelDir = Path.Combine(_tempDir, "roundtrip_test");
        Directory.CreateDirectory(modelDir);
        var onnxPath = Path.Combine(modelDir, "model.onnx");
        var onnxDataPath = Path.Combine(modelDir, "model.onnx_data");

        var random = new Random(42);
        var tensor1 = new byte[1000];
        var tensor2 = new byte[2000];
        var tensor3 = new byte[64]; // small
        random.NextBytes(tensor1);
        random.NextBytes(tensor2);
        random.NextBytes(tensor3);

        var originalOnnxBytes = CreateSyntheticOnnxModel(new[]
        {
            ("layer1.weight", tensor1),
            ("layer2.weight", tensor2),
            ("layer1.bias", tensor3)
        });
        await File.WriteAllBytesAsync(onnxPath, originalOnnxBytes);

        // Step 1: Embedded -> External
        var (extSuccess, extError) = await OnnxFormatConverter.ConvertFormatAsync(modelDir, "external");
        Assert.True(extSuccess, extError);
        Assert.True(File.Exists(onnxDataPath));
        Assert.Equal(tensor1.Length + tensor2.Length, new FileInfo(onnxDataPath).Length);

        // Step 2: External -> Embedded
        var (embSuccess, embError) = await OnnxFormatConverter.ConvertFormatAsync(modelDir, "embedded");
        Assert.True(embSuccess, embError);
        Assert.False(File.Exists(onnxDataPath));

        // Read and verify tensors from restored model.onnx
        var roundTripBytes = await File.ReadAllBytesAsync(onnxPath);
        Assert.True(roundTripBytes.AsSpan().IndexOf(tensor1) >= 0, "tensor1 bytes must be preserved");
        Assert.True(roundTripBytes.AsSpan().IndexOf(tensor2) >= 0, "tensor2 bytes must be preserved");
        Assert.True(roundTripBytes.AsSpan().IndexOf(tensor3) >= 0, "tensor3 bytes must be preserved");
    }

    [Fact]
    public async Task ConvertFormat_DirectoryNotFound_ReturnsError()
    {
        var nonExistent = Path.Combine(_tempDir, "does_not_exist");
        var (success, error) = await OnnxFormatConverter.ConvertFormatAsync(nonExistent, "external");
        Assert.False(success);
        Assert.Contains("不存在", error);
    }

    [Fact]
    public async Task ConvertFormat_NoOnnxFile_ReturnsError()
    {
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);
        var (success, error) = await OnnxFormatConverter.ConvertFormatAsync(emptyDir, "external");
        Assert.False(success);
        Assert.Contains("未找到 model.onnx", error);
    }

    [Fact]
    public async Task ConvertFormat_AlreadyTargetFormat_ReturnsError()
    {
        var modelDir = Path.Combine(_tempDir, "already_format");
        Directory.CreateDirectory(modelDir);
        var onnxPath = Path.Combine(modelDir, "model.onnx");
        var onnxDataPath = Path.Combine(modelDir, "model.onnx_data");

        await File.WriteAllBytesAsync(onnxPath, new byte[] { 1, 2, 3 });

        // Case 1: Already embedded, requesting embedded
        var (embSuccess, embError) = await OnnxFormatConverter.ConvertFormatAsync(modelDir, "embedded");
        Assert.False(embSuccess);
        Assert.Contains("已是 embedded 格式", embError);

        // Case 2: Create onnx_data, now external, requesting external
        await File.WriteAllBytesAsync(onnxDataPath, new byte[] { 4, 5, 6 });
        var (extSuccess, extError) = await OnnxFormatConverter.ConvertFormatAsync(modelDir, "external");
        Assert.False(extSuccess);
        Assert.Contains("已是 external 格式", extError);
    }

    [Fact]
    public async Task ConvertFormat_UnknownFormat_ReturnsError()
    {
        var modelDir = Path.Combine(_tempDir, "unknown_fmt");
        Directory.CreateDirectory(modelDir);
        var onnxPath = Path.Combine(modelDir, "model.onnx");
        await File.WriteAllBytesAsync(onnxPath, new byte[] { 1, 2, 3 });

        var (success, error) = await OnnxFormatConverter.ConvertFormatAsync(modelDir, "safetensors");
        Assert.False(success);
        Assert.Contains("未知目标格式", error);
    }
}
