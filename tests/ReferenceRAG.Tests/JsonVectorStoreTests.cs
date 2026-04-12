using ReferenceRAG.Storage;
using ReferenceRAG.Core.Models;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// JsonVectorStore 单元测试
/// </summary>
public class JsonVectorStoreTests : IAsyncLifetime
{
    private readonly JsonVectorStore _store;
    private readonly string _testDataPath;

    public JsonVectorStoreTests()
    {
        _testDataPath = Path.Combine(Path.GetTempPath(), $"obsidian-test-{Guid.NewGuid()}");
        _store = new JsonVectorStore(_testDataPath);
    }

    public async Task InitializeAsync()
    {
        // 确保测试目录存在
        Directory.CreateDirectory(_testDataPath);
    }

    public async Task DisposeAsync()
    {
        // 清理测试数据
        if (Directory.Exists(_testDataPath))
        {
            Directory.Delete(_testDataPath, true);
        }
    }

    [Fact]
    public async Task UpsertFileAsync_AndGetFileAsync_Works()
    {
        // Arrange
        var file = new FileRecord
        {
            Id = "file-1",
            Path = "test.md",
            Title = "Test File",
            ContentHash = "hash123"
        };

        // Act
        await _store.UpsertFileAsync(file);
        var retrieved = await _store.GetFileAsync("file-1");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("test.md", retrieved.Path);
        Assert.Equal("Test File", retrieved.Title);
    }

    [Fact]
    public async Task GetFileByPathAsync_ReturnsCorrectFile()
    {
        // Arrange
        var file = new FileRecord
        {
            Id = "file-2",
            Path = "path/to/file.md",
            Title = "Nested File"
        };
        await _store.UpsertFileAsync(file);

        // Act
        var retrieved = await _store.GetFileByPathAsync("path/to/file.md");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("file-2", retrieved.Id);
    }

    [Fact]
    public async Task DeleteFileAsync_RemovesFileAndRelatedData()
    {
        // Arrange
        var file = new FileRecord { Id = "file-3", Path = "delete.md" };
        var chunk = new ChunkRecord { Id = "chunk-3", FileId = "file-3", Content = "content" };
        var vector = new VectorRecord { Id = "vec-3", ChunkId = "chunk-3", Vector = new float[] { 0.1f, 0.2f } };

        await _store.UpsertFileAsync(file);
        await _store.UpsertChunkAsync(chunk);
        await _store.UpsertVectorAsync(vector);

        // Act
        await _store.DeleteFileAsync("file-3");

        // Assert
        var deletedFile = await _store.GetFileAsync("file-3");
        Assert.Null(deletedFile);
    }

    [Fact]
    public async Task UpsertChunkAsync_AndGetChunkAsync_Works()
    {
        // Arrange
        var chunk = new ChunkRecord
        {
            Id = "chunk-1",
            FileId = "file-1",
            Content = "Test content",
            StartLine = 1,
            EndLine = 5,
            TokenCount = 10
        };

        // Act
        await _store.UpsertChunkAsync(chunk);
        var retrieved = await _store.GetChunkAsync("chunk-1");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("Test content", retrieved.Content);
        Assert.Equal(1, retrieved.StartLine);
        Assert.Equal(5, retrieved.EndLine);
    }

    [Fact]
    public async Task UpsertVectorAsync_AndGetVectorAsync_Works()
    {
        // Arrange
        var vector = new VectorRecord
        {
            Id = "vec-1",
            ChunkId = "chunk-1",
            Vector = new float[] { 0.1f, 0.2f, 0.3f },
            ModelName = "test-model"
        };

        // Act
        await _store.UpsertVectorAsync(vector);
        var retrieved = await _store.GetVectorAsync("vec-1");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("chunk-1", retrieved.ChunkId);
        Assert.Equal(3, retrieved.Vector.Length);
    }

    [Fact]
    public async Task SearchAsync_ReturnsTopResults()
    {
        // Arrange
        var file = new FileRecord { Id = "file-s", Path = "search.md", Title = "Search Test" };
        var chunk1 = new ChunkRecord { Id = "chunk-s1", FileId = "file-s", Content = "Content 1" };
        var chunk2 = new ChunkRecord { Id = "chunk-s2", FileId = "file-s", Content = "Content 2" };
        var vec1 = new VectorRecord { Id = "vec-s1", ChunkId = "chunk-s1", Vector = new float[] { 1f, 0f, 0f } };
        var vec2 = new VectorRecord { Id = "vec-s2", ChunkId = "chunk-s2", Vector = new float[] { 0.9f, 0.1f, 0f } };

        await _store.UpsertFileAsync(file);
        await _store.UpsertChunkAsync(chunk1);
        await _store.UpsertChunkAsync(chunk2);
        await _store.UpsertVectorAsync(vec1);
        await _store.UpsertVectorAsync(vec2);

        // Act - 搜索与 [1, 0, 0] 相似的向量
        var queryVector = new float[] { 1f, 0f, 0f };
        var results = await _store.SearchAsync(queryVector, topK: 10);

        // Assert
        Assert.NotEmpty(results);
        Assert.True(results.First().Score > 0.9f);
    }

    [Fact]
    public async Task SearchByAggregateTypeAsync_FiltersCorrectly()
    {
        // Arrange
        var file = new FileRecord { Id = "file-a", Path = "aggregate.md" };
        var chunkDoc = new ChunkRecord 
        { 
            Id = "chunk-doc", 
            FileId = "file-a", 
            Content = "Doc content",
            AggregateType = AggregateType.Document 
        };
        var chunkSection = new ChunkRecord 
        { 
            Id = "chunk-section", 
            FileId = "file-a", 
            Content = "Section content",
            AggregateType = AggregateType.Section 
        };
        var vecDoc = new VectorRecord { Id = "vec-doc", ChunkId = "chunk-doc", Vector = new float[] { 1f, 0f } };
        var vecSection = new VectorRecord { Id = "vec-section", ChunkId = "chunk-section", Vector = new float[] { 0.8f, 0.2f } };

        await _store.UpsertFileAsync(file);
        await _store.UpsertChunkAsync(chunkDoc);
        await _store.UpsertChunkAsync(chunkSection);
        await _store.UpsertVectorAsync(vecDoc);
        await _store.UpsertVectorAsync(vecSection);

        // Act
        var queryVector = new float[] { 1f, 0f };
        var results = await _store.SearchByAggregateTypeAsync(queryVector, AggregateType.Document, 10);

        // Assert
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(AggregateType.Document, r.AggregateType));
    }
}
