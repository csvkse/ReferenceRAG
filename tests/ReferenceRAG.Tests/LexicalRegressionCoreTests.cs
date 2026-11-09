using System.Text.Json;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Tests;

public class LexicalRegressionCoreTests
{
    [Fact]
    public async Task P03_T1_ChineseAndShortWords_Segmentation()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p03-t1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var file = new FileRecord { Id = "f1", Path = "chinese.md", Source = "s1", Title = "知识图谱" };
            var chunk = new ChunkRecord
            {
                Id = "c1",
                FileId = "f1",
                Source = "s1",
                Content = "# 知识图谱索引\n\n关于知识图谱索引和图书馆系统的文档，单字 图 和单字母 C 语言开发。",
                StartLine = 1,
                EndLine = 3
            };
            await coordinator.StageDocumentsAsync(file, "v1", new[] { chunk });
            await coordinator.StageIndexAsync("f1", "v1");
            await coordinator.PublishAsync("f1", "v1");

            // Query full Chinese term
            var r1 = await coordinator.SearchAsync("知识图谱索引", new[] { "f1" }, 5);
            Assert.NotEmpty(r1);
            Assert.Equal("c1", r1[0].ChunkId);

            // Query sub-term "图书馆"
            var r2 = await coordinator.SearchAsync("图书馆", new[] { "f1" }, 5);
            Assert.NotEmpty(r2);

            // Query single char "图"
            var r3 = await coordinator.SearchAsync("图", new[] { "f1" }, 5);
            Assert.NotEmpty(r3);

            // Query single letter "C"
            var r4 = await coordinator.SearchAsync("C", new[] { "f1" }, 5);
            Assert.NotEmpty(r4);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task P03_T2_TechnicalWordsAndPath_Escaping()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p03-t2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var file = new FileRecord { Id = "f2", Path = @"docs\api\index.md", Source = "s1", Title = "API Doc" };
            var chunk = new ChunkRecord
            {
                Id = "c2",
                FileId = "f2",
                Source = "s1",
                Content = @"# API Index docs\api\index.md

Error code ERR-1042 occurred in C# and .NET 10 environment.",
                StartLine = 1,
                EndLine = 4
            };
            await coordinator.StageDocumentsAsync(file, "v1", new[] { chunk });
            await coordinator.StageIndexAsync("f2", "v1");
            await coordinator.PublishAsync("f2", "v1");

            // Technical words and path with backslashes
            var r1 = await coordinator.SearchAsync("ERR-1042", new[] { "f2" }, 5);
            Assert.NotEmpty(r1);

            var r2 = await coordinator.SearchAsync(@"docs\api\index.md", new[] { "f2" }, 5);
            Assert.NotEmpty(r2);

            var r3 = await coordinator.SearchAsync("C# .NET", new[] { "f2" }, 5);
            Assert.NotEmpty(r3);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task P03_T3_NegationQuotesAndSqlSpecialChars_SafeHandling()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p03-t3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));
            var file = new FileRecord { Id = "f3", Path = "special.md", Source = "s1" };
            var chunk = new ChunkRecord
            {
                Id = "c3",
                FileId = "f3",
                Source = "s1",
                Content = "alpha beta gamma delta test injection"
            };
            await coordinator.StageDocumentsAsync(file, "v1", new[] { chunk });
            await coordinator.StageIndexAsync("f3", "v1");
            await coordinator.PublishAsync("f3", "v1");

            // FTS operators: NOT, quotes, unclosed quotes, SQL injection patterns
            var r1 = await coordinator.SearchAsync("alpha NOT beta", new[] { "f3" }, 5);
            Assert.NotEmpty(r1);

            var r2 = await coordinator.SearchAsync("\"alpha beta\"", new[] { "f3" }, 5);
            Assert.NotEmpty(r2);

            var r3 = await coordinator.SearchAsync("\"unclosed quote alpha", new[] { "f3" }, 5);
            Assert.NotEmpty(r3);

            var r4 = await coordinator.SearchAsync("' OR 1=1 --", new[] { "f3" }, 5);
            // Safe SQL escaping, does not throw exception

            // Pure punctuation returns empty without throwing exception
            var r5 = await coordinator.SearchAsync("???!!!::;,,", new[] { "f3" }, 5);
            Assert.Empty(r5);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task P03_T4_QueryLengthAndTermLimits()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p03-t4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await using var coordinator = new LexicalIndexCoordinator(Path.Combine(dir, "vectors.db"), Path.Combine(dir, "bm25.db"));

            // Valid length <= 4096 characters succeeds
            var validQuery = new string('a', 4096);
            await coordinator.SearchAsync(validQuery, Array.Empty<string>(), 5);

            // Query > 4096 characters throws ArgumentOutOfRangeException
            var tooLongQuery = new string('a', 4097);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.SearchAsync(tooLongQuery, Array.Empty<string>(), 5));

            // TopK limits: < 1 throws, > 1000 throws
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.SearchAsync("test", Array.Empty<string>(), 0));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.SearchAsync("test", Array.Empty<string>(), 1001));

            // Term limits: 65 terms with sentinel at term 64 and term 65
            var terms = Enumerable.Range(1, 65).Select(i => $"term{i:D3}").ToArray();
            var queryWith65Terms = string.Join(" ", terms);

            var file = new FileRecord { Id = "f4", Path = "terms.md", Source = "s1" };
            var chunk = new ChunkRecord
            {
                Id = "c4",
                FileId = "f4",
                Source = "s1",
                Content = "term064 is indexed here."
            };
            await coordinator.StageDocumentsAsync(file, "v1", new[] { chunk });
            await coordinator.StageIndexAsync("f4", "v1");
            await coordinator.PublishAsync("f4", "v1");

            // term 64 is within top 64 terms, should hit
            var hit = await coordinator.SearchAsync(queryWith65Terms, new[] { "f4" }, 5);
            Assert.NotEmpty(hit);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void P03_C1_ConfigManager_DefaultsAndRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"p03-c1-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var configFile = Path.Combine(dir, "appsettings.json");

        try
        {
            // Empty JSON config defaults
            File.WriteAllText(configFile, "{}");
            var manager = new ConfigManager(customConfigPath: configFile);
            var loaded = manager.Load();

            Assert.True(loaded.Indexing.EmbeddingEnabled);
            Assert.True(loaded.Search.EnableLexical);
            Assert.Equal(QueryMode.HybridRerank, loaded.Search.ChatQueryMode);

            // Save explicit lexical settings
            loaded.Indexing.EmbeddingEnabled = false;
            loaded.Search.EnableLexical = true;
            loaded.Search.ChatQueryMode = QueryMode.Lexical;
            manager.Save(loaded);

            // Reload and verify
            var manager2 = new ConfigManager(customConfigPath: configFile);
            var reloaded = manager2.Load();
            Assert.False(reloaded.Indexing.EmbeddingEnabled);
            Assert.True(reloaded.Search.EnableLexical);
            Assert.Equal(QueryMode.Lexical, reloaded.Search.ChatQueryMode);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void P03_C2_QueryModeEnum_Mapping()
    {
        Assert.Equal(0, (int)QueryMode.Quick);
        Assert.Equal(1, (int)QueryMode.Standard);
        Assert.Equal(2, (int)QueryMode.Deep);
        Assert.Equal(3, (int)QueryMode.Hybrid);
        Assert.Equal(4, (int)QueryMode.HybridRerank);
        Assert.Equal(5, (int)QueryMode.Lexical);

        var json = JsonSerializer.Serialize(new { mode = QueryMode.Lexical });
        Assert.Contains("Lexical", json);
    }
}
