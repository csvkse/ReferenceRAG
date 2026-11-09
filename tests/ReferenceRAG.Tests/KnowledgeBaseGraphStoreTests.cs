using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Models;
using Xunit;

namespace ReferenceRAG.Tests;

public class KnowledgeBaseGraphStoreTests : IDisposable
{
    private readonly string _testDir;

    public KnowledgeBaseGraphStoreTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"kb-graph-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task SqliteKnowledgeBaseStore_GraphStore_UpsertAndRetrieveNode()
    {
        var dbPath = Path.Combine(_testDir, "kb1.db");
        using var store = new SqliteKnowledgeBaseStore("kb1", dbPath, NullLogger<SqliteKnowledgeBaseStore>.Instance);
        await store.InitializeAsync();

        var graphStore = store.GraphStore;
        var node = new GraphNode
        {
            Id = "docs/architecture.md",
            Title = "系统架构设计",
            Type = "document"
        };

        await graphStore.UpsertNodeAsync(node);

        var retrieved = await graphStore.GetNodeAsync("docs/architecture.md");
        Assert.NotNull(retrieved);
        Assert.Equal("docs/architecture.md", retrieved!.Id);
        Assert.Equal("系统架构设计", retrieved.Title);
        Assert.Equal("document", retrieved.Type);
    }

    [Fact]
    public async Task SqliteKnowledgeBaseStore_GraphStore_EdgesAndNeighborsTraversal()
    {
        var dbPath = Path.Combine(_testDir, "kb_graph.db");
        using var store = new SqliteKnowledgeBaseStore("kb_graph", dbPath, NullLogger<SqliteKnowledgeBaseStore>.Instance);
        await store.InitializeAsync();

        var graph = store.GraphStore;

        await graph.UpsertNodeAsync(new GraphNode { Id = "root.md", Title = "根索引", Type = "document" });
        await graph.UpsertNodeAsync(new GraphNode { Id = "module_a.md", Title = "模块A", Type = "document" });
        await graph.UpsertNodeAsync(new GraphNode { Id = "submodule_a1.md", Title = "子模块A1", Type = "document" });

        await graph.UpsertEdgesAsync(new[]
        {
            new GraphEdge { FromId = "root.md", ToId = "module_a.md", Type = "wikilink", LineNumber = 10 },
            new GraphEdge { FromId = "module_a.md", ToId = "submodule_a1.md", Type = "wikilink", LineNumber = 20 }
        });

        // 深度 1
        var depth1 = await graph.GetNeighborsAsync("root.md", 1);
        Assert.Equal("root.md", depth1.RootId);
        Assert.Contains(depth1.Nodes, n => n.Id == "module_a.md");
        Assert.DoesNotContain(depth1.Nodes, n => n.Id == "submodule_a1.md");

        // 深度 2
        var depth2 = await graph.GetNeighborsAsync("root.md", 2);
        Assert.Contains(depth2.Nodes, n => n.Id == "module_a.md");
        Assert.Contains(depth2.Nodes, n => n.Id == "submodule_a1.md");
        Assert.Equal(2, depth2.Edges.Count);
    }

    [Fact]
    public async Task SqliteKnowledgeBaseStore_GraphStore_SearchAndStats()
    {
        var dbPath = Path.Combine(_testDir, "kb_search.db");
        using var store = new SqliteKnowledgeBaseStore("kb_search", dbPath, NullLogger<SqliteKnowledgeBaseStore>.Instance);
        await store.InitializeAsync();

        var graph = store.GraphStore;

        await graph.UpsertNodeAsync(new GraphNode { Id = "doc1.md", Title = "向量与知识图谱协同", Type = "document" });
        await graph.UpsertNodeAsync(new GraphNode { Id = "tag:rag", Title = "rag", Type = "tag" });
        await graph.UpsertNodeAsync(new GraphNode { Id = "heading:设计方案", Title = "设计方案", Type = "heading" });
        await graph.UpsertEdgesAsync(new[]
        {
            new GraphEdge { FromId = "doc1.md", ToId = "tag:rag", Type = "tag", LineNumber = 1 },
            new GraphEdge { FromId = "doc1.md", ToId = "heading:设计方案", Type = "heading", LineNumber = 5 }
        });

        var searchResults = await graph.SearchNodesAsync("协同", 10);
        Assert.Single(searchResults);
        Assert.Equal("doc1.md", searchResults[0].Id);

        var stats = await graph.GetStatsAsync();
        Assert.Equal(3, stats.NodeCount);
        Assert.Equal(1, stats.DocCount);
        Assert.Equal(1, stats.TagCount);
        Assert.Equal(1, stats.HeadingCount);
        Assert.Equal(2, stats.EdgeCount);
    }

    [Fact]
    public async Task KnowledgeBases_PhysicalGraphIsolation_AreCompletelyIndependent()
    {
        var kb1Db = Path.Combine(_testDir, "kb1.db");
        var kb2Db = Path.Combine(_testDir, "kb2.db");

        using var store1 = new SqliteKnowledgeBaseStore("kb1", kb1Db, NullLogger<SqliteKnowledgeBaseStore>.Instance);
        using var store2 = new SqliteKnowledgeBaseStore("kb2", kb2Db, NullLogger<SqliteKnowledgeBaseStore>.Instance);

        await store1.InitializeAsync();
        await store2.InitializeAsync();

        await store1.GraphStore.UpsertNodeAsync(new GraphNode { Id = "doc1.md", Title = "KB1 Note", Type = "document" });
        await store2.GraphStore.UpsertNodeAsync(new GraphNode { Id = "doc2.md", Title = "KB2 Note", Type = "document" });

        var kb1Nodes = await store1.GraphStore.SearchNodesAsync("Note", 10);
        var kb2Nodes = await store2.GraphStore.SearchNodesAsync("Note", 10);

        Assert.Single(kb1Nodes);
        Assert.Equal("KB1 Note", kb1Nodes[0].Title);

        Assert.Single(kb2Nodes);
        Assert.Equal("KB2 Note", kb2Nodes[0].Title);

        Assert.Null(await store1.GraphStore.GetNodeAsync("doc2.md"));
        Assert.Null(await store2.GraphStore.GetNodeAsync("doc1.md"));
    }
}
