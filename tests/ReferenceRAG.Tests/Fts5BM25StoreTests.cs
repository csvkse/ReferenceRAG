using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// Fts5BM25Store 中文分词测试
/// </summary>
public class Fts5BM25StoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly Fts5BM25Store _store;

    public Fts5BM25StoreTests()
    {
        // 创建临时数据库
        _dbPath = Path.Combine(Path.GetTempPath(), $"fts5_test_{Guid.NewGuid():N}.db");
        _store = new Fts5BM25Store(_dbPath);
    }

    public void Dispose()
    {
        _store?.Dispose();
        // 等待文件释放
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (File.Exists(_dbPath))
        {
            try
            {
                File.Delete(_dbPath);
            }
            catch
            {
                // 忽略删除失败
            }
        }
    }

    [Fact]
    public async Task Search_WithChineseText_ReturnsResults()
    {
        // Arrange - 插入中文测试数据并建立索引
        var testDocs = new[]
        {
            ("chunk1", "这是一个中文搜索测试"),
            ("chunk2", "全文检索引擎支持中文"),
            ("chunk3", "数据库性能优化方案"),
            ("chunk4", "FTS5 full text search engine")
        };

        await _store.IndexBatchAsync(testDocs);

        // Act - 搜索中文关键词
        var results = await _store.SearchAsync("中文");

        // Assert
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.ChunkId == "chunk1" || r.ChunkId == "chunk2");
    }

    [Fact]
    public async Task Search_WithEnglishText_ReturnsResults()
    {
        // Arrange
        var testDocs = new[]
        {
            ("chunk1", "Hello world this is a test"),
            ("chunk2", "Full text search engine"),
            ("chunk3", "Database optimization techniques")
        };

        await _store.IndexBatchAsync(testDocs);

        // Act
        var results = await _store.SearchAsync("search");

        // Assert
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.ChunkId == "chunk2");
    }

    [Fact]
    public async Task Search_WithMixedChineseEnglish_ReturnsResults()
    {
        // Arrange
        var testDocs = new[]
        {
            ("chunk1", "Python编程语言入门教程"),
            ("chunk2", "使用FTS5实现全文检索"),
            ("chunk3", "Machine Learning机器学习基础"),
            ("chunk4", "Web开发最佳实践")
        };

        await _store.IndexBatchAsync(testDocs);

        // Act & Assert - 搜索中文
        var resultsChinese = await _store.SearchAsync("编程");
        Assert.NotEmpty(resultsChinese);

        // Act & Assert - 搜索英文
        var resultsEnglish = await _store.SearchAsync("FTS5");
        Assert.NotEmpty(resultsEnglish);
        Assert.Contains(resultsEnglish, r => r.ChunkId == "chunk2");
    }

    [Fact]
    public async Task Search_WithNoMatch_ReturnsEmptyList()
    {
        // Arrange
        await _store.IndexDocumentAsync("chunk1", "一些中文内容");

        // Act - 搜索不存在的内容
        var results = await _store.SearchAsync("不存在的关键词xyz");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_WithTopK_ReturnsCorrectNumberOfResults()
    {
        // Arrange - 插入多个包含相同关键词的文档
        var docs = new List<(string, string)>();
        for (int i = 0; i < 10; i++)
        {
            docs.Add(($"chunk{i}", $"测试文档编号{i}"));
        }
        await _store.IndexBatchAsync(docs);

        // Act
        var results = await _store.SearchAsync("测试", topK: 3);

        // Assert
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task Search_WithMultipleKeywords_ReturnsRelevantResults()
    {
        // Arrange
        var testDocs = new[]
        {
            ("chunk1", "Python是一种流行的编程语言"),
            ("chunk2", "Java也是一种编程语言"),
            ("chunk3", "编程语言有很多种"),
            ("chunk4", "机器学习是人工智能的分支")
        };

        await _store.IndexBatchAsync(testDocs);

        // Act - 搜索多个关键词
        var results = await _store.SearchAsync("编程 语言");

        // Assert - 应该找到包含"编程"或"语言"的文档
        Assert.NotEmpty(results);
        // 至少应该找到 chunk1, chunk2, chunk3（都包含"编程"或"语言"）
        Assert.True(results.Count >= 3);
    }

    [Fact]
    public async Task ClearIndexAsync_RemovesAllDocuments()
    {
        // Arrange
        await _store.IndexDocumentAsync("chunk1", "测试内容");

        // Act
        await _store.ClearIndexAsync();

        // Assert
        var stats = await _store.GetStatsAsync();
        Assert.Equal(0, stats.TotalDocuments);
    }

    [Fact]
    public async Task GetStatsAsync_ReturnsCorrectStatistics()
    {
        // Arrange
        var testDocs = new[]
        {
            ("chunk1", "第一个测试文档"),
            ("chunk2", "第二个测试文档"),
            ("chunk3", "第三个测试文档")
        };

        await _store.IndexBatchAsync(testDocs);

        // Act
        var stats = await _store.GetStatsAsync();

        // Assert
        Assert.Equal(3, stats.TotalDocuments);
        Assert.True(stats.VocabularySize > 0);
        Assert.True(stats.AverageDocLength > 0);
    }

    [Fact]
    public async Task IndexDocumentAsync_UpdatesExistingDocument()
    {
        // Arrange
        await _store.IndexDocumentAsync("chunk1", "原始内容");

        // Act - 更新同一文档
        await _store.IndexDocumentAsync("chunk1", "更新后的内容");

        // Assert
        var results = await _store.SearchAsync("更新后");
        Assert.NotEmpty(results);
        Assert.Single(results);
        Assert.Equal("chunk1", results[0].ChunkId);
    }
}
