using System;
using System.Collections.Generic;
using System.Linq;
using ReferenceRAG.Business.Features.Search.Compaction;
using ReferenceRAG.Business.Features.Search.Strategies.ContextFormat;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.Search.Adapters;
using Xunit;

namespace ReferenceRAG.Tests;

public class SearchResultAdapterAndContextStrategyTests
{
    [Fact]
    public void RecallItemAdapter_ShouldPreserveLineNumbersAndMetadata()
    {
        var recall = new RecallItem(
            ChunkId: "chunk_abc123",
            DocumentId: "doc_7254d",
            SourceKey: "旧版/天龙八部（旧版）.md",
            Text: "虚竹更是奇怪，道：“是你的小妹子？”",
            Score: 0.95,
            ScoreKind: ScoreKind.BM25,
            DocumentVersion: 1,
            ConnectorId: "connector_tlbb",
            Locator: "manual://connector_tlbb/doc_7254d",
            StartLine: 120,
            EndLine: 155,
            HeadingPath: "第一百零八章 鹫宫新主"
        );

        var searchResult = SearchResultAdapter.FromRecallItem(recall, 9.8f);

        Assert.Equal("chunk_abc123", searchResult.ChunkId);
        Assert.Equal("doc_7254d", searchResult.FileId);
        Assert.Equal("旧版/天龙八部（旧版）.md", searchResult.FilePath);
        Assert.Equal("connector_tlbb", searchResult.Source);
        Assert.Equal("天龙八部（旧版）", searchResult.Title);
        Assert.Equal(120, searchResult.StartLine);
        Assert.Equal(155, searchResult.EndLine);
        Assert.Equal("第一百零八章 鹫宫新主", searchResult.HeadingPath);
        Assert.Equal("manual://connector_tlbb/doc_7254d", searchResult.Locator);
        Assert.Equal(9.8f, searchResult.Score);

        var chunkResult = SearchResultAdapter.ToChunkResult(searchResult);
        Assert.Equal("L120-L155", chunkResult.LineRange);
        Assert.Equal("[[天龙八部（旧版）#L120-L155]]", chunkResult.ObsidianLink);
    }

    [Theory]
    [InlineData(0, 0, "飞狐外传.md", "[[飞狐外传]]")]
    [InlineData(-1, -1, "三联版/倚天屠龙记.md", "[[倚天屠龙记]]")]
    [InlineData(42, 42, "鹿鼎记.md", "[[鹿鼎记#L42]]")]
    [InlineData(10, 25, "旧版/天龙八部（旧版）.md", "[[天龙八部（旧版）#L10-L25]]")]
    public void FormatObsidianLink_NeverGeneratesL0Defect(int start, int end, string path, string expectedLink)
    {
        var link = SearchResultAdapter.FormatObsidianLink(path, start, end);
        Assert.Equal(expectedLink, link);
        Assert.DoesNotContain("#L0", link);
        Assert.DoesNotContain("#L0-L0", link);
    }

    [Fact]
    public void StandardMarkdownContextStrategy_ShouldIncludePathLinesAndCitation()
    {
        var strategy = new StandardMarkdownContextStrategy();
        var chunks = new List<ChunkResult>
        {
            new()
            {
                RefId = "c1",
                FilePath = "旧版/天龙八部（旧版）.md",
                Title = "天龙八部（旧版）",
                StartLine = 120,
                EndLine = 155,
                HeadingPath = "第一百零八章 鹫宫新主",
                Content = "虚竹更是奇怪，道：“是你的小妹子？”",
                ObsidianLink = "[[天龙八部（旧版）#L120-L155]]"
            }
        };

        var context = strategy.FormatContext(chunks, maxTokens: 2000);
        Assert.Contains("### [1] 《天龙八部（旧版）》", context);
        Assert.Contains("- 文件: 旧版/天龙八部（旧版）.md", context);
        Assert.Contains("- 范围: 第 120-155 行", context);
        Assert.Contains("- 章节: 第一百零八章 鹫宫新主", context);
        Assert.Contains("来源: [[天龙八部（旧版）#L120-L155]]", context);

        var prompt = strategy.FormatPrompt("小妹子是谁", context);
        Assert.Contains("小妹子是谁", prompt);
        Assert.Contains("请根据上述内容提供准确、详细的回答，并在回答中引用来源（包括文件名和行号）。", prompt);
    }

