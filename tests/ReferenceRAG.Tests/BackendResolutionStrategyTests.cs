using ReferenceRAG.Core.Features.ModelManagement.Strategies;
using Xunit;

namespace ReferenceRAG.Tests;

public class BackendResolutionStrategyTests
{
    [Theory]
    [InlineData("models/qwen2.5-coder-7b.gguf", "llama")]
    [InlineData("C:/path/to/model.GGUF", "llama")]
    [InlineData("models/bge-m3.onnx", "onnx")]
    [InlineData("models/bge-m3/model.onnx", "onnx")]
    [InlineData("http://localhost:11434/v1", "openai")]
    [InlineData("https://api.openai.com/v1", "openai")]
    public void StrategyFactory_ResolvesCorrectStrategy_ByPath(string modelPath, string expectedStrategy)
    {
        var strategy = ModelBackendStrategyFactory.ResolveStrategy(modelPath);
        Assert.Equal(expectedStrategy, strategy.StrategyName);
    }

    [Theory]
    [InlineData("llama", "llama")]
    [InlineData("gguf", "llama")]
    [InlineData("onnx", "onnx")]
    [InlineData("openai", "openai")]
    public void StrategyFactory_ResolvesCorrectStrategy_ByExplicitMode(string mode, string expectedStrategy)
    {
        var strategy = ModelBackendStrategyFactory.ResolveStrategy("any/path", mode);
        Assert.Equal(expectedStrategy, strategy.StrategyName);
    }

    [Fact]
    public void GgufBackendStrategy_CanHandle_DirectoryWithGguf()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "gguf_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var fakeGguf = Path.Combine(tempDir, "model.gguf");
            File.WriteAllText(fakeGguf, "dummy content");

            var strategy = new GgufBackendStrategy();
            Assert.True(strategy.CanHandle(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OnnxBackendStrategy_CanHandle_DirectoryWithOnnx()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "onnx_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var fakeOnnx = Path.Combine(tempDir, "model.onnx");
            File.WriteAllText(fakeOnnx, "dummy content");

            var strategy = new OnnxBackendStrategy();
            Assert.True(strategy.CanHandle(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
