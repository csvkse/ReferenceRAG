using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class RerankStatusSnapshotTests
{
    [Fact]
    public void ConfiguredLazyModelIsEnabledBeforeItIsLoaded()
    {
        var snapshot = RerankStatusSnapshot.Create(true, new StubRerankService(isLoaded: false));

        Assert.True(snapshot.Enabled);
        Assert.False(snapshot.Loaded);
        Assert.Equal("test-reranker", snapshot.Model);
    }

    private sealed class StubRerankService(bool isLoaded) : IRerankService
    {
        public string ModelName => "test-reranker";
        public bool IsLoaded => isLoaded;

        public Task<double> RerankAsync(string query, string document, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RerankResult> RerankBatchAsync(string query, IEnumerable<string> documents, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ReloadModelAsync(string modelPath, string modelName) =>
            throw new NotSupportedException();

        public void UnloadModel() => throw new NotSupportedException();
    }
}
