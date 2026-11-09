using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
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
using ReferenceRAG.Service.Services;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class LexicalModeTransitionTests
{
    private sealed class HostEnvironment : IAsyncDisposable
    {
        public string Root { get; }
        public string SourceA { get; }
        public string SourceB { get; }
        public ObsidianRagConfig Config { get; private set; }
        public ConfigManager Manager { get; private set; }
        public Mock<IEmbeddingService> Embedding { get; }

        private WebApplication? _app;
        private InProcessServer? _server;
        private LexicalIndexCoordinator? _coordinator;

        public WebApplication App => _app ?? throw new InvalidOperationException("Host not started");
        public InProcessServer Server => _server ?? throw new InvalidOperationException("Host not started");
        public IndexService IndexService => App.Services.GetRequiredService<IndexService>();
        public LexicalIndexCoordinator Coordinator => _coordinator ?? throw new InvalidOperationException("Host not started");
        public IVectorStore VectorStore => App.Services.GetRequiredService<IVectorStore>();

        private HostEnvironment(string root, string sourceA, string sourceB, ObsidianRagConfig config, Mock<IEmbeddingService> embedding)
        {
            Root = root;
            SourceA = sourceA;
            SourceB = sourceB;
            Config = config;
            Manager = new ConfigManager(config);
            Embedding = embedding;
        }

        public static async Task<HostEnvironment> CreateAsync(bool embeddingEnabled, bool lexicalEnabled)
        {
            var root = Path.Combine(Path.GetTempPath(), $"p02-host-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var sourceA = Path.Combine(root, "source-a");
            var sourceB = Path.Combine(root, "source-b");
            Directory.CreateDirectory(sourceA);
            Directory.CreateDirectory(sourceB);

            await File.WriteAllTextAsync(Path.Combine(sourceA, "note-a.md"), "# Note A\n\np02_initial_token\n\nContent for note A.");
            await File.WriteAllTextAsync(Path.Combine(sourceB, "note-b.md"), "# Note B\n\np02_b_token\n\nContent for note B in source B.");

            var config = new ObsidianRagConfig
            {
                DataPath = root,
                Sources = new()
                {
                    new SourceFolder { Name = "A", Path = sourceA },
                    new SourceFolder { Name = "B", Path = sourceB }
                }
            };
            config.Indexing.EmbeddingEnabled = embeddingEnabled;
            config.Search.EnableLexical = lexicalEnabled;

            var embedding = new Mock<IEmbeddingService>();
            embedding.SetupGet(e => e.ModelName).Returns("transition-model");
            embedding.SetupGet(e => e.Dimension).Returns(3);
            embedding.Setup(e => e.EncodeBatchAsync(It.IsAny<IEnumerable<string>>(), EmbeddingMode.Document, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<string> input, EmbeddingMode _, CancellationToken _) =>
                    input.Select(text => text.Contains("note-b") || text.Contains("p02_b_token") ? new float[] { 0, 1, 0 } : new float[] { 1, 0, 0 }).ToArray());
            embedding.Setup(e => e.EncodeAsync(It.IsAny<string>(), EmbeddingMode.Query, It.IsAny<CancellationToken>()))
                .ReturnsAsync((string query, EmbeddingMode _, CancellationToken _) =>
                    query.Contains("p02_b_token") ? new float[] { 0, 1, 0 } : new float[] { 1, 0, 0 });

            var env = new HostEnvironment(root, sourceA, sourceB, config, embedding);
            await env.StartHostAsync();
            return env;
        }

        public async Task RestartAsync(bool embeddingEnabled, bool lexicalEnabled)
        {
            if (_coordinator != null)
            {
                await _coordinator.DisposeAsync();
                _coordinator = null;
            }
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
                _server = null;
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            Config.Indexing.EmbeddingEnabled = embeddingEnabled;
            Config.Search.EnableLexical = lexicalEnabled;
            Manager = new ConfigManager(Config);

            await StartHostAsync();
        }

        private async Task StartHostAsync()
        {
            var vectorStore = new SqliteVectorStore(Path.Combine(Root, "vectors.db"), 3);
            var bm25 = new Fts5BM25Store(Path.Combine(Root, "bm25.db"));
            var graph = new SqliteGraphStore(Path.Combine(Root, "graph.db"));
            _coordinator = new LexicalIndexCoordinator(Path.Combine(Root, "vectors.db"), Path.Combine(Root, "bm25.db"));

            var enhancer = new Mock<ITextEnhancer>();
            enhancer.Setup(e => e.Enhance(It.IsAny<ChunkRecord>(), It.IsAny<FileRecord>())).Returns<ChunkRecord, FileRecord>((c, _) => c.Content);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Root });
            builder.Services.AddInProcessServer(listen: false);
            builder.Services.AddControllers().AddApplicationPart(typeof(IndexController).Assembly)
                .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

            builder.Services.AddSingleton(Manager);
            builder.Services.AddSingleton(_coordinator);
            builder.Services.AddSingleton<IVectorStore>(vectorStore);
            builder.Services.AddSingleton<IBM25Store>(bm25);
            builder.Services.AddSingleton<IGraphStore>(graph);
            builder.Services.AddSingleton(Embedding.Object);
            builder.Services.AddSingleton<IMarkdownChunker>(new MarkdownChunker());
            builder.Services.AddSingleton(enhancer.Object);
            builder.Services.AddSingleton<IModelManager>(new Mock<IModelManager>().Object);
            builder.Services.AddSingleton<IQueryStatsService>(new Mock<IQueryStatsService>().Object);
            builder.Services.AddSingleton<IRerankService>(new Mock<IRerankService>().Object);
            builder.Services.AddSingleton<IIndexEventPublisher>(new Mock<IIndexEventPublisher>().Object);
            builder.Services.AddIndexingPipeline();
            builder.Services.AddSearch();
            builder.Services.AddSingleton<IndexService>();

            _app = builder.Build();
            _app.MapControllers();
            await _app.StartAsync();

            _server = _app.Services.GetRequiredService<InProcessServer>();
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
            if (_coordinator != null)
            {
                await _coordinator.DisposeAsync();
                _coordinator = null;
            }
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    [Fact]
    public async Task P02_ModeTransition_VectorToLexical_DisablesVectors_EnablesLexicalQuery()
    {
        await using var env = await HostEnvironment.CreateAsync(embeddingEnabled: true, lexicalEnabled: false);

        // 1. Initial build in Vector mode
        var (initStatus, initBody) = await env.RequestAsync("POST", "/api/index/rebuild", "{}");
        Assert.Equal(202, initStatus);
        using (var doc = JsonDocument.Parse(initBody))
        {
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            var job = env.IndexService.GetStatus(jobId)!;
            await env.IndexService.WaitForCompletionAsync(job);
            Assert.Equal(IndexStatus.Completed, job.Status);
        }

        // Standard vector search works
        var (vStatus, vBody) = await env.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p02_initial_token\",\"mode\":\"Standard\",\"topK\":5}");
        Assert.Equal(200, vStatus);
        using (var doc = JsonDocument.Parse(vBody))
        {
            var chunks = doc.RootElement.GetProperty("chunks");
            Assert.NotEmpty(chunks.EnumerateArray());
            Assert.Contains(chunks.EnumerateArray(), c => c.GetProperty("content").GetString()!.Contains("p02_initial_token"));
        }

        // 2. Restart in Lexical mode (embedding disabled)
        await env.RestartAsync(embeddingEnabled: false, lexicalEnabled: true);

        // Vector rebuild is rejected with 409 embedding_disabled in lexical mode
        var (rejRebuildStatus, rejRebuildBody) = await env.RequestAsync("POST", "/api/index/rebuild", "{}");
        Assert.Equal(409, rejRebuildStatus);

        // Index in lexical mode via /api/index/jobs
        var (lexRebuildStatus, lexRebuildBody) = await env.RequestAsync("POST", "/api/index/jobs", "{\"force\":true}");
        Assert.Equal(202, lexRebuildStatus);
        using (var doc = JsonDocument.Parse(lexRebuildBody))
        {
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            var job = env.IndexService.GetStatus(jobId)!;
            await env.IndexService.WaitForCompletionAsync(job);
            Assert.Equal(IndexStatus.Completed, job.Status);
        }

        // 3. Lexical query succeeds without vector service
        var (lexStatus, lexBody) = await env.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p02_initial_token\",\"mode\":\"Lexical\",\"topK\":5}");
        Assert.Equal(200, lexStatus);
        using (var doc = JsonDocument.Parse(lexBody))
        {
            var chunks = doc.RootElement.GetProperty("chunks");
            Assert.NotEmpty(chunks.EnumerateArray());
            Assert.Contains(chunks.EnumerateArray(), c => c.GetProperty("content").GetString()!.Contains("p02_initial_token"));
        }

        // 4. Standard vector query gracefully degrades to lexical search with 200 OK
        var (rejStatus, rejBody) = await env.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p02_initial_token\",\"mode\":\"Standard\",\"topK\":5}");
        Assert.Equal(200, rejStatus);
        using (var doc = JsonDocument.Parse(rejBody))
        {
            var stats = doc.RootElement.GetProperty("stats");
            Assert.True(stats.GetProperty("isDegraded").GetBoolean());
        }
    }

    [Fact]
    public async Task P02_ModeTransition_LexicalToVector_RequiresRebuild_Blocks409UntilComplete()
    {
        await using var env = await HostEnvironment.CreateAsync(embeddingEnabled: false, lexicalEnabled: true);

        // 1. Initial build in Lexical mode
        var (initStatus, initBody) = await env.RequestAsync("POST", "/api/index/jobs", "{\"force\":true}");
        Assert.Equal(202, initStatus);
        using (var doc = JsonDocument.Parse(initBody))
        {
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            var job = env.IndexService.GetStatus(jobId)!;
            await env.IndexService.WaitForCompletionAsync(job);
            Assert.Equal(IndexStatus.Completed, job.Status);
        }

        // 2. Publish an update to note-a with unique token
        var noteAPath = Path.Combine(env.SourceA, "note-a.md");
        await File.WriteAllTextAsync(noteAPath, "# Note A\n\np02_lex_updated_token\n\nContent for note A updated in lexical mode.");
        var indexer = env.App.Services.GetRequiredService<LexicalFileIndexer>();
        await indexer.IndexAsync(noteAPath, "A");

        // Stale vector source is marked
        var stale = await env.Coordinator.GetStaleVectorSourcesAsync();
        Assert.Contains("A", stale);

        // 3. Restart in Vector mode (embedding enabled, lexical disabled)
        await env.RestartAsync(embeddingEnabled: true, lexicalEnabled: false);

        // Standard vector query before rebuild is blocked with 409 vector_index_incomplete
        var (blockedStatus, blockedBody) = await env.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p02_lex_updated_token\",\"mode\":\"Standard\",\"topK\":5}");
        Assert.Equal(409, blockedStatus);
        using (var doc = JsonDocument.Parse(blockedBody))
        {
            Assert.Equal("vector_index_incomplete", doc.RootElement.GetProperty("code").GetString());
        }

        // 4. Trigger full rebuild
        var (rebStatus, rebBody) = await env.RequestAsync("POST", "/api/index/rebuild", "{}");
        Assert.Equal(202, rebStatus);
        using (var doc = JsonDocument.Parse(rebBody))
        {
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            var job = env.IndexService.GetStatus(jobId)!;
            await env.IndexService.WaitForCompletionAsync(job);
            Assert.Equal(IndexStatus.Completed, job.Status);
        }

        // 5. After full rebuild: query succeeds with new token and stale markers are cleared
        var (okStatus, okBody) = await env.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p02_lex_updated_token\",\"mode\":\"Standard\",\"topK\":5}");
        Assert.Equal(200, okStatus);
        using (var doc = JsonDocument.Parse(okBody))
        {
            var chunks = doc.RootElement.GetProperty("chunks");
            Assert.NotEmpty(chunks.EnumerateArray());
            Assert.Contains(chunks.EnumerateArray(), c => c.GetProperty("content").GetString()!.Contains("p02_lex_updated_token"));
        }

        Assert.Empty(await env.Coordinator.GetStaleVectorSourcesAsync());
    }

    [Fact]
    public async Task P02_ModeTransition_VectorBackToLexical_SyncsNewAndDeletedFiles()
    {
        await using var env = await HostEnvironment.CreateAsync(embeddingEnabled: true, lexicalEnabled: false);

        // 1. Initial build in Vector mode
        var (initStatus, initBody) = await env.RequestAsync("POST", "/api/index/rebuild", "{}");
        Assert.Equal(202, initStatus);
        using (var doc = JsonDocument.Parse(initBody))
        {
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            var job = env.IndexService.GetStatus(jobId)!;
            await env.IndexService.WaitForCompletionAsync(job);
            Assert.Equal(IndexStatus.Completed, job.Status);
        }

        // 2. Modify note-a, add note-c, delete note-b
        await File.WriteAllTextAsync(Path.Combine(env.SourceA, "note-a.md"), "# Note A\n\np02_v2_updated\n\nContent note A v2.");
        await File.WriteAllTextAsync(Path.Combine(env.SourceA, "note-c.md"), "# Note C\n\np02_new_added\n\nBrand new file note C.");
        File.Delete(Path.Combine(env.SourceB, "note-b.md"));

        // Rebuild in Vector mode
        var (v2Status, v2Body) = await env.RequestAsync("POST", "/api/index/rebuild", "{}");
        Assert.Equal(202, v2Status);
        using (var doc = JsonDocument.Parse(v2Body))
        {
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            var job = env.IndexService.GetStatus(jobId)!;
            await env.IndexService.WaitForCompletionAsync(job);
            Assert.Equal(IndexStatus.Completed, job.Status);
        }

        // 3. Switch back to Lexical mode
        await env.RestartAsync(embeddingEnabled: false, lexicalEnabled: true);

        // Rebuild / sync in Lexical mode
        var (lexRebStatus, lexRebBody) = await env.RequestAsync("POST", "/api/index/jobs", "{\"force\":true}");
        Assert.Equal(202, lexRebStatus);
        using (var doc = JsonDocument.Parse(lexRebBody))
        {
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            var job = env.IndexService.GetStatus(jobId)!;
            await env.IndexService.WaitForCompletionAsync(job);
            Assert.Equal(IndexStatus.Completed, job.Status);
        }

        // 4. Query Lexical: updated note-a is found
        var (aStatus, aBody) = await env.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p02_v2_updated\",\"mode\":\"Lexical\",\"topK\":5}");
        Assert.Equal(200, aStatus);
        using (var doc = JsonDocument.Parse(aBody))
        {
            var chunks = doc.RootElement.GetProperty("chunks");
            Assert.Contains(chunks.EnumerateArray(), c => c.GetProperty("content").GetString()!.Contains("p02_v2_updated"));
        }

        // 5. Query Lexical: new note-c is found
        var (cStatus, cBody) = await env.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p02_new_added\",\"mode\":\"Lexical\",\"topK\":5}");
        Assert.Equal(200, cStatus);
        using (var doc = JsonDocument.Parse(cBody))
        {
            var chunks = doc.RootElement.GetProperty("chunks");
            Assert.Contains(chunks.EnumerateArray(), c => c.GetProperty("content").GetString()!.Contains("p02_new_added"));
        }

        // 6. Query Lexical: deleted note-b is NOT found
        var (bStatus, bBody) = await env.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p02_b_token\",\"mode\":\"Lexical\",\"topK\":5}");
        Assert.Equal(200, bStatus);
        using (var doc = JsonDocument.Parse(bBody))
        {
            var chunks = doc.RootElement.GetProperty("chunks");
            Assert.DoesNotContain(chunks.EnumerateArray(), c => c.GetProperty("filePath").GetString()!.Contains("note-b.md"));
            Assert.DoesNotContain(chunks.EnumerateArray(), c => c.GetProperty("content").GetString()!.Contains("p02_b_token"));
        }
    }
}
