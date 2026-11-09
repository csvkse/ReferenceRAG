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

public class VectorRecoveryConcurrencyTests
{
    private sealed class TestFixture : IAsyncDisposable
    {
        public string Root { get; }
        public string SourceA { get; }
        public string SourceB { get; }
        public ObsidianRagConfig Config { get; }
        public ConfigManager Manager { get; }
        public SqliteVectorStore VectorStore { get; }
        public Fts5BM25Store BM25Store { get; }
        public SqliteGraphStore GraphStore { get; }
        public LexicalIndexCoordinator Coordinator { get; }
        public Mock<IEmbeddingService> Embedding { get; }
        public WebApplication App { get; }
        public InProcessServer Server { get; }
        public IndexService IndexService { get; }

        private TestFixture(string root, string sourceA, string sourceB, ObsidianRagConfig config, ConfigManager manager,
            SqliteVectorStore vectorStore, Fts5BM25Store bm25Store, SqliteGraphStore graphStore,
            LexicalIndexCoordinator coordinator, Mock<IEmbeddingService> embedding, WebApplication app,
            InProcessServer server, IndexService indexService)
        {
            Root = root;
            SourceA = sourceA;
            SourceB = sourceB;
            Config = config;
            Manager = manager;
            VectorStore = vectorStore;
            BM25Store = bm25Store;
            GraphStore = graphStore;
            Coordinator = coordinator;
            Embedding = embedding;
            App = app;
            Server = server;
            IndexService = indexService;
        }

        public static async Task<TestFixture> CreateAsync()
        {
            StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
            var root = Path.Combine(Path.GetTempPath(), $"p01-test-{Guid.NewGuid():N}");
            var sourceA = Path.Combine(root, "source-a");
            var sourceB = Path.Combine(root, "source-b");
            Directory.CreateDirectory(sourceA);
            Directory.CreateDirectory(sourceB);

            await File.WriteAllTextAsync(Path.Combine(sourceA, "note-a.md"), "# Note A\n\np01_old_token link to [[note-b]]\n\nMore substantial content for note A to exceed chunk length requirements.");
            await File.WriteAllTextAsync(Path.Combine(sourceB, "note-b.md"), "# Note B\n\np01_b_token\n\nContent for note B in source B.");

            var config = new ObsidianRagConfig
            {
                DataPath = root,
                Sources = new()
                {
                    new SourceFolder { Name = "A", Path = sourceA },
                    new SourceFolder { Name = "B", Path = sourceB }
                }
            };
            config.Indexing.EmbeddingEnabled = true;
            config.Search.EnableLexical = false;
            var manager = new ConfigManager(config);

            var vectorStore = new SqliteVectorStore(Path.Combine(root, "vectors.db"), 3);
            var bm25 = new Fts5BM25Store(Path.Combine(root, "bm25.db"));
            var graph = new SqliteGraphStore(Path.Combine(root, "graph.db"));
            var coordinator = new LexicalIndexCoordinator(Path.Combine(root, "vectors.db"), Path.Combine(root, "bm25.db"));

            var embedding = new Mock<IEmbeddingService>();
            embedding.SetupGet(e => e.ModelName).Returns("recovery-model");
            embedding.SetupGet(e => e.Dimension).Returns(3);
            embedding.Setup(e => e.EncodeBatchAsync(It.IsAny<IEnumerable<string>>(), EmbeddingMode.Document, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<string> input, EmbeddingMode _, CancellationToken _) =>
                    input.Select(text => text.Contains("Note B") || text.Contains("p01_b_token") ? new float[] { 0, 1, 0 } : new float[] { 1, 0, 0 }).ToArray());
            embedding.Setup(e => e.EncodeAsync(It.IsAny<string>(), EmbeddingMode.Query, It.IsAny<CancellationToken>()))
                .ReturnsAsync((string query, EmbeddingMode _, CancellationToken _) =>
                    query.Contains("p01_b_token") ? new float[] { 0, 1, 0 } : new float[] { 1, 0, 0 });

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

            var webApp = builder.Build();
            webApp.MapControllers();
            await webApp.StartAsync();

            var server = webApp.Services.GetRequiredService<InProcessServer>();
            var indexService = webApp.Services.GetRequiredService<IndexService>();

            return new TestFixture(root, sourceA, sourceB, config, manager, vectorStore, bm25, graph,
                coordinator, embedding, webApp, server, indexService);
        }

