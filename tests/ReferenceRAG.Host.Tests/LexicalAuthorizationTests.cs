using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.McpTools;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.Search.Fusion;
using Xunit;
using ReferenceRAG.Core.Extensions;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Storage;
using ReferenceRAG.Business.Features.Indexing.Services;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using ReferenceRAG.ApiHost.Transport;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;

namespace ReferenceRAG.Host.Tests;

public class LexicalAuthorizationTests
{
    [Fact]
    public async Task VectorRebuildIsRejectedWithoutResolvingEmbeddingWhenDisabled()
    {
        var config = new ObsidianRagConfig();
        config.Indexing.EmbeddingEnabled = false;
        config.Search.EnableLexical = true;
        var manager = new ConfigManager(config);
        using var services = new ServiceCollection().BuildServiceProvider();
        var pipeline = new Mock<IFileIndexPipeline>().Object;
        var index = new IndexService(services, new Mock<IIndexEventPublisher>().Object, manager, pipeline,
            new Mock<IFileProcessingGuard>().Object, NullLogger<IndexService>.Instance);
        var embedding = new Mock<IEmbeddingService>(MockBehavior.Strict);
        var controller = new IndexController(new Mock<IVectorStore>().Object, embedding.Object, new Mock<IBM25Store>().Object,
            new Mock<IGraphStore>().Object, pipeline, new Mock<IModelManager>().Object, manager, index,
            new Mock<IQueryStatsService>().Object, NullLogger<IndexController>.Instance);
        Assert.IsType<ConflictObjectResult>((await controller.Rebuild()).Result);
        embedding.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task IndexControllerActivationDoesNotResolveEmbeddingWhenDisabled()
    {
        var config = new ObsidianRagConfig();
        config.Indexing.EmbeddingEnabled = false;
        var manager = new ConfigManager(config);
        using var dummyProvider = new ServiceCollection().BuildServiceProvider();
        var pipeline = new Mock<IFileIndexPipeline>().Object;
        var index = new IndexService(dummyProvider, new Mock<IIndexEventPublisher>().Object, manager, pipeline,
            new Mock<IFileProcessingGuard>().Object, NullLogger<IndexService>.Instance);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(manager);
        services.AddSingleton(index);
        services.AddSingleton(pipeline);
        services.AddSingleton(new Mock<IVectorStore>().Object);
        services.AddSingleton(new Mock<IBM25Store>().Object);
        services.AddSingleton(new Mock<IGraphStore>().Object);
        services.AddSingleton(new Mock<IModelManager>().Object);
        services.AddSingleton(new Mock<IQueryStatsService>().Object);
        services.AddSingleton<IEmbeddingService>(_ => throw new InvalidOperationException("embedding resolved"));
        using var provider = services.BuildServiceProvider();
        var controller = ActivatorUtilities.CreateInstance<IndexController>(provider);
        Assert.IsType<ConflictObjectResult>((await controller.Rebuild()).Result);
    }

    [Fact]
    public async Task RestrictedHttpCrossKnowledgeBaseSearchUsesDocumentScopeBeforeTopK()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-http-kb-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(dir, "sources");
        var allowed = Path.Combine(sourceRoot, "allowed");
        var secret = Path.Combine(sourceRoot, "secret");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(secret);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(allowed, "note.md"), "crossneedle public");
            await File.WriteAllTextAsync(Path.Combine(secret, "note.md"), "crossneedle secret");
            using var catalog = new CatalogStore(Path.Combine(dir, "catalog.db"));
            await catalog.InitializeAsync();
            using var storage = new KnowledgeBaseStorageManager(dir);
            await catalog.SaveKnowledgeBaseAsync(new KnowledgeBaseDefinition("mixed", "Mixed", null, true, 1, 1, "default", DateTime.UtcNow, DateTime.UtcNow));
            await catalog.SaveConnectorAsync(new ConnectorDefinition("connector", "mixed", "Folder", sourceRoot, "*.md", 1, true, DateTime.UtcNow, DateTime.UtcNow));
            await using (var lease = await storage.AcquireLeaseAsync("mixed", StorageAccess.ReadWrite))
            {
                await lease.Store.CommitDocumentAsync(new IndexBatch("mixed", 1, 0, "connector", "allowed/note.md", Path.Combine(allowed, "note.md"), null, "a",
                    new[] { new ChunkData("public-chunk", 0, "crossneedle public", 2) }));
                await lease.Store.CommitDocumentAsync(new IndexBatch("mixed", 1, 0, "connector", "secret/note.md", Path.Combine(secret, "note.md"), null, "b",
                    new[] { new ChunkData("secret-chunk", 0, "crossneedle crossneedle crossneedle secret", 4) }));
            }
            var config = new ObsidianRagConfig { Sources = new() {
                new SourceFolder { Name = "allowed-source", Path = allowed }, new SourceFolder { Name = "secret-source", Path = secret } } };
            config.Indexing.EmbeddingEnabled = false;
            var manager = new ConfigManager(config);
            var kb = new KnowledgeBaseService(catalog, storage, configManager: manager);
            var search = new KnowledgeBaseSearchService(catalog, storage, configManager: manager);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = dir });
            builder.Services.AddInProcessServer(listen: false);
            builder.Services.AddControllers().AddApplicationPart(typeof(KnowledgeBasesController).Assembly);
            builder.Services.AddSingleton<IKnowledgeBaseService>(kb);
            builder.Services.AddSingleton<IKnowledgeBaseSearchService>(search);
            await using var app = builder.Build();
            app.Use(async (ctx, next) => {
                ctx.Items["CurrentApiKey"] = new ApiKeyEntity { SourceScopeMode = "custom", AllowedSources = "[\"allowed-source\"]" };
                await next();
            });
            app.MapControllers();
            await app.StartAsync();
            var server = app.Services.GetRequiredService<InProcessServer>();
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"query\":\"crossneedle\",\"knowledgeBaseIds\":[\"mixed\"],\"topK\":1,\"authorizedSourceNames\":[\"secret-source\"]}")))
            using (var output = new MemoryStream())
            {
                var result = new HttpResponseFeature();
                await server.DispatchAsync("POST", "/api/knowledge-base-search", new Dictionary<string, string> { ["Content-Type"] = "application/json" }, input, output, result, CancellationToken.None);
                Assert.Equal(200, result.StatusCode);
                using var json = JsonDocument.Parse(output.ToArray());
                Assert.Equal("public-chunk", Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("chunkId").GetString());
            }
            using (var input = new MemoryStream())
            using (var output = new MemoryStream())
            {
                var result = new HttpResponseFeature();
                await server.DispatchAsync("GET", "/api/knowledge-bases", new Dictionary<string, string>(), input, output, result, CancellationToken.None);
                Assert.Equal(200, result.StatusCode);
                using var json = JsonDocument.Parse(output.ToArray());
                Assert.Equal("mixed", Assert.Single(json.RootElement.EnumerateArray()).GetProperty("id").GetString());
            }
            await app.StopAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ControllerPropagatesRequestCancellationToSearch()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var search = new Mock<ISearchService>(MockBehavior.Strict);
        search.Setup(s => s.SearchAsync(It.IsAny<AIQueryRequest>(), cts.Token))
            .Returns(Task.FromCanceled<AIQueryResponse>(cts.Token));
        var controller = new AIQueryController(search.Object, new Mock<IQueryStatsService>().Object,
            new Mock<IEmbeddingService>().Object, new Mock<IVectorStore>().Object,
            new Mock<IBM25Store>().Object, new Mock<IRerankService>().Object,
            new ConfigManager(new ObsidianRagConfig()), NullLogger<AIQueryController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = cts.Token } } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.Query(
            new AIQueryRequest { Query = "cancel", Mode = QueryMode.Lexical }));
        search.VerifyAll();
    }

    [Fact]
    public async Task InProcessHttpReturnsScopedLexicalResultsAndExplicitEmbeddingDisabledError()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-http-{Guid.NewGuid():N}");
        var allowed = Path.Combine(dir, "allowed");
        var secret = Path.Combine(dir, "secret");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(secret);
        try
        {
            var config = new ObsidianRagConfig { DataPath = dir, Sources = new() {
                new SourceFolder { Name = "allowed", Path = allowed }, new SourceFolder { Name = "secret", Path = secret } } };
            config.Indexing.EmbeddingEnabled = false;
            config.Search.EnableLexical = true;
            var manager = new ConfigManager(config);
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var indexer = new LexicalFileIndexer(new MarkdownChunker(), coordinator, manager);
            foreach (var (root, source) in new[] { (allowed, "allowed"), (secret, "secret") })
            {
                var path = Path.Combine(root, "note.md");
                await File.WriteAllTextAsync(path, $"# Note\n\nhttpneedle {source}");
                await indexer.IndexAsync(path, source);
            }
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = dir });
            builder.Services.AddInProcessServer(listen: false);
            builder.Services.AddControllers().AddApplicationPart(typeof(AIQueryController).Assembly)
                .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(coordinator);
            builder.Services.AddSingleton<IVectorStore>(new Mock<IVectorStore>(MockBehavior.Strict).Object);
            builder.Services.AddSingleton<IBM25Store>(new Mock<IBM25Store>(MockBehavior.Strict).Object);
            builder.Services.AddSingleton<ITextEnhancer>(new Mock<ITextEnhancer>().Object);
            builder.Services.AddSingleton<IQueryStatsService>(new Mock<IQueryStatsService>().Object);
            builder.Services.AddSingleton<IKnowledgeBaseService>(new Mock<IKnowledgeBaseService>(MockBehavior.Strict).Object);
            var scopedKb = new Mock<IKnowledgeBaseSearchService>(MockBehavior.Strict);
            scopedKb.Setup(s => s.ListAccessibleKnowledgeBasesAsync(It.Is<IReadOnlyList<string>>(v => v.SequenceEqual(new[] { "allowed" })), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { new KnowledgeBaseDefinition("visible-kb", "Visible", null, true, 1, 1, "", DateTime.UtcNow, DateTime.UtcNow) });
            builder.Services.AddSingleton(scopedKb.Object);
            builder.Services.AddSingleton<IEmbeddingService>(_ => throw new InvalidOperationException("embedding resolved"));
            builder.Services.AddSingleton<IRerankService>(_ => throw new InvalidOperationException("reranker resolved"));
            builder.Services.AddSearch();
            await using var app = builder.Build();
            app.Use(async (ctx, next) => {
                ctx.Items["CurrentApiKey"] = new ApiKeyEntity { SourceScopeMode = "custom", AllowedSources = "[\"allowed\"]" };
                await next();
            });
            app.MapControllers();
            await app.StartAsync();
            var server = app.Services.GetRequiredService<InProcessServer>();
            async Task<(int Status, JsonDocument Json)> QueryAsync(string mode)
            {
                using var input = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"query\":\"httpneedle\",\"mode\":\"{mode}\",\"topK\":5}}"));
                using var output = new MemoryStream();
                var response = new HttpResponseFeature();
                await server.DispatchAsync("POST", "/api/ai/query", new Dictionary<string, string> { ["Content-Type"] = "application/json" }, input, output, response, CancellationToken.None);
                return (response.StatusCode, JsonDocument.Parse(output.ToArray()));
            }
            var lexical = await QueryAsync("Lexical");
            using (lexical.Json)
            {
                Assert.Equal(200, lexical.Status);
                Assert.Equal("allowed", Assert.Single(lexical.Json.RootElement.GetProperty("chunks").EnumerateArray()).GetProperty("source").GetString());
            }
            config.Search.EnableAutomaticFallback = false;
            var vector = await QueryAsync("Standard");
            using (vector.Json)
            {
                Assert.Equal(409, vector.Status);
                Assert.Equal("embedding_disabled", vector.Json.RootElement.GetProperty("code").GetString());
            }
            config.Indexing.EmbeddingEnabled = true;
            var stale = await QueryAsync("Standard");
            using (stale.Json)
            {
                Assert.Equal(409, stale.Status);
                Assert.Equal("vector_index_incomplete", stale.Json.RootElement.GetProperty("code").GetString());
                Assert.Equal("allowed", Assert.Single(stale.Json.RootElement.GetProperty("sources").EnumerateArray()).GetString());
            }
            using (var input = new MemoryStream())
            using (var output = new MemoryStream())
            {
                var response = new HttpResponseFeature();
                await server.DispatchAsync("GET", "/api/knowledge-bases", new Dictionary<string, string>(), input, output, response, CancellationToken.None);
                Assert.Equal(200, response.StatusCode);
                using var listed = JsonDocument.Parse(output.ToArray());
                Assert.Equal("visible-kb", Assert.Single(listed.RootElement.EnumerateArray()).GetProperty("id").GetString());
            }
            await app.StopAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task LexicalFilesReachRestAndMcpWithoutResolvingModelsAndRespectScope()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-host-{Guid.NewGuid():N}");
        var allowed = Path.Combine(dir, "allowed");
        var secret = Path.Combine(dir, "secret");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(secret);
        try
        {
            var config = new ObsidianRagConfig { Sources = new() {
                new SourceFolder { Name = "allowed", Path = allowed }, new SourceFolder { Name = "secret", Path = secret } } };
            config.DataPath = dir;
            config.Indexing.EmbeddingEnabled = false;
            config.Search.EnableLexical = true;
            var manager = new ConfigManager(config);
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var indexer = new LexicalFileIndexer(new MarkdownChunker(), coordinator, manager);
            foreach (var (root, source) in new[] { (allowed, "allowed"), (secret, "secret") })
            {
                var path = Path.Combine(root, "note.md");
                await File.WriteAllTextAsync(path, $"# Note\n\nintegrationneedle {source}");
                await indexer.IndexAsync(path, source);
            }
            var context = new DefaultHttpContext();
            context.Items["CurrentApiKey"] = new ApiKeyEntity { SourceScopeMode = "custom", AllowedSources = "[\"allowed\"]" };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(manager);
            services.AddSingleton(coordinator);
            services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
            services.AddSingleton<IVectorStore>(new Mock<IVectorStore>(MockBehavior.Strict).Object);
            services.AddSingleton<IBM25Store>(new Mock<IBM25Store>(MockBehavior.Strict).Object);
            services.AddSingleton<ITextEnhancer>(new Mock<ITextEnhancer>().Object);
            services.AddSingleton<IQueryStatsService>(new Mock<IQueryStatsService>().Object);
            services.AddSingleton<IEmbeddingService>(_ => throw new InvalidOperationException("embedding resolved"));
            services.AddSingleton<IRerankService>(_ => throw new InvalidOperationException("reranker resolved"));
            services.AddSearch();
            await using var provider = services.BuildServiceProvider();
            var controller = ActivatorUtilities.CreateInstance<AIQueryController>(provider);
            controller.ControllerContext = new ControllerContext { HttpContext = context };
            var result = await controller.Query(new AIQueryRequest { Query = "integrationneedle", Mode = QueryMode.Lexical });
            var response = Assert.IsType<AIQueryResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.Equal("allowed", Assert.Single(response.Chunks).Source);
            var json = await new RagSearchTools(provider).LexicalSearch("integrationneedle");
            using var parsed = System.Text.Json.JsonDocument.Parse(json);
            Assert.True(parsed.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("allowed", Assert.Single(parsed.RootElement.GetProperty("chunks").EnumerateArray()).GetProperty("source").GetString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SettingsRejectLiveIndexModeChange()
    {
        var current = new ObsidianRagConfig();
        var next = new ObsidianRagConfig();
        next.Indexing.EmbeddingEnabled = false;
        next.Search.EnableLexical = true;
        var manager = new ConfigManager(current);
        var controller = new SettingsController(manager, new Mock<IModelManager>().Object,
            NullLogger<SettingsController>.Instance);
        Assert.IsType<ConflictObjectResult>((await controller.Save(next)).Result);
        Assert.True(manager.Load().Indexing.EmbeddingEnabled);
    }

    [Fact]
    public async Task GraphEndpointsRejectStaleGraphWhenLexicalModeIsEnabled()
    {
        var config = new ObsidianRagConfig();
        config.Indexing.EmbeddingEnabled = false;
        config.Search.EnableLexical = true;
        var graph = new Mock<IGraphStore>(MockBehavior.Strict);
        var controller = new GraphController(graph.Object, new Mock<IVectorStore>().Object,
            new Mock<IGraphIndexingService>().Object,
            new ConfigManager(config), NullLogger<GraphController>.Instance);
        Assert.IsType<ConflictObjectResult>((await controller.GetNode("note.md")).Result);
        Assert.IsType<ConflictObjectResult>(controller.StartRebuild().Result);
        graph.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CrossKnowledgeBaseControllerDoesNotTreatSourceNameAsKnowledgeBasePermission()
    {
        var search = new Mock<IKnowledgeBaseSearchService>(MockBehavior.Strict);
        search.Setup(s => s.SearchAsync(It.Is<CrossKnowledgeBaseSearchRequest>(r =>
                r.KnowledgeBaseIds.SequenceEqual(new[] { "allowed", "secret" }) &&
                r.AuthorizedSourceNames != null && r.AuthorizedSourceNames.SequenceEqual(new[] { "allowed" })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CrossKnowledgeBaseSearchResponse("needle", Array.Empty<RecallItem>(),
                new[] { "allowed", "secret" }, Array.Empty<string>(), false, ScoreKind.RRF, TimeSpan.Zero));
        var controller = new KnowledgeBaseSearchController(search.Object);
        var context = new DefaultHttpContext();
        context.Items["CurrentApiKey"] = new ApiKeyEntity { SourceScopeMode = "custom", AllowedSources = "[\"allowed\"]" };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var result = await controller.Search(new CrossKnowledgeBaseSearchRequest("needle", new[] { "allowed", "secret" }), CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
        search.VerifyAll();
    }

    [Fact]
    public async Task CrossKnowledgeBaseMcpToolDoesNotTreatSourceNameAsKnowledgeBasePermission()
    {
        var search = new Mock<IKnowledgeBaseSearchService>(MockBehavior.Strict);
        search.Setup(s => s.SearchAsync(It.Is<CrossKnowledgeBaseSearchRequest>(r =>
                r.KnowledgeBaseIds.SequenceEqual(new[] { "allowed", "secret" }) &&
                r.AuthorizedSourceNames != null && r.AuthorizedSourceNames.SequenceEqual(new[] { "allowed" })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CrossKnowledgeBaseSearchResponse("needle", Array.Empty<RecallItem>(),
                new[] { "allowed", "secret" }, Array.Empty<string>(), false, ScoreKind.RRF, TimeSpan.Zero));
        var context = new DefaultHttpContext();
        context.Items["CurrentApiKey"] = new ApiKeyEntity { SourceScopeMode = "custom", AllowedSources = "[\"allowed\"]" };
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
        services.AddSingleton(new Mock<IKnowledgeBaseService>().Object);
        services.AddSingleton(search.Object);
        using var provider = services.BuildServiceProvider();
        var json = await new KnowledgeBaseMcpTools(provider).KnowledgeBaseSearch("needle", "[\"allowed\",\"secret\"]");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        search.VerifyAll();
    }

    [Fact]
    public async Task CrossKnowledgeBaseMcpListUsesDocumentScopedDiscovery()
    {
        var context = new DefaultHttpContext();
        context.Items["CurrentApiKey"] = new ApiKeyEntity { SourceScopeMode = "custom", AllowedSources = "[\"allowed\"]" };
        var search = new Mock<IKnowledgeBaseSearchService>(MockBehavior.Strict);
        search.Setup(s => s.ListAccessibleKnowledgeBasesAsync(It.Is<IReadOnlyList<string>>(sources => sources.SequenceEqual(new[] { "allowed" })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new KnowledgeBaseDefinition("visible", "Visible", null, true, 1, 1, "", DateTime.UtcNow, DateTime.UtcNow) });
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
        services.AddSingleton(search.Object);
        using var provider = services.BuildServiceProvider();
        using var doc = JsonDocument.Parse(await new KnowledgeBaseMcpTools(provider).KnowledgeBaseList());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("visible", Assert.Single(doc.RootElement.GetProperty("knowledgeBases").EnumerateArray()).GetProperty("id").GetString());
        search.VerifyAll();
    }

    [Fact]
    public async Task ControllerActivationAndStatusDoNotResolveModelsWhenEmbeddingIsDisabled()
    {
        var config = new ObsidianRagConfig();
        config.Indexing.EmbeddingEnabled = false;
        config.Search.EnableLexical = true;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ConfigManager(config));
        services.AddSingleton(new Mock<ISearchService>().Object);
        services.AddSingleton(new Mock<IQueryStatsService>().Object);
        services.AddSingleton(new Mock<IVectorStore>().Object);
        services.AddSingleton(new Mock<IBM25Store>().Object);
        services.AddSingleton<IEmbeddingService>(_ => throw new InvalidOperationException("embedding resolved"));
        services.AddSingleton<IRerankService>(_ => throw new InvalidOperationException("reranker resolved"));
        using var provider = services.BuildServiceProvider();
        var controller = ActivatorUtilities.CreateInstance<AIQueryController>(provider);
        var result = await controller.GetSearchStatus();
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task DrillDownWithNoAuthorizedSourcesDoesNotCallSearch()
    {
        var search = new Mock<ISearchService>(MockBehavior.Strict);
        var controller = new AIQueryController(search.Object, new Mock<IQueryStatsService>().Object,
            new Mock<IEmbeddingService>().Object, new Mock<IVectorStore>().Object,
            new Mock<IBM25Store>().Object, new Mock<IRerankService>().Object,
            new ConfigManager(new ObsidianRagConfig()), NullLogger<AIQueryController>.Instance);
        var context = new DefaultHttpContext();
        context.Items["CurrentApiKey"] = new ApiKeyEntity { SourceScopeMode = "custom", AllowedSources = "[]" };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var result = await controller.DrillDown(new DrillDownRequest { RefIds = new() { "secret" }, Mode = QueryMode.Lexical });
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(Assert.IsType<DrillDownResponse>(ok.Value).ExpandedChunks);
        search.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LexicalMcpToolRejectsRevokedKeyBeforeSearching()
    {
        var search = new Mock<ISearchService>(MockBehavior.Strict);
        var key = new ApiKeyEntity { Id = "revoked", SourceScopeMode = "all" };
        var keyStore = new Mock<IApiKeyStore>(MockBehavior.Strict);
        keyStore.Setup(s => s.GetByIdAsync("revoked")).ReturnsAsync(new ApiKeyEntity { Id = "revoked", IsEnabled = false });
        var context = new DefaultHttpContext();
        context.Items["CurrentApiKey"] = key;
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
        services.AddSingleton(keyStore.Object);
        services.AddSingleton(search.Object);
        using var provider = services.BuildServiceProvider();
        var result = await new RagSearchTools(provider).LexicalSearch("needle");
        using var json = System.Text.Json.JsonDocument.Parse(result);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        search.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LexicalMcpToolUsesLexicalModeAndRestrictsSources()
    {
        var search = new Mock<ISearchService>(MockBehavior.Strict);
        search.Setup(s => s.SearchAsync(It.Is<AIQueryRequest>(r => r.Mode == QueryMode.Lexical &&
                r.Sources != null && r.Sources.Count == 1 && r.Sources[0] == "public"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIQueryResponse { Mode = QueryMode.Lexical });
        var context = new DefaultHttpContext();
        context.Items["CurrentApiKey"] = new ApiKeyEntity { SourceScopeMode = "custom", AllowedSources = "[\"public\"]" };
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
        services.AddSingleton(search.Object);
        using var provider = services.BuildServiceProvider();
        var result = await new RagSearchTools(provider).LexicalSearch("needle", 5, "[\"public\",\"private\"]");
        using var json = System.Text.Json.JsonDocument.Parse(result);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        search.VerifyAll();
    }

    [Fact]
    public async Task LegacyBm25ManagementIsBlockedInLexicalMode()
    {
        var config = new ObsidianRagConfig();
        config.Indexing.EmbeddingEnabled = false;
        config.Search.EnableLexical = true;
        var manager = new ConfigManager(config);
        var bm25 = new Mock<IBM25Store>(MockBehavior.Strict);
        var vector = new Mock<IVectorStore>(MockBehavior.Strict);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var index = new IndexService(provider, new Mock<IIndexEventPublisher>().Object, manager,
            new Mock<IFileIndexPipeline>().Object, new Mock<IFileProcessingGuard>().Object,
            NullLogger<IndexService>.Instance);
        var controller = new BM25IndexController(bm25.Object, vector.Object, manager, index,
            NullLogger<BM25IndexController>.Instance);
        Assert.IsType<ConflictObjectResult>((await controller.Search("secret")).Result);
        Assert.IsType<ConflictObjectResult>(await controller.ClearIndex());
        Assert.IsType<ConflictObjectResult>((await controller.IndexAllDocuments()).Result);
        bm25.VerifyNoOtherCalls();
        vector.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpWithNoAuthorizedSourcesDoesNotCallSearch()
    {
        var search = new Mock<ISearchService>(MockBehavior.Strict);
        var context = new DefaultHttpContext();
        context.Items["CurrentApiKey"] = new ApiKeyEntity { SourceScopeMode = "custom", AllowedSources = "[]" };
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context });
        services.AddSingleton(search.Object);
        using var provider = services.BuildServiceProvider();
        var tool = new RagSearchTools(provider);
        var result = await tool.SemanticSearch("secret");
        using var json = System.Text.Json.JsonDocument.Parse(result);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
        search.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task QueryWithNoAuthorizedSourcesDoesNotCallSearch()
    {
        var search = new Mock<ISearchService>(MockBehavior.Strict);
        var controller = new AIQueryController(search.Object, new Mock<IQueryStatsService>().Object,
            new Mock<IEmbeddingService>().Object, new Mock<IVectorStore>().Object,
            new Mock<IBM25Store>().Object, new Mock<IRerankService>().Object,
            new ConfigManager(new ObsidianRagConfig()), NullLogger<AIQueryController>.Instance);
        var context = new DefaultHttpContext();
        context.Items["CurrentApiKey"] = new ApiKeyEntity { SourceScopeMode = "custom", AllowedSources = "[]" };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        var result = await controller.Query(new AIQueryRequest { Query = "secret", Mode = QueryMode.Lexical });
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Empty(Assert.IsType<AIQueryResponse>(ok.Value).Chunks);
        search.VerifyNoOtherCalls();
    }
}
