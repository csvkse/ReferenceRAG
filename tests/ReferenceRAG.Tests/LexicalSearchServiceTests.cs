using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Extensions;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Tests;

public class LexicalSearchServiceTests
{
    [Fact]
    public async Task SourceExclusionsAreAppliedBeforeTopKAndDuringDrillDown()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-exclude-query-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            foreach (var (id, path, content) in new[] {
                ("hidden", "/notes/.git/hidden.md", "policyword policyword policyword"),
                ("allowed", "/notes/visible.md", "policyword") })
            {
                var file = new FileRecord { Id = id, Source = "notes", Path = path };
                await coordinator.StageDocumentsAsync(file, "v1", new[] { new ChunkRecord { Id = id + "-chunk", FileId = id, Content = content } });
                await coordinator.StageIndexAsync(id, "v1");
                await coordinator.PublishAsync(id, "v1");
            }
            var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = "/notes" } } };
            config.Search.EnableLexical = true;
            var service = new SearchService(new Mock<IVectorStore>().Object, new Mock<IEmbeddingService>().Object,
                new Mock<ITextEnhancer>().Object, new ConfigManager(config), NullLogger<SearchService>.Instance,
                lexicalFactory: () => coordinator);
            var response = await service.SearchAsync(new AIQueryRequest { Query = "policyword", Mode = QueryMode.Lexical, TopK = 1 });
            Assert.Equal("allowed-chunk", Assert.Single(response.Chunks).RefId);
            var expanded = await service.DrillDownAsync(new DrillDownRequest { RefIds = new() { "hidden-chunk" }, Mode = QueryMode.Lexical });
            Assert.Empty(expanded.ExpandedChunks);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DependencyInjectionDoesNotResolveModelsForLexicalSearch()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-di-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var file = new FileRecord { Id = "file", Source = "notes", Path = "/notes/a.md" };
            await coordinator.StageDocumentsAsync(file, "v1", new[] { new ChunkRecord { Id = "c", FileId = "file", Content = "uniquedi" } });
            await coordinator.StageIndexAsync("file", "v1");
            await coordinator.PublishAsync("file", "v1");
            var config = new ObsidianRagConfig { Sources = new() { new SourceFolder { Name = "notes", Path = "/notes" } } };
            config.Search.EnableLexical = true;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new ConfigManager(config));
            services.AddSingleton<IVectorStore>(new Mock<IVectorStore>(MockBehavior.Strict).Object);
            services.AddSingleton<ITextEnhancer>(new Mock<ITextEnhancer>().Object);
            services.AddSingleton(coordinator);
            services.AddSingleton<IEmbeddingService>(_ => throw new InvalidOperationException("embedding was resolved"));
            services.AddSingleton<IRerankService>(_ => throw new InvalidOperationException("reranker was resolved"));
            services.AddSingleton<IModelTelemetryService>(_ => throw new InvalidOperationException("telemetry was resolved"));
            services.AddSearch();
            await using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var response = await service.SearchAsync(new AIQueryRequest { Query = "uniquedi", Mode = QueryMode.Lexical });
            Assert.Single(response.Chunks);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LexicalSearchRoutesBeforeModelsAndAppliesAllowedSources()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-search-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            foreach (var source in new[] { "allowed", "disabled" })
            {
                var file = new FileRecord { Id = source, Source = source, Path = $"/{source}/note.md" };
                await coordinator.StageDocumentsAsync(file, "v1", new[] { new ChunkRecord { Id = source + "-chunk", FileId = source, Content = "unique text", StartLine = 4, EndLine = 5 } });
                await coordinator.StageIndexAsync(source, "v1");
                await coordinator.PublishAsync(source, "v1");
            }
            var config = new ObsidianRagConfig
            {
                Sources = new() { new SourceFolder { Name = "allowed", Path = "/allowed", Enabled = true }, new SourceFolder { Name = "disabled", Path = "/disabled", Enabled = false } }
            };
            config.Search.EnableLexical = true;
            var model = new Mock<IEmbeddingService>(MockBehavior.Strict);
            var store = new Mock<IVectorStore>(MockBehavior.Strict);
            var rerank = new Mock<IRerankService>(MockBehavior.Strict);
            var service = new SearchService(store.Object, model.Object, new Mock<ITextEnhancer>().Object,
                new ConfigManager(config), NullLogger<SearchService>.Instance, rerankService: rerank.Object,
                lexicalFactory: () => coordinator);
            var response = await service.SearchAsync(new AIQueryRequest { Query = "unique", Mode = QueryMode.Lexical, TopK = 1 });
            var chunk = Assert.Single(response.Chunks);
            Assert.Equal("allowed", chunk.Source);
            Assert.Equal("unique text", chunk.Content);
            Assert.Equal(4, chunk.StartLine);
            Assert.Null(response.Stats.PipelineTelemetry?.EmbeddingModel);
            Assert.False(response.RerankApplied);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SearchAsync(
                new AIQueryRequest { Query = "unique", Mode = (QueryMode)999 }));
            var expanded = await service.DrillDownAsync(new DrillDownRequest
            {
                RefIds = new() { chunk.RefId }, Mode = QueryMode.Lexical, ExpandContext = 1
            });
            Assert.Contains("unique text", expanded.FullContext);
            Assert.Equal("allowed", Assert.Single(expanded.ExpandedChunks).Source);
            var denied = await service.DrillDownAsync(new DrillDownRequest
            {
                RefIds = new() { chunk.RefId }, Mode = QueryMode.Lexical, Sources = new() { "disabled" }
            });
            Assert.Empty(denied.ExpandedChunks);
            config.Indexing.EmbeddingEnabled = false;
            config.Search.EnableAutomaticFallback = false;
            var disabled = await Assert.ThrowsAsync<SearchCapabilityException>(() => service.SearchAsync(
                new AIQueryRequest { Query = "unique", Mode = QueryMode.Standard }));
            Assert.Contains("已禁用", disabled.Message);
            await Assert.ThrowsAsync<SearchCapabilityException>(() => service.DrillDownAsync(
                new DrillDownRequest { RefIds = new() { "old-vector-ref" } }));
            config.Indexing.EmbeddingEnabled = true;
            config.Search.EnableLexical = false;
            var error = await Assert.ThrowsAsync<SearchCapabilityException>(() => service.SearchAsync(
                new AIQueryRequest { Query = "unique", Mode = QueryMode.Standard }));
            Assert.Contains("覆盖不完整", error.Message);
            model.VerifyNoOtherCalls();
            rerank.VerifyNoOtherCalls();
            store.VerifyNoOtherCalls();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }
}
