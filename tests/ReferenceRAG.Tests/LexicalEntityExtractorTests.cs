using System;
using System.Collections.Generic;
using ReferenceRAG.Business.Features.Search.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class LexicalEntityExtractorTests
{
    [Fact]
    public void ExtractNamePhrases_ExtractsEnglishPhrasesAndChineseQuotedEntities()
    {
        var text = "According to J. R. Smith and the SiliconFlow API, 《九阳真经》 与 “倚天剑” 是重要内容。";
        var phrases = LexicalEntityExtractor.ExtractNamePhrases(text);

        Assert.Contains(phrases, p => p.Contains("SiliconFlow API") || p.Contains("Smith"));
        Assert.Contains("九阳真经", phrases);
        Assert.Contains("倚天剑", phrases);
    }

    [Fact]
    public void Interleave_CombinesCandidateListsInRoundRobinOrderWithoutDuplicates()
    {
        var listA = new List<string> { "doc1", "doc2", "doc3" };
        var listB = new List<string> { "doc2", "doc4", "doc5" };
        var listC = new List<string> { "doc6", "doc7" };

        var interleaved = LexicalEntityExtractor.Interleave(
            new[] { listA, listB, listC },
            id => id,
            limit: 5);

        // Round 0: doc1 (from A), doc2 (from B), doc6 (from C)
        // Round 1: doc2 (skipped from A), doc4 (from B), doc7 (from C)
        // Expected total 5: doc1, doc2, doc6, doc4, doc7
        Assert.Equal(5, interleaved.Count);
        Assert.Equal("doc1", interleaved[0]);
        Assert.Equal("doc2", interleaved[1]);
        Assert.Equal("doc6", interleaved[2]);
        Assert.Equal("doc4", interleaved[3]);
        Assert.Equal("doc7", interleaved[4]);
    }

    [Fact]
    public void BuildExpansionTerms_PrioritizesEntitiesAndExcludesQueryTerms()
    {
        var seeds = new List<(string Content, double Score)>
        {
            ("金庸先生在《射雕英雄传》中描写了全真教的武功秘籍。", 0.95),
            ("九阳真经与少林寺的武僧传承息息相关。", 0.8)
        };
        var queryTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "金庸" };

        var expansion = LexicalEntityExtractor.BuildExpansionTerms(seeds, queryTerms, limit: 3);

        Assert.NotEmpty(expansion);
        Assert.DoesNotContain("金庸", expansion);
        Assert.Contains("射雕英雄传", expansion);
    }
}
