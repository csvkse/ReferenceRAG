using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Storage;
using Microsoft.Data.Sqlite;

namespace ReferenceRAG.Tests;

public class DocumentStoreBatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetChunksByIds_ReturnsOriginalChunksForLargeAndDuplicateInput(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"document-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        var store = sqlite
            ? (IVectorStore)new SqliteVectorStore(Path.Combine(path, "vectors.db"))
            : new JsonVectorStore(path);
        try
        {
            await store.UpsertFileAsync(new FileRecord { Id = "file", Path = "file.md" });
            await store.UpsertChunksAsync(new[]
            {
                new ChunkRecord { Id = "a", FileId = "file", Content = "原始正文", StartLine = 7 },
                new ChunkRecord { Id = "b", FileId = "file", Content = "second", StartLine = 12 }
            });
            var ids = Enumerable.Range(0, 1100).Select(i => $"missing-{i}")
                .Concat(new[] { "a", "b", "a" }).ToArray();

            var result = await ((IDocumentStore)store).GetChunksByIdsAsync(ids);

            Assert.Equal(2, result.Count);
            Assert.Equal("原始正文", result["a"].Content);
            Assert.Equal(7, result["a"].StartLine);
            Assert.Equal("second", result["b"].Content);
            Assert.Empty(await ((IDocumentStore)store).GetChunksByIdsAsync(Array.Empty<string>()));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ((IDocumentStore)store).GetChunksByIdsAsync(new[] { "a" }, cts.Token));
        }
        finally
        {
            (store as IDisposable)?.Dispose();
            if (sqlite)
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(path, "vectors.db"),
                    Mode = SqliteOpenMode.ReadWriteCreate
                }.ConnectionString);
                SqliteConnection.ClearPool(connection);
            }
            Directory.Delete(path, true);
        }
    }
}
