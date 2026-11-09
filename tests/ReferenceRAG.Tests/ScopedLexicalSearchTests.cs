using Microsoft.Data.Sqlite;
using ReferenceRAG.Storage;

namespace ReferenceRAG.Tests;

public class ScopedLexicalSearchTests
{
    [Fact]
    public async Task ScopeIsAppliedBeforeTopKAndEmptyScopeReturnsNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scoped-fts-{Guid.NewGuid():N}.db");
        using var store = new Fts5BM25Store(path);
        try
        {
            var docs = Enumerable.Range(0, 40).Select(i => ($"outside-{i}", "database database database"))
                .Append(("allowed", "database configuration"));
            await store.IndexBatchAsync(docs);
            var result = await store.SearchScopedAsync("database", new[] { "allowed" }, 1);
            Assert.Equal("allowed", Assert.Single(result).ChunkId);
            Assert.Empty(await store.SearchScopedAsync("database", Array.Empty<string>(), 1));
            Assert.Empty(await store.SearchScopedAsync("\" OR *", new[] { "allowed" }, 1));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.SearchScopedAsync("database", new[] { "allowed" }, 1, cts.Token));
            Assert.Equal("allowed", Assert.Single(await store.SearchScopedAsync("database", new[] { "allowed" }, 1)).ChunkId);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                store.SearchScopedAsync(new string('a', 4097), new[] { "allowed" }, 1));
        }
        finally
        {
            store.Dispose();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ConnectionString);
            SqliteConnection.ClearPool(connection);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
