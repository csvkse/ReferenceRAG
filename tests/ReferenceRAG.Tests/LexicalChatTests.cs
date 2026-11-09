using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Business.Features.Indexing.Services;
using ReferenceRAG.Storage;
using Microsoft.Data.Sqlite;

namespace ReferenceRAG.Tests;

public class LexicalChatTests
{
    [Fact]
    public async Task ChatReadsOnlyAuthorizedActiveLexicalContent()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var dir = Path.Combine(Path.GetTempPath(), $"lexical-chat-read-{Guid.NewGuid():N}");
        var allowed = Path.Combine(dir, "allowed");
        var secret = Path.Combine(dir, "secret");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(secret);
        try
        {
            var config = new ObsidianRagConfig { Sources = new() {
                new SourceFolder { Name = "allowed", Path = allowed }, new SourceFolder { Name = "secret", Path = secret } } };
            config.Indexing.EmbeddingEnabled = false;
            config.Search.EnableLexical = true;
            config.Search.ChatQueryMode = QueryMode.Lexical;
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var indexer = new LexicalFileIndexer(new MarkdownChunker(), coordinator);
            foreach (var root in new[] { allowed, secret })
            {
                var path = Path.Combine(root, "note.md");
                await File.WriteAllTextAsync(path, $"# Note\n\n{Path.GetFileName(root)}uniquetext");
                await indexer.IndexAsync(path, Path.GetFileName(root));
            }
            var accessor = new Mock<IRequestSourceScopeAccessor>();
            accessor.Setup(a => a.GetAllowedSources()).Returns(new[] { "allowed" });
            var services = new ServiceCollection().AddLogging().AddHttpClient();
            services.AddSingleton(new ConfigManager(config));
            services.AddSingleton(coordinator);
            services.AddSingleton(accessor.Object);
            services.AddSingleton<IVectorStore>(new Mock<IVectorStore>(MockBehavior.Strict).Object);
            using var provider = services.BuildServiceProvider();
            using var chat = new MafChatService(new ConfigurationBuilder().Build(), provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IHttpClientFactory>(), NullLogger<MafChatService>.Instance);
            var method = typeof(MafChatService).GetMethod("ReadFileContentAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var visible = await (Task<string>)method.Invoke(chat, new object?[] { Path.Combine(allowed, "note.md"), 0, 0 })!;
            var hidden = await (Task<string>)method.Invoke(chat, new object?[] { Path.Combine(secret, "note.md"), 0, 0 })!;
            Assert.Contains("alloweduniquetext", visible);
            Assert.DoesNotContain("secretuniquetext", hidden);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ChatSearchToolPassesRequestSourceScope()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var config = new ObsidianRagConfig();
        config.Search.ChatQueryMode = QueryMode.Lexical;
        var search = new Mock<ISearchService>(MockBehavior.Strict);
        search.Setup(s => s.SearchAsync(It.Is<AIQueryRequest>(r => r.Sources != null && r.Sources.SequenceEqual(new[] { "allowed" })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIQueryResponse());
        var accessor = new Mock<IRequestSourceScopeAccessor>();
        accessor.Setup(a => a.GetAllowedSources()).Returns(new[] { "allowed" });
        var services = new ServiceCollection().AddLogging().AddHttpClient();
        services.AddSingleton(new ConfigManager(config));
        services.AddSingleton(search.Object);
        services.AddSingleton(accessor.Object);
        using var provider = services.BuildServiceProvider();
        using var chat = new MafChatService(new ConfigurationBuilder().Build(), provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IHttpClientFactory>(), NullLogger<MafChatService>.Instance);
        var method = typeof(MafChatService).GetMethod("SearchKnowledgeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task<string>)method.Invoke(chat, new object?[] { "needle", 5, null })!;
        search.VerifyAll();
    }

    [Theory]
    [InlineData("\"Lexical\"")]
    [InlineData("5")]
    public void ChatModeCanBeReadFromConfiguration(string mode)
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<ObsidianRagConfig>(
            $"{{\"Search\":{{\"ChatQueryMode\":{mode}}}}}");
        Assert.Equal(QueryMode.Lexical, config!.Search.ChatQueryMode);
    }

    [Fact]
    public async Task ChatTitleSearchDoesNotUseStaleGraphInLexicalMode()
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var config = new ObsidianRagConfig();
        config.Indexing.EmbeddingEnabled = false;
        config.Search.EnableLexical = true;
        var services = new ServiceCollection().AddLogging().AddHttpClient();
        services.AddSingleton(new ConfigManager(config));
        services.AddSingleton<IGraphStore>(_ => throw new InvalidOperationException("stale graph accessed"));
        using var provider = services.BuildServiceProvider();
        using var chat = new MafChatService(new ConfigurationBuilder().Build(), provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IHttpClientFactory>(), NullLogger<MafChatService>.Instance);
        var method = typeof(MafChatService).GetMethod("SearchNoteByTitleAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<string>)method.Invoke(chat, new object?[] { "needle", 5 })!;
        Assert.Contains("词法", result);
        Assert.DoesNotContain("stale graph accessed", result);
    }

    [Theory]
    [InlineData("SearchKnowledgeAsync")]
    [InlineData("BM25SearchAsync")]
    public async Task ChatSearchToolsUseConfiguredLexicalMode(string methodName)
    {
        StaticLogger.LoggerFactory ??= Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var config = new ObsidianRagConfig();
        config.Indexing.EmbeddingEnabled = false;
        config.Search.EnableLexical = true;
        config.Search.ChatQueryMode = QueryMode.Lexical;
        var search = new Mock<ISearchService>(MockBehavior.Strict);
        search.Setup(s => s.SearchAsync(It.Is<AIQueryRequest>(r => r.Query == "needle" && r.Mode == QueryMode.Lexical),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AIQueryResponse { Chunks = new() { new ChunkResult { Content = "needle content", FilePath = "note.md" } } });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(new ConfigManager(config));
        services.AddSingleton(search.Object);
        using var provider = services.BuildServiceProvider();
        using var chat = new MafChatService(new ConfigurationBuilder().Build(), provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IHttpClientFactory>(), NullLogger<MafChatService>.Instance);
        var method = typeof(MafChatService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var result = await (Task<string>)method.Invoke(chat, new object?[] { "needle", 5, null })!;
        Assert.Contains("needle content", result);
        search.VerifyAll();
    }
}
