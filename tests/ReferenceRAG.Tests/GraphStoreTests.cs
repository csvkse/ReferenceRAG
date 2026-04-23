using ReferenceRAG.Core.Models;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Tests;

public class GraphStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteGraphStore _store;

    public GraphStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"graph-test-{Guid.NewGuid():N}.db");
        _store = new SqliteGraphStore(_dbPath);
    }

    [Fact]
    public async Task UpsertNode_ThenGet_ReturnsNode()
    {
        var node = new GraphNode { Id = "Projects/foo.md", Title = "Foo", Type = "document" };
        await _store.UpsertNodeAsync(node);

        var retrieved = await _store.GetNodeAsync("Projects/foo.md");

        Assert.NotNull(retrieved);
        Assert.Equal("Foo", retrieved!.Title);
    }

    [Fact]
    public async Task UpsertNode_Twice_Updates()
    {
        await _store.UpsertNodeAsync(new GraphNode { Id = "a.md", Title = "Old" });
        await _store.UpsertNodeAsync(new GraphNode { Id = "a.md", Title = "New" });

        var node = await _store.GetNodeAsync("a.md");
        Assert.Equal("New", node!.Title);
    }

    [Fact]
    public async Task DeleteNode_RemovesNodeAndEdges()
    {
        await _store.UpsertNodeAsync(new GraphNode { Id = "a.md", Title = "A" });
        await _store.UpsertNodeAsync(new GraphNode { Id = "b.md", Title = "B" });
        await _store.UpsertEdgesAsync(new[] { new GraphEdge { FromId = "a.md", ToId = "b.md", Type = "wikilink" } });

        await _store.DeleteNodeAsync("a.md");

        Assert.Null(await _store.GetNodeAsync("a.md"));

        var result = await _store.GetNeighborsAsync("b.md", 1);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public async Task GetNeighbors_Depth1_ReturnsDirect()
    {
        await _store.UpsertNodeAsync(new GraphNode { Id = "root.md", Title = "Root" });
        await _store.UpsertNodeAsync(new GraphNode { Id = "child.md", Title = "Child" });
        await _store.UpsertEdgesAsync(new[]
        {
            new GraphEdge { FromId = "root.md", ToId = "child.md", Type = "wikilink" }
        });

        var result = await _store.GetNeighborsAsync("root.md", 1);

        Assert.Contains(result.Nodes, n => n.Id == "child.md");
        Assert.Single(result.Edges);
    }

    [Fact]
    public async Task GetNeighbors_Depth2_ReturnsTransitive()
    {
        await _store.UpsertNodeAsync(new GraphNode { Id = "a.md", Title = "A" });
        await _store.UpsertNodeAsync(new GraphNode { Id = "b.md", Title = "B" });
        await _store.UpsertNodeAsync(new GraphNode { Id = "c.md", Title = "C" });
        await _store.UpsertEdgesAsync(new[]
        {
            new GraphEdge { FromId = "a.md", ToId = "b.md", Type = "wikilink" },
            new GraphEdge { FromId = "b.md", ToId = "c.md", Type = "wikilink" }
        });

        var result = await _store.GetNeighborsAsync("a.md", 2);

        Assert.Contains(result.Nodes, n => n.Id == "c.md");
    }

    [Fact]
    public async Task SearchNodes_ByTitle_ReturnsMatch()
    {
        await _store.UpsertNodeAsync(new GraphNode { Id = "foo.md", Title = "Foo Bar" });
        await _store.UpsertNodeAsync(new GraphNode { Id = "baz.md", Title = "Baz Qux" });

        var results = await _store.SearchNodesAsync("Foo");

        Assert.Single(results);
        Assert.Equal("foo.md", results[0].Id);
    }

    [Fact]
    public async Task GetStats_ReturnsCorrectCounts()
    {
        await _store.UpsertNodeAsync(new GraphNode { Id = "n1.md", Title = "N1" });
        await _store.UpsertNodeAsync(new GraphNode { Id = "n2.md", Title = "N2" });
        await _store.UpsertEdgesAsync(new[]
        {
            new GraphEdge { FromId = "n1.md", ToId = "n2.md", Type = "wikilink" }
        });

        var stats = await _store.GetStatsAsync();

        Assert.Equal(2, stats.NodeCount);
        Assert.Equal(1, stats.EdgeCount);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
