using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Rerank;

namespace ReferenceRAG.Tests;

public class LazyModelServiceTests
{
    [Fact]
    public async Task EmbeddingModelIsNotCreatedUntilInference()
    {
        var created=0;
        using var service=new LazyEmbeddingService(new EmbeddingOptions{ModelPath="missing.onnx",ModelName="test"},null,()=>
        {
            created++;
            return new EmbeddingService(new EmbeddingOptions{ModelPath="missing.onnx",ModelName="test"});
        });

        Assert.Equal("test",service.ModelName);
        service.Normalize([3,4]);
        Assert.False(service.IsModelLoaded);
        Assert.Equal(0,created);

        await service.EncodeAsync("first inference");
        Assert.True(service.IsModelLoaded);
        Assert.Equal(1,created);
    }

    [Fact]
    public async Task RerankModelIsNotCreatedUntilInference()
    {
        var created=0;
        using var service=new LazyRerankService("test",()=>
        {
            created++;
            return new OnnxRerankService(new RerankOptions{ModelPath="missing.onnx",ModelName="test"});
        });

        Assert.Equal("test",service.ModelName);
        Assert.False(service.IsLoaded);
        Assert.False(service.IsModelLoaded);
        Assert.Equal(0,created);

        await service.RerankAsync("query","document");
        Assert.True(service.IsModelLoaded);
        Assert.Equal(1,created);
    }
}
