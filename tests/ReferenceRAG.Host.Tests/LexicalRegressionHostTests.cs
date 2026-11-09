using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.ApiHost.Transport;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Business.Features.Indexing.Services;
using ReferenceRAG.Core.Extensions;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Services.Graph;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.McpTools;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class LexicalRegressionHostTests
{
    private sealed class RegressionFixture : IAsyncDisposable
    {
        public string Root { get; }
        public string SourceA { get; }
        public string SourceB { get; }
        public ObsidianRagConfig Config { get; }
        public ConfigManager Manager { get; }
        public WebApplication App { get; }
        public InProcessServer Server { get; }
        public LexicalIndexCoordinator Coordinator { get; }

        public static async Task<RegressionFixture> CreateAsync(Action<HttpContext>? configureContext = null)
        {
            var root = Path.Combine(Path.GetTempPath(), $"p03-host-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var sourceA = Path.Combine(root, "source-a");
            var sourceB = Path.Combine(root, "source-b");
            Directory.CreateDirectory(sourceA);
            Directory.CreateDirectory(sourceB);

            await File.WriteAllTextAsync(Path.Combine(sourceA, "note-a.md"), "# Note A\n\nalphakw\n\nContent for note A.");
            await File.WriteAllTextAsync(Path.Combine(sourceB, "note-b.md"), "# Note B\n\nbetakw\n\nContent for note B in source B.");

            var config = new ObsidianRagConfig
            {
                DataPath = root,
                Sources = new()
                {
                    new SourceFolder { Name = "A", Path = sourceA },
                    new SourceFolder { Name = "B", Path = sourceB }
                }
            };
            config.Indexing.EmbeddingEnabled = false;
            config.Search.EnableLexical = true;
            var manager = new ConfigManager(config);

            var vectorStore = new SqliteVectorStore(Path.Combine(root, "vectors.db"), 3);
            var bm25 = new Fts5BM25Store(Path.Combine(root, "bm25.db"));
            var graph = new SqliteGraphStore(Path.Combine(root, "graph.db"));
            var coordinator = new LexicalIndexCoordinator(Path.Combine(root, "vectors.db"), Path.Combine(root, "bm25.db"));

            var embedding = new Mock<IEmbeddingService>();
            embedding.SetupGet(e => e.ModelName).Returns("test-model");
            embedding.SetupGet(e => e.Dimension).Returns(3);

            var enhancer = new Mock<ITextEnhancer>();
            enhancer.Setup(e => e.Enhance(It.IsAny<ChunkRecord>(), It.IsAny<FileRecord>())).Returns<ChunkRecord, FileRecord>((c, _) => c.Content);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.Services.AddInProcessServer(listen: false);
            builder.Services.AddControllers().AddApplicationPart(typeof(IndexController).Assembly)
                .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

            builder.Services.AddSingleton(manager);
            builder.Services.AddSingleton(coordinator);
            builder.Services.AddSingleton<IVectorStore>(vectorStore);
            builder.Services.AddSingleton<IBM25Store>(bm25);
            builder.Services.AddSingleton<IGraphStore>(graph);
            builder.Services.AddSingleton(embedding.Object);
            builder.Services.AddSingleton<IMarkdownChunker>(new MarkdownChunker());
            builder.Services.AddSingleton(enhancer.Object);
            builder.Services.AddSingleton<IModelManager>(new Mock<IModelManager>().Object);
            builder.Services.AddSingleton<IQueryStatsService>(new Mock<IQueryStatsService>().Object);
            builder.Services.AddSingleton<IRerankService>(new Mock<IRerankService>().Object);
            builder.Services.AddSingleton<IIndexEventPublisher>(new Mock<IIndexEventPublisher>().Object);
            builder.Services.AddIndexingPipeline();
            builder.Services.AddSearch();
            builder.Services.AddSingleton<IndexService>();

            var app = builder.Build();
            if (configureContext != null)
            {
                app.Use(async (ctx, next) =>
                {
                    configureContext(ctx);
                    await next();
                });
            }
            app.MapControllers();
            await app.StartAsync();

            var server = app.Services.GetRequiredService<InProcessServer>();

            // Initial index of both files in lexical mode
            var indexer = app.Services.GetRequiredService<LexicalFileIndexer>();
            await indexer.IndexAsync(Path.Combine(sourceA, "note-a.md"), "A");
            await indexer.IndexAsync(Path.Combine(sourceB, "note-b.md"), "B");

            return new RegressionFixture(root, sourceA, sourceB, config, manager, app, server, coordinator);
        }

        private RegressionFixture(string root, string sourceA, string sourceB, ObsidianRagConfig config, ConfigManager manager,
            WebApplication app, InProcessServer server, LexicalIndexCoordinator coordinator)
        {
            Root = root;
            SourceA = sourceA;
            SourceB = sourceB;
            Config = config;
            Manager = manager;
            App = app;
            Server = server;
            Coordinator = coordinator;
        }

        public async Task<(int StatusCode, string Body)> RequestAsync(string method, string path, string? body = null, Dictionary<string, string>? headers = null)
        {
            var reqHeaders = headers ?? new Dictionary<string, string>();
            if (body != null && !reqHeaders.ContainsKey("Content-Type"))
                reqHeaders["Content-Type"] = "application/json";

            using var input = new MemoryStream(body == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body));
            using var output = new MemoryStream();
            var responseFeature = new HttpResponseFeature();

            await Server.DispatchAsync(method, path, reqHeaders, input, output, responseFeature, CancellationToken.None);
            return (responseFeature.StatusCode, Encoding.UTF8.GetString(output.ToArray()));
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await App.StopAsync();
            await App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    [Fact]
    public async Task P03_E1_RESTSearch_And_Status()
    {
        await using var f = await RegressionFixture.CreateAsync();

        // 1. GET /api/ai/status
        var (statusStatus, statusBody) = await f.RequestAsync("GET", "/api/ai/status");
        Assert.Equal(200, statusStatus);

        // 2. POST /api/ai/query with mode=Lexical
        var (qStatus, qBody) = await f.RequestAsync("POST", "/api/ai/query", "{\"query\":\"alphakw\",\"mode\":\"Lexical\",\"topK\":1}");
        Assert.Equal(200, qStatus);
        using (var doc = JsonDocument.Parse(qBody))
        {
            Assert.Equal("Lexical", doc.RootElement.GetProperty("mode").GetString());
            var chunks = doc.RootElement.GetProperty("chunks");
            Assert.NotEmpty(chunks.EnumerateArray());
            var first = chunks[0];
            Assert.Contains("alphakw", first.GetProperty("content").GetString());
            Assert.Equal("A", first.GetProperty("source").GetString());
            Assert.Equal(1, first.GetProperty("startLine").GetInt32());
            Assert.Null(first.GetProperty("embeddingScore").GetString());
            Assert.Null(first.GetProperty("rerankScore").GetString());
        }

        // 3. POST /api/ai/query with mode=Standard in lexical mode gracefully degrades to Lexical
        var (stdStatus, stdBody) = await f.RequestAsync("POST", "/api/ai/query", "{\"query\":\"alphakw\",\"mode\":\"Standard\",\"topK\":1}");
        Assert.Equal(200, stdStatus);
        using (var doc = JsonDocument.Parse(stdBody))
        {
            var stats = doc.RootElement.GetProperty("stats");
            Assert.True(stats.GetProperty("isDegraded").GetBoolean());
        }
    }

    [Fact]
    public async Task P03_E2_RESTDrillDown_And_Invalidation()
    {
        await using var f = await RegressionFixture.CreateAsync();

        // 1. Get valid refId from query
        var (_, qBody) = await f.RequestAsync("POST", "/api/ai/query", "{\"query\":\"alphakw\",\"mode\":\"Lexical\",\"topK\":1}");
        string refId;
        using (var doc = JsonDocument.Parse(qBody))
            refId = doc.RootElement.GetProperty("chunks")[0].GetProperty("refId").GetString()!;

        // 2. Drill-down on valid refId
        var (ddStatus, ddBody) = await f.RequestAsync("POST", "/api/ai/drill-down", $"{{\"refIds\":[\"{refId}\"],\"mode\":\"Lexical\"}}");
        Assert.Equal(200, ddStatus);
        using (var doc = JsonDocument.Parse(ddBody))
        {
            var chunks = doc.RootElement.GetProperty("expandedChunks");
            Assert.NotEmpty(chunks.EnumerateArray());
            Assert.Contains("alphakw", chunks[0].GetProperty("content").GetString());
        }

        // 3. Drill-down on invalid refId returns empty
        var (fakeStatus, fakeBody) = await f.RequestAsync("POST", "/api/ai/drill-down", "{\"refIds\":[\"non-existent-ref\"],\"mode\":\"Lexical\"}");
        Assert.Equal(200, fakeStatus);
        using (var doc = JsonDocument.Parse(fakeBody))
        {
            var chunks = doc.RootElement.GetProperty("expandedChunks");
            Assert.Empty(chunks.EnumerateArray());
        }
    }

    [Fact]
    public async Task P03_E3_McpLexicalSearch_And_Scope()
    {
        await using var f = await RegressionFixture.CreateAsync();
        var tools = new RagSearchTools(f.App.Services);

        // Tool call with source filter
        var resJson = await tools.LexicalSearch("alphakw", 5, "[\"A\"]");
        using (var doc = JsonDocument.Parse(resJson))
        {
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("count").GetInt32() > 0);
        }

        // Tool call with non-matching source filter returns 0 hits
        var zeroJson = await tools.LexicalSearch("alphakw", 5, "[\"B\"]");
        using (var doc = JsonDocument.Parse(zeroJson))
        {
            Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        }
    }

    [Fact]
    public async Task P03_E7_DirectBM25_RejectsWritesInLexicalMode()
    {
        await using var f = await RegressionFixture.CreateAsync();

        // Direct BM25 write endpoints reject in lexical mode
        var (postStatus, _) = await f.RequestAsync("POST", "/api/bm25index/index", "{}");
        Assert.Equal(409, postStatus);

        var (delStatus, _) = await f.RequestAsync("DELETE", "/api/bm25index/index");
        Assert.Equal(409, delStatus);
    }

    [Fact]
    public async Task P03_A1_ApiKeyModuleAndSourceAuthorization()
    {
        // When key has empty allowed sources, search returns 0 chunks
        await using var f = await RegressionFixture.CreateAsync(ctx =>
        {
            ctx.Items["CurrentApiKey"] = new ApiKeyEntity
            {
                SourceScopeMode = "custom",
                AllowedSources = "[]"
            };
        });

        var (status, body) = await f.RequestAsync("POST", "/api/ai/query", "{\"query\":\"alphakw\",\"mode\":\"Lexical\",\"topK\":5}");
        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Empty(doc.RootElement.GetProperty("chunks").EnumerateArray());
    }

    [Fact]
    public async Task P03_A2_SourceIntersectionAndOrder()
    {
        // User key only authorizes Source A
        await using var f = await RegressionFixture.CreateAsync(ctx =>
        {
            ctx.Items["CurrentApiKey"] = new ApiKeyEntity
            {
                SourceScopeMode = "custom",
                AllowedSources = "[\"A\"]"
            };
        });

        // Client requests both A and B, but server scopes to intersection [A]
        var (status, body) = await f.RequestAsync("POST", "/api/ai/query", "{\"query\":\"Content\",\"sources\":[\"A\",\"B\"],\"mode\":\"Lexical\",\"topK\":10}");
        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(body);
        var chunks = doc.RootElement.GetProperty("chunks");
        Assert.NotEmpty(chunks.EnumerateArray());
        Assert.All(chunks.EnumerateArray(), c => Assert.Equal("A", c.GetProperty("source").GetString()));
    }
}