        public async Task<(int Status, string Body)> RequestAsync(string method, string path, string? body = null)
        {
            using var input = body == null ? new MemoryStream() : new MemoryStream(Encoding.UTF8.GetBytes(body));
            using var output = new MemoryStream();
            var response = new HttpResponseFeature();
            var headers = new Dictionary<string, string>();
            if (body != null) headers["Content-Type"] = "application/json";
            await Server.DispatchAsync(method, path, headers, input, output, response, CancellationToken.None);
            return (response.StatusCode, Encoding.UTF8.GetString(output.ToArray()));
        }

        public async Task PublishNewLexicalVersionOfAAsync(string newToken = "p01_new_token")
        {
            var path = Path.Combine(SourceA, "note-a.md");
            await File.WriteAllTextAsync(path, $"# Note A\n\n{newToken} updated content linked to [[note-b]]\n\nMore substantial content for note A.");
            var indexer = new LexicalFileIndexer(new MarkdownChunker(), Coordinator, Manager);
            await indexer.IndexAsync(path, "A");
        }

        public async ValueTask DisposeAsync()
        {
            IndexService.BeforeCommitVerifiedHook = null;
            IndexService.AfterRecoveryLeaseAcquiredHook = null;
            IndexController.BeforePreCleanupHook = null;
            GraphController.BeforeGraphClearHook = null;

            await App.StopAsync();
            await App.DisposeAsync();
            await Coordinator.DisposeAsync();
            VectorStore.Dispose();
            BM25Store.Dispose();
            GraphStore.Dispose();

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, true); } catch { }
        }
    }

    [Fact]
    public async Task P01_ManagementWrite_DELETE_Model_QueuesOrRejects_And_PersistsStale()
    {
        await using var f = await TestFixture.CreateAsync();
        // 1. Initial build: both A and B
        var initialJob = await f.IndexService.StartIndexAsync(new IndexRequest { Force = true, Sources = new() { "A", "B" } });
        await f.IndexService.WaitForCompletionAsync(initialJob);
        Assert.Equal(IndexStatus.Completed, initialJob.Status);

        // 2. Publish new lexical version of A -> A becomes stale
        await f.PublishNewLexicalVersionOfAAsync();
        Assert.Contains("A", await f.Coordinator.GetStaleVectorSourcesAsync());

        // 3. Setup Pause Point B (before CommitVerified)
        var reachedPauseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowContinueB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IndexService.BeforeCommitVerifiedHook = async () =>
        {
            reachedPauseB.TrySetResult();
            await allowContinueB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        // 4. Start recovery rebuild
        var rebuildTask = Task.Run(async () =>
        {
            var (status, body) = await f.RequestAsync("POST", "/api/index/rebuild", "{}");
            Assert.Equal(202, status);
            using var doc = JsonDocument.Parse(body);
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            var job = f.IndexService.GetStatus(jobId)!;
            return await f.IndexService.WaitForCompletionAsync(job);
        });

        await reachedPauseB.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 5. While paused at B, issue DELETE /api/index/models/recovery-model
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteTask = Task.Run(async () =>
        {
            deleteStarted.TrySetResult();
            return await f.RequestAsync("DELETE", "/api/index/models/recovery-model");
        });

        await deleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Give short window to confirm delete is queued on the write lease and didn't preempt storage
        await Task.Delay(100);
        Assert.False(deleteTask.IsCompleted, "DELETE should be queued behind recovery write lease");

        // 6. Release Pause Point B
        allowContinueB.TrySetResult();

        var completedRebuild = await rebuildTask;
        Assert.Equal(IndexStatus.Completed, completedRebuild.Status);

        var (deleteStatus, _) = await deleteTask;
        Assert.Equal(200, deleteStatus);

        // 7. Verify stale status was persisted and model vectors are removed
        var stale = await f.Coordinator.GetStaleVectorSourcesAsync();
        Assert.Contains("*", stale);
        var stats = await f.VectorStore.GetVectorStatsAsync();
        Assert.DoesNotContain(stats, s => s.ModelName == "recovery-model");
    }

    [Fact]
    public async Task P01_ManagementWrite_DELETE_AllModels_QueuesOrRejects_And_PersistsStale()
    {
        await using var f = await TestFixture.CreateAsync();
        var initialJob = await f.IndexService.StartIndexAsync(new IndexRequest { Force = true, Sources = new() { "A", "B" } });
        await f.IndexService.WaitForCompletionAsync(initialJob);

        await f.PublishNewLexicalVersionOfAAsync();

        var reachedPauseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowContinueB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IndexService.BeforeCommitVerifiedHook = async () =>
        {
            reachedPauseB.TrySetResult();
            await allowContinueB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        var rebuildTask = Task.Run(async () =>
        {
            var (status, body) = await f.RequestAsync("POST", "/api/index/rebuild", "{}");
            Assert.Equal(202, status);
            using var doc = JsonDocument.Parse(body);
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            return await f.IndexService.WaitForCompletionAsync(f.IndexService.GetStatus(jobId)!);
        });

        await reachedPauseB.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var deleteTask = Task.Run(async () => await f.RequestAsync("DELETE", "/api/index/models"));
        await Task.Delay(100);
        Assert.False(deleteTask.IsCompleted);

        allowContinueB.TrySetResult();
        await rebuildTask;

        var (deleteStatus, _) = await deleteTask;
        Assert.Equal(200, deleteStatus);

        var stale = await f.Coordinator.GetStaleVectorSourcesAsync();
        Assert.Contains("*", stale);
    }

    [Fact]
    public async Task P01_ManagementWrite_DELETE_AllData_QueuesOrRejects_And_PersistsStale()
    {
        await using var f = await TestFixture.CreateAsync();
        var initialJob = await f.IndexService.StartIndexAsync(new IndexRequest { Force = true, Sources = new() { "A", "B" } });
        await f.IndexService.WaitForCompletionAsync(initialJob);

        await f.PublishNewLexicalVersionOfAAsync();

        var reachedPauseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowContinueB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IndexService.BeforeCommitVerifiedHook = async () =>
        {
            reachedPauseB.TrySetResult();
            await allowContinueB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        var rebuildTask = Task.Run(async () =>
        {
            var (status, body) = await f.RequestAsync("POST", "/api/index/rebuild", "{}");
            Assert.Equal(202, status);
            using var doc = JsonDocument.Parse(body);
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            return await f.IndexService.WaitForCompletionAsync(f.IndexService.GetStatus(jobId)!);
        });

        await reachedPauseB.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var deleteTask = Task.Run(async () => await f.RequestAsync("DELETE", "/api/index/data"));
        await Task.Delay(100);
        Assert.False(deleteTask.IsCompleted);

        allowContinueB.TrySetResult();
        await rebuildTask;

        var (deleteStatus, _) = await deleteTask;
        Assert.Equal(200, deleteStatus);

        var stale = await f.Coordinator.GetStaleVectorSourcesAsync();
        Assert.Contains("*", stale);
    }

    [Fact]
    public async Task P01_ManagementWrite_POST_Cleanup_QueuesOrRejects_WithoutCorruptingIndexes()
    {
        await using var f = await TestFixture.CreateAsync();
        var initialJob = await f.IndexService.StartIndexAsync(new IndexRequest { Force = true, Sources = new() { "A", "B" } });
        await f.IndexService.WaitForCompletionAsync(initialJob);

        await f.PublishNewLexicalVersionOfAAsync();

        var reachedPauseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowContinueB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IndexService.BeforeCommitVerifiedHook = async () =>
        {
            reachedPauseB.TrySetResult();
            await allowContinueB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        var rebuildTask = Task.Run(async () =>
        {
            var (status, body) = await f.RequestAsync("POST", "/api/index/rebuild", "{}");
            Assert.Equal(202, status);
            using var doc = JsonDocument.Parse(body);
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            return await f.IndexService.WaitForCompletionAsync(f.IndexService.GetStatus(jobId)!);
        });

        await reachedPauseB.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cleanupTask = Task.Run(async () => await f.RequestAsync("POST", "/api/index/cleanup"));
        await Task.Delay(100);
        Assert.False(cleanupTask.IsCompleted);

        allowContinueB.TrySetResult();
        var rebuildJob = await rebuildTask;
        Assert.Equal(IndexStatus.Completed, rebuildJob.Status);

        var (cleanupStatus, _) = await cleanupTask;
        Assert.Equal(200, cleanupStatus);

        // Verification completed, cleanup did not wipe active files
        var files = await f.VectorStore.GetAllFilesAsync();
        Assert.NotEmpty(files);
    }

    [Fact]
    public async Task P01_RebuildPreCleanup_OrderRecoveryTakesOverFirst_ProtectsVerifiedData()
    {
        await using var f = await TestFixture.CreateAsync();
        var initialJob = await f.IndexService.StartIndexAsync(new IndexRequest { Force = true, Sources = new() { "A", "B" } });
        await f.IndexService.WaitForCompletionAsync(initialJob);

        // State is not stale initially
        Assert.Empty(await f.Coordinator.GetStaleVectorSourcesAsync());

        // Pause C: Rebuild pre-cleanup pauses before clearing
        var reachedPauseC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowContinueC = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IndexController.BeforePreCleanupHook = async () =>
        {
            reachedPauseC.TrySetResult();
            await allowContinueC.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };

        var rebuildJobTask = Task.Run(async () =>
        {
            var (status, body) = await f.RequestAsync("POST", "/api/index/rebuild", "{\"deleteExisting\":true}");
            Assert.Equal(202, status);
            using var doc = JsonDocument.Parse(body);
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            return await f.IndexService.WaitForCompletionAsync(f.IndexService.GetStatus(jobId)!);
        });

        await reachedPauseC.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Recovery takes over: publish new version of A and run full recovery
        await f.PublishNewLexicalVersionOfAAsync("p01_new_token_preempt");
        Assert.Contains("A", await f.Coordinator.GetStaleVectorSourcesAsync());

        // Now release Pause Point C so the old rebuild resumes under its write lease
        allowContinueC.TrySetResult();

        var job = await rebuildJobTask;
        Assert.Equal(IndexStatus.Completed, job.Status);

        // Verify that data was not wiped out by the old pre-cleanup
        var fileA = await f.VectorStore.GetFileByPathAsync(Path.Combine(f.SourceA, "note-a.md"));
        Assert.NotNull(fileA);
        var chunks = await f.VectorStore.GetChunksByFileAsync(fileA!.Id);
        Assert.NotEmpty(chunks);
        Assert.Contains("p01_new_token_preempt", chunks.First().Content);
    }

    [Fact]
    public async Task P01_NormalEntryRecovery_And_ActualQuery()
    {
        await using var f = await TestFixture.CreateAsync();
        // 1. Initial build: both A and B
        var initialJob = await f.IndexService.StartIndexAsync(new IndexRequest { Force = true, Sources = new() { "A", "B" } });
        await f.IndexService.WaitForCompletionAsync(initialJob);

        // 2. Publish new version of A with p01_new_token
        await f.PublishNewLexicalVersionOfAAsync("p01_new_token");
        Assert.Contains("A", await f.Coordinator.GetStaleVectorSourcesAsync());

        // 3. Before recovery: query Standard / Hybrid -> 409 vector_index_incomplete
        var (queryStatus, queryBody) = await f.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p01_new_token\",\"mode\":\"Standard\",\"topK\":5}");
        Assert.Equal(409, queryStatus);
        using (var doc = JsonDocument.Parse(queryBody))
            Assert.Equal("vector_index_incomplete", doc.RootElement.GetProperty("code").GetString());

        var (hybridStatus, hybridBody) = await f.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p01_new_token\",\"mode\":\"Hybrid\",\"topK\":5}");
        Assert.Equal(409, hybridStatus);

        // 4. Trigger recovery rebuild via standard POST /api/index/rebuild
        var (rebuildStatus, rebuildBody) = await f.RequestAsync("POST", "/api/index/rebuild", "{}");
        Assert.Equal(202, rebuildStatus);
        using (var doc = JsonDocument.Parse(rebuildBody))
        {
            var jobId = doc.RootElement.GetProperty("jobId").GetString()!;
            var job = f.IndexService.GetStatus(jobId)!;
            await f.IndexService.WaitForCompletionAsync(job);
            Assert.Equal(IndexStatus.Completed, job.Status);
        }

        // 5. After recovery: queries succeed with new token
        var (okStatus, okBody) = await f.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p01_new_token\",\"mode\":\"Standard\",\"topK\":5}");
        Assert.Equal(200, okStatus);
        using (var doc = JsonDocument.Parse(okBody))
        {
            var chunks = doc.RootElement.GetProperty("chunks");
            Assert.NotEmpty(chunks.EnumerateArray());
            Assert.Contains(chunks.EnumerateArray(), c => c.GetProperty("content").GetString()!.Contains("p01_new_token"));
        }

        // Old token is not in any chunk
        var (oldStatus, oldBody) = await f.RequestAsync("POST", "/api/ai/query", "{\"query\":\"p01_old_token\",\"mode\":\"Standard\",\"topK\":5}");
        Assert.Equal(200, oldStatus);
        using (var doc = JsonDocument.Parse(oldBody))
        {
            var chunks = doc.RootElement.GetProperty("chunks");
            Assert.DoesNotContain(chunks.EnumerateArray(), c => c.GetProperty("content").GetString()!.Contains("p01_old_token"));
        }
    }
}
