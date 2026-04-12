using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// BM25Searcher 单元测试
/// </summary>
public class BM25SearcherTests
{
    private readonly BM25Searcher _searcher;

    public BM25SearcherTests()
    {
        _searcher = new BM25Searcher();
    }

    [Fact]
    public void IndexDocument_WithValidContent_StoresDocument()
    {
        // Arrange
        var docId = "doc1";
        var content = "Hello world this is a test document";

        // Act
        _searcher.IndexDocument(docId, content);
        var results = _searcher.Search("Hello");

        // Assert
        Assert.Single(results);
        Assert.Equal(docId, results[0].DocId);
        Assert.Equal(content, results[0].Content);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public void IndexDocument_WithDuplicateId_UpdatesDocument()
    {
        // Arrange
        var docId = "doc1";
        var oldContent = "Old content about apples";
        var newContent = "New content about oranges";

        // Act
        _searcher.IndexDocument(docId, oldContent);
        _searcher.IndexDocument(docId, newContent);

        // Assert - search for "oranges" should find the document
        var results = _searcher.Search("oranges");
        Assert.Single(results);
        Assert.Equal(docId, results[0].DocId);
        Assert.Equal(newContent, results[0].Content);

        // Assert - search for "apples" should not find anything
        var oldResults = _searcher.Search("apples");
        Assert.Empty(oldResults);
    }

    [Fact]
    public void RemoveDocument_WithExistingId_RemovesFromIndex()
    {
        // Arrange
        var docId = "doc1";
        var content = "Document to be removed";
        _searcher.IndexDocument(docId, content);

        // Act
        _searcher.RemoveDocument(docId);
        var results = _searcher.Search("Document");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void Search_WithExactMatch_ReturnsHighScore()
    {
        // Arrange
        _searcher.IndexDocument("doc1", "The quick brown fox jumps over the lazy dog");
        _searcher.IndexDocument("doc2", "A completely different text about cats");

        // Act
        var results = _searcher.Search("fox");

        // Assert
        Assert.Single(results);
        Assert.Equal("doc1", results[0].DocId);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public void Search_WithNoMatch_ReturnsEmptyList()
    {
        // Arrange
        _searcher.IndexDocument("doc1", "Hello world");
        _searcher.IndexDocument("doc2", "Goodbye world");

        // Act
        var results = _searcher.Search("nonexistent");

        // Assert
        Assert.Empty(results);
    }

    [Fact]
    public void Search_WithChineseText_ReturnsResults()
    {
        // Arrange
        _searcher.IndexDocument("doc1", "这是一段中文测试文本");
        _searcher.IndexDocument("doc2", "另一个文档");

        // Act
        var results = _searcher.Search("测试");

        // Assert
        Assert.Single(results);
        Assert.Equal("doc1", results[0].DocId);
        Assert.Contains("测试", results[0].Content);
    }

    [Fact]
    public void Search_WithStopWords_FiltersStopWords()
    {
        // Arrange
        _searcher.IndexDocument("doc1", "The cat sits on the mat");

        // Act - "the" is a stop word and should be filtered
        var results = _searcher.Search("the");

        // Assert - stop word query should return empty
        Assert.Empty(results);

        // Act - normal word should work
        var results2 = _searcher.Search("cat");
        Assert.Single(results2);
    }

    [Fact]
    public void Search_WithEmptyQuery_ReturnsEmptyList()
    {
        // Arrange
        _searcher.IndexDocument("doc1", "Some content");

        // Act
        var results = _searcher.Search("");

        // Assert
        Assert.Empty(results);
    }

    [Theory]
    [InlineData("Hello 世界")]
    [InlineData("Python编程语言")]
    [InlineData("test 测试 case")]
    public void Tokenize_WithMixedChineseEnglish_HandlesCorrectly(string text)
    {
        // Arrange - create a searcher with no stop words for this test
        var options = new BM25Options
        {
            StopWords = new HashSet<string>() // Empty stop words
        };
        var searcher = new BM25Searcher(options);

        // Act - index and search to trigger tokenization
        searcher.IndexDocument("doc1", text);
        var results = searcher.Search(text);

        // Assert - document should be found with exact match
        Assert.Single(results);
        Assert.Equal("doc1", results[0].DocId);
    }

    [Fact]
    public void Clear_RemovesAllDocuments()
    {
        // Arrange
        _searcher.IndexDocument("doc1", "First document");
        _searcher.IndexDocument("doc2", "Second document");
        _searcher.IndexDocument("doc3", "Third document");

        // Act
        _searcher.Clear();

        // Assert - all searches should return empty
        var results1 = _searcher.Search("First");
        var results2 = _searcher.Search("Second");
        var results3 = _searcher.Search("Third");

        Assert.Empty(results1);
        Assert.Empty(results2);
        Assert.Empty(results3);
    }

    [Fact]
    public void Search_WithTopK_ReturnsCorrectNumberOfResults()
    {
        // Arrange
        for (int i = 0; i < 10; i++)
        {
            _searcher.IndexDocument($"doc{i}", $"Document number {i} with some common words");
        }

        // Act
        var results = _searcher.Search("Document", topK: 3);

        // Assert
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Search_WithMultipleDocuments_RanksByRelevance()
    {
        // Arrange
        _searcher.IndexDocument("doc1", "apple apple apple apple apple"); // 5 occurrences
        _searcher.IndexDocument("doc2", "apple apple"); // 2 occurrences
        _searcher.IndexDocument("doc3", "apple"); // 1 occurrence

        // Act
        var results = _searcher.Search("apple");

        // Assert - should be ranked by score (highest first)
        Assert.Equal(3, results.Count);
        Assert.Equal("doc1", results[0].DocId);
        Assert.Equal("doc2", results[1].DocId);
        Assert.Equal("doc3", results[2].DocId);
    }

    [Fact]
    public void IndexDocuments_WithBatch_StoresAllDocuments()
    {
        // Arrange
        var documents = new[]
        {
            ("doc1", "First document content"),
            ("doc2", "Second document content"),
            ("doc3", "Third document content")
        };

        // Act
        _searcher.IndexDocuments(documents);

        // Assert
        var results1 = _searcher.Search("First");
        var results2 = _searcher.Search("Second");
        var results3 = _searcher.Search("Third");

        Assert.Single(results1);
        Assert.Single(results2);
        Assert.Single(results3);
    }

    [Theory]
    [InlineData("   ", 0)] // whitespace only
    [InlineData("\t\n", 0)] // tab and newline
    [InlineData("test", 1)] // single word
    [InlineData("test test test", 1)] // repeated word (distinct terms)
    public void Search_WithVariousQueries_HandlesCorrectly(string query, int expectedCount)
    {
        // Arrange
        _searcher.IndexDocument("doc1", "test document");

        // Act
        var results = _searcher.Search(query);

        // Assert
        if (expectedCount == 0)
        {
            Assert.Empty(results);
        }
        else
        {
            Assert.Equal(expectedCount, results.Count);
        }
    }
}
