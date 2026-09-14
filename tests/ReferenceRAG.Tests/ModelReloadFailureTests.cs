using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Tests;

public class ModelReloadFailureTests
{
    [Fact]
    public async Task EmbeddingReload_ReturnsFalse_WhenModelCannotBeLoaded()
    {
        using var service = new EmbeddingService(new EmbeddingOptions
        {
            ModelPath = "missing-initial.onnx",
            ModelName = "initial"
        });

        var result = await service.ReloadModelAsync("missing-next.onnx", "next");

        Assert.False(result);
        Assert.True(service.IsSimulationMode);
    }
}