    [Fact]
    public void StructuredXmlContextStrategy_ShouldRenderWellFormedXmlTags()
    {
        var strategy = new StructuredXmlContextStrategy();
        var chunks = new List<ChunkResult>
        {
            new()
            {
                RefId = "c1",
                FilePath = "三联版/飞狐外传.md",
                Title = "飞狐外传",
                StartLine = 40,
                EndLine = 60,
                HeadingPath = "药王谷",
                Content = "程灵素道：“两位若是相信新师父的话……”"
            }
        };

        var context = strategy.FormatContext(chunks, maxTokens: 2000);
        Assert.StartsWith("<context>", context.Trim());
        Assert.EndsWith("</context>", context.Trim());
        Assert.Contains("<chunk id=\"1\" file=\"三联版/飞狐外传.md\" title=\"飞狐外传\" start_line=\"40\" end_line=\"60\" heading=\"药王谷\">", context);
        Assert.Contains("程灵素道", context);
    }

    [Fact]
    public void CompactAgentContextStrategy_ShouldRenderHighDensityTokens()
    {
        var strategy = new CompactAgentContextStrategy();
        var chunks = new List<ChunkResult>
        {
            new()
            {
                RefId = "c1",
                FilePath = "三联版/倚天屠龙记.md",
                Title = "倚天屠龙记",
                StartLine = 12,
                EndLine = 35,
                Content = "张翠山一愕：“二哥摆空城什么？”"
            }
        };

        var context = strategy.FormatContext(chunks, maxTokens: 2000);
        Assert.Contains("[1] 三联版/倚天屠龙记.md L12-L35:", context);
        Assert.Contains("张翠山一愕", context);
    }

    [Fact]
    public void AdjacentChunkCompactor_ShouldMergeConsecutiveChunksFromSameFile()
    {
        var chunks = new List<ChunkResult>
        {
            new()
            {
                RefId = "c1",
                FileId = "doc_file1",
                FilePath = "金庸/天龙八部.md",
                Title = "天龙八部",
                StartLine = 10,
                EndLine = 30,
                Score = 0.9f,
                Content = "第一段文本内容。"
            },
            new()
            {
                RefId = "c2",
                FileId = "doc_file1",
                FilePath = "金庸/天龙八部.md",
                Title = "天龙八部",
                StartLine = 31,
                EndLine = 55,
                Score = 0.85f,
                Content = "第二段接续文本内容。"
            },
            new()
            {
                RefId = "c3",
                FileId = "doc_file2",
                FilePath = "金庸/笑傲江湖.md",
                Title = "笑傲江湖",
                StartLine = 1,
                EndLine = 20,
                Score = 0.7f,
                Content = "笑傲江湖正文。"
            }
        };

        var compacted = AdjacentChunkCompactor.CompactAdjacentChunks(chunks, maxGapLines: 2);

        Assert.Equal(2, compacted.Count);

        var merged = compacted.First(c => c.FileId == "doc_file1");
        Assert.Equal(10, merged.StartLine);
        Assert.Equal(55, merged.EndLine);
        Assert.Contains("第一段文本内容。", merged.Content);
        Assert.Contains("第二段接续文本内容。", merged.Content);
        Assert.Equal(0.9f, merged.Score);
        Assert.Equal("L10-L55", merged.LineRange);

        var other = compacted.First(c => c.FileId == "doc_file2");
        Assert.Equal(1, other.StartLine);
        Assert.Equal(20, other.EndLine);
    }

    [Fact]
    public void ContextFormatStrategyFactory_FallbackToStandard()
    {
        var s1 = ContextFormatStrategyFactory.GetStrategy(ContextFormat.Standard);
        Assert.IsType<StandardMarkdownContextStrategy>(s1);

        var s2 = ContextFormatStrategyFactory.GetStrategy(ContextFormat.Obsidian);
        Assert.IsType<ObsidianWikiLinkContextStrategy>(s2);

        var s3 = ContextFormatStrategyFactory.GetStrategy((ContextFormat)999);
        Assert.IsType<StandardMarkdownContextStrategy>(s3);
    }
}
