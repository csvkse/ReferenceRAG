using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.Storage.Services;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// 金庸测试数据集与 Golden QA 基准评测测试
/// 验证编码适配器、Markdown 分块、BM25 词法检索召回率及评测数据集完整性
/// </summary>
public class JinYongGoldenQaEvaluationTests
{
    private static string GetSolutionRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "ReferenceRAG.slnx")) || Directory.Exists(Path.Combine(current, "env", "data-prepared")))
            {
                return current;
            }
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            current = parent;
        }
        return Directory.GetCurrentDirectory();
    }

    public class GoldenQaItem
    {
        public string Id { get; set; } = string.Empty;
        public string Tier { get; set; } = string.Empty;
        public string TargetNovel { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Question { get; set; } = string.Empty;
        public string ExpectedAnswer { get; set; } = string.Empty;
        public List<string> MustContainKeywords { get; set; } = new();
        public List<string> NegativeKeywords { get; set; } = new();
    }

    [Fact]
    public void GoldenQaFixture_ValidJsonAndFormat()
    {
        var root = GetSolutionRoot();
        var fixturePath = Path.Combine(root, "tests", "fixtures", "jinyong-golden-qa.json");
        Assert.True(File.Exists(fixturePath), $"Golden QA fixture not found at {fixturePath}");

        var json = File.ReadAllText(fixturePath);
        var items = JsonSerializer.Deserialize<List<GoldenQaItem>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(items);
        Assert.NotEmpty(items);
        Assert.All(items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id));
            Assert.False(string.IsNullOrWhiteSpace(item.Question));
            Assert.False(string.IsNullOrWhiteSpace(item.ExpectedAnswer));
            Assert.NotEmpty(item.MustContainKeywords);
        });
    }

    [Fact]
    public async Task SmokeCorpus_SafeReadAndChunk_PreservesTextIntegrity()
    {
        var root = GetSolutionRoot();
        var smokePath = Path.Combine(root, "env", "data-prepared", "smoke", "越女剑.md");
        if (!File.Exists(smokePath))
        {
            // Fallback to raw data if prepared data not present in current working dir
            smokePath = Path.Combine(root, "env", "data", "越女剑.TXT");
        }

        Assert.True(File.Exists(smokePath), $"Corpus file not found: {smokePath}");

        var content = await TextEncodingAdapter.SafeReadFileAsync(smokePath);

        // Verify no replacement characters
        Assert.DoesNotContain("\uFFFD", content);
        Assert.Contains("阿青", content);
        Assert.Contains("范蠡", content);
        Assert.Contains("西施", content);

        Assert.NotNull(content);
        var chunker = new MarkdownChunker();
        var chunks = chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 500,
            MinTokens = 50,
            PreserveHeadings = true,
            OverlapTokens = 30
        });

        Assert.True(chunks.Count > 10, $"Expected > 10 chunks, got {chunks.Count}");
        Assert.Contains(chunks, c => c.Content.Contains("白公公") || c.Content.Contains("白猿"));
    }

    [Fact]
    public async Task SmokeTier_BM25LexicalRetrieval_AchievesFullRecallOnGoldenQa()
    {
        var root = GetSolutionRoot();
        var smokePath = Path.Combine(root, "env", "data-prepared", "smoke", "越女剑.md");
        if (!File.Exists(smokePath))
        {
            smokePath = Path.Combine(root, "env", "data", "越女剑.TXT");
        }
        Assert.True(File.Exists(smokePath), $"Corpus file not found: {smokePath}");

        var content = await TextEncodingAdapter.SafeReadFileAsync(smokePath);
        Assert.NotNull(content);
        var chunker = new MarkdownChunker();
        var chunks = chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 500,
            MinTokens = 50,
            PreserveHeadings = true,
            OverlapTokens = 50
        });

        var searcher = new BM25Searcher();
        for (int i = 0; i < chunks.Count; i++)
        {
            searcher.IndexDocument($"chunk_{i}", chunks[i].Content);
        }

        var fixturePath = Path.Combine(root, "tests", "fixtures", "jinyong-golden-qa.json");
        var json = File.ReadAllText(fixturePath);
        var goldenItems = JsonSerializer.Deserialize<List<GoldenQaItem>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;

        var smokeQuestions = goldenItems.Where(q => q.Tier == "Smoke").ToList();
        Assert.NotEmpty(smokeQuestions);

        foreach (var qa in smokeQuestions)
        {
            // Search using keywords and question terms
            var query = $"{string.Join(" ", qa.MustContainKeywords)} {qa.Question}";
            var results = searcher.Search(query, topK: 5);

            Assert.NotEmpty(results);

            // Verify at least one retrieved chunk satisfies the golden keywords
            var anyMatched = results.Any(r => qa.MustContainKeywords.Any(kw => r.Content.Contains(kw)));
            Assert.True(anyMatched, $"QA '{qa.Id}' ({qa.Question}) failed to recall any chunks containing keywords [{string.Join(", ", qa.MustContainKeywords)}]");
        }
    }

    [Fact]
    public async Task CoreTier_SnowFox_SafeReadAndRecall_Succeeds()
    {
        var root = GetSolutionRoot();
        var corePath = Path.Combine(root, "env", "data-prepared", "core", "雪山飞狐.md");
        if (!File.Exists(corePath))
        {
            return; // Skip if core not locally prepared
        }

        var content = await TextEncodingAdapter.SafeReadFileAsync(corePath);
        Assert.DoesNotContain("\uFFFD", content);
        Assert.Contains("胡斐", content);
        Assert.Contains("胡一刀", content);
        Assert.Contains("苗人凤", content);

        Assert.NotNull(content);
        var chunker = new MarkdownChunker();
        var chunks = chunker.Chunk(content, new ChunkingOptions
        {
            MaxTokens = 500,
            MinTokens = 50,
            PreserveHeadings = true
        });

        var searcher = new BM25Searcher();
        for (int i = 0; i < chunks.Count; i++)
        {
            searcher.IndexDocument($"core_chunk_{i}", chunks[i].Content);
        }

        // Test search for Hu Yidao and Miao Renfeng
        var results = searcher.Search("胡一刀 苗人凤 决战 毒", topK: 3);
        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Content.Contains("胡一刀") && r.Content.Contains("苗人凤"));
    }
}
