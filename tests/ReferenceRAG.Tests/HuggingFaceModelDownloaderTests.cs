using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Models;
using System.Text;
using Xunit;

namespace ReferenceRAG.Tests;

public class HuggingFaceModelDownloaderTests
{
    [Fact]
    public async Task GetModelFilesAsync_PublicModel_ReturnsFiles()
    {
        using var downloader = new HuggingFaceModelDownloader();
        // Alibaba-NLP/gte-multilingual-reranker-base is public
        var files = await downloader.GetModelFilesAsync("Alibaba-NLP/gte-multilingual-reranker-base");

        Assert.NotNull(files);
        Assert.NotEmpty(files);
        Assert.Contains(files, f => f.Path == "config.json");
    }

    [Fact]
    public async Task GetModelFilesAsync_PrivateModel_ThrowsUnauthorizedAccessException()
    {
        using var downloader = new HuggingFaceModelDownloader();
        // xCloudinfo/xVITA-Embed-300M-zhTW is private/gated (HTTP 401)
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => downloader.GetModelFilesAsync("xCloudinfo/xVITA-Embed-300M-zhTW")
        );

        Assert.Contains("401", ex.Message);
        Assert.Contains("HF_TOKEN", ex.Message);
    }

    [Fact]
    public async Task ConvertToOnnxAsync_WithExistingModel_DelegatesToProtobufConverter_Succeeds()
    {
        using var downloader = new HuggingFaceModelDownloader();
        var tempDir = Path.Combine(Path.GetTempPath(), $"hf-convert-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var onnxPath = Path.Combine(tempDir, "model.onnx");
            var onnxDataPath = Path.Combine(tempDir, "model.onnx_data");

            // Write a synthetic ONNX file with embedded tensor
            var tensorData = Encoding.UTF8.GetBytes(new string('E', 500));
            var onnxBytes = CreateMinimalOnnxBytes(tensorData);
            await File.WriteAllBytesAsync(onnxPath, onnxBytes);

            // Act - ConvertToOnnxAsync delegating to pure C# converter
            var success = await downloader.ConvertToOnnxAsync(tempDir, onnxPath, "external");

            // Assert
            Assert.True(success);
            Assert.True(File.Exists(onnxPath));
            Assert.True(File.Exists(onnxDataPath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ConvertToOnnxAsync_WhenDirectoryEmpty_ReturnsFalseGracefully()
    {
        using var downloader = new HuggingFaceModelDownloader();
        var tempDir = Path.Combine(Path.GetTempPath(), $"hf-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var onnxPath = Path.Combine(tempDir, "model.onnx");
            var success = await downloader.ConvertToOnnxAsync(tempDir, onnxPath, "external");
            Assert.False(success);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    private static byte[] CreateMinimalOnnxBytes(byte[] rawData)
    {
        using var ms = new MemoryStream();

        // graph
        using var graphMs = new MemoryStream();
        // tensor
        using var tensorMs = new MemoryStream();
        // field 9: raw_data
        WriteVarint(tensorMs, (9 << 3) | 2);
        WriteVarint(tensorMs, (ulong)rawData.Length);
        tensorMs.Write(rawData, 0, rawData.Length);

        var tensorBytes = tensorMs.ToArray();
        // field 5: initializer
        WriteVarint(graphMs, (5 << 3) | 2);
        WriteVarint(graphMs, (ulong)tensorBytes.Length);
        graphMs.Write(tensorBytes, 0, tensorBytes.Length);

        var graphBytes = graphMs.ToArray();
        // field 7: graph
        WriteVarint(ms, (7 << 3) | 2);
        WriteVarint(ms, (ulong)graphBytes.Length);
        ms.Write(graphBytes, 0, graphBytes.Length);

        return ms.ToArray();
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)(value & 0x7F));
    }
}
