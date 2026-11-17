using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Infrastructure.Features.Search.Fusion;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Service.McpTools;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G-M3 / G-M4 / G-M5 / G-M6：参数适配器、重排分数语义、召回通道、入参边界。
///
/// 背景（生产实测）：同一 <c>knowledgeBaseIds</c> 在 5 个工具上语义不同；
/// 重排降级时对外发布 <c>0.85 - i*step</c> 伪分数；纯词法库上的「语义检索」
/// 与 <c>lexical_search</c> 结果完全相同且无任何通道标志；0/-1 被静默夹取。
/// </summary>
public sealed class McpInputAndSemanticsGateTests
{
    // ---------- G-M3 单一 ID 解析 ----------

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("[]", 0)]
    [InlineData("null", 0)]
    [InlineData("[\"kb_a\"]", 1)]
    [InlineData("[\"kb_a\",\"kb_b\"]", 2)]
    [InlineData("kb_a", 1)]
    [InlineData("kb_a, kb_b", 2)]
    public void KnowledgeBaseIds_ParseConsistently(string? raw, int expected)
    {
        Assert.Equal(expected, McpInputAdapters.ParseKnowledgeBaseIds(raw).Count);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("[\"ok\", ")]
    [InlineData("[\"a\",1]")]
    public void KnowledgeBaseIds_RejectInvalidJson(string raw)
    {
        var ex = Assert.Throws<McpInputArgumentException>(
            () => McpInputAdapters.ParseKnowledgeBaseIds(raw));
        // 不得泄漏序列化器的 Path/LineNumber 内部文本。
        Assert.DoesNotContain("LineNumber", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("knowledgeBaseIds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KnowledgeBaseIds_SingleTokenIsTreatedAsOneId()
    {
        // 单值（无逗号、非数组）是合法写法：模型常只传一个库 ID。
        Assert.Single(McpInputAdapters.ParseKnowledgeBaseIds("40745ffb28444648"));
    }

    [Fact]
    public void StringArray_ParsesAndRejectsWithoutLeakingSerializerInternals()
    {
        Assert.Equal(2, McpInputAdapters.ParseStringArray("[\"a\",\"b\"]", "texts").Count);
        var ex = Assert.Throws<McpInputArgumentException>(
            () => McpInputAdapters.ParseStringArray("not-json", "documents"));
        Assert.DoesNotContain("LineNumber", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------- G-M6 数值边界 ----------

    [Fact]
    public void IntBoundaries_RejectBelowMinAndClampAboveMax()
    {
        var (req, eff) = McpInputAdapters.ClampInt("topK", 900, 1, 50);
        Assert.Equal((900, 50), (req, eff));

        Assert.Throws<McpInputArgumentException>(() => McpInputAdapters.ClampInt("limit", 0, 1, 200));
        Assert.Throws<McpInputArgumentException>(() => McpInputAdapters.ClampInt("limit", -1, 1, 200));
    }

    [Fact]
    public void TextAndItemLimits_AreEnforcedAtEntry()
    {
        McpInputAdapters.EnsureTextLength("text", new string('x', McpInputAdapters.MaxEmbedChars));
        Assert.Throws<McpInputArgumentException>(
            () => McpInputAdapters.EnsureTextLength("text", new string('x', McpInputAdapters.MaxEmbedChars + 1)));

        McpInputAdapters.EnsureItemCount("texts", McpInputAdapters.MaxEmbedBatchItems, McpInputAdapters.MaxEmbedBatchItems);
        Assert.Throws<McpInputArgumentException>(
            () => McpInputAdapters.EnsureItemCount("texts", McpInputAdapters.MaxEmbedBatchItems + 1, McpInputAdapters.MaxEmbedBatchItems));
    }

    // ---------- G-M4 重排分数语义 ----------

    [Fact]
    public void DegradedRerank_NeverPublishesFabricatedScores()
    {
        var docs = new[] { "完全无关", "MCP 协议", "天气" };
        var fallback = ReferenceRAG.Core.Features.ModelManagement.StateMachine
            .RerankExecutionContextStateMachine.GenerateFallbackDocuments(docs);

        Assert.All(fallback, d =>
        {
            Assert.Null(d.RelevanceScore);
            Assert.Equal(RerankScoreKind.Degraded, d.ScoreKind);
        });

        // 顺序按原始 index 保留，且不得出现旧公式的 0.85/0.733/0.617 序列。
        Assert.Equal(new[] { 0, 1, 2 }, fallback.Select(d => d.Index).ToArray());
    }

    [Fact]
    public void DegradedRerankResult_ExposesReasonAndKind()
    {
        var result = new RerankResult
        {
            Query = "q",
            Degraded = true,
            DegradationReason = "GGUF 缺 Rank 头",
            Documents = { new RerankDocument { Index = 0, Document = "d", RelevanceScore = null, ScoreKind = RerankScoreKind.Degraded } }
        };

        Assert.Equal(RerankScoreKind.Degraded, result.ScoreKind);
        Assert.False(string.IsNullOrWhiteSpace(result.DegradationReason));
    }

    // ---------- G-M5 召回通道 ----------

    [Fact]
    public void FusionPreservesChannelFacts()
    {
        var snapshots = new[]
        {
            new RecallSnapshot(
                "kb1", 1, 1, 1,
                new List<RecallItem>
                {
                    new("c1", "d1", "k1", "text", 0.9, ScoreKind.BM25, 1, Channels: RecallChannel.Lexical),
                },
                Degraded: false),
            new RecallSnapshot(
                "kb2", 1, 1, 1,
                new List<RecallItem>
                {
                    new("c1", "d2", "k2", "text2", 0.8, ScoreKind.Cosine, 1, Channels: RecallChannel.Vector),
                },
                Degraded: false)
        };

        var fused = new RrfSearchFusionStrategy(k: 60, query: "q").Fuse(snapshots, topK: 5);

        Assert.NotEmpty(fused);
        Assert.All(fused, i => Assert.Equal(ScoreKind.RRF, i.ScoreKind));
        Assert.All(fused, i => Assert.True(i.Channels != RecallChannel.None,
            "融合后必须保留通道事实，否则无法区分向量命中与关键词命中"));
    }

    [Fact]
    public void FusionUnionsChannelsForSameChunk()
    {
        // 同一 chunk 同时被词法与向量命中：ZScore 融合必须把两路通道取并集。
        var lexical = new List<RecallItem>
        {
            new("c1", "d1", "k1", "text", 0.9, ScoreKind.BM25, 1, Channels: RecallChannel.Lexical),
            new("c2", "d2", "k2", "text2", 0.5, ScoreKind.BM25, 1, Channels: RecallChannel.Lexical)
        };
        var vector = new List<RecallItem>
        {
            new("c1", "d1", "k1", "text", 0.8, ScoreKind.Cosine, 1, Channels: RecallChannel.Vector)
        };

        var fused = AdaptiveQueryAwareFusion.FuseWithZScore(lexical, vector, "q", topK: 5);

        var merged = fused.Single(i => i.ChunkId == "c1");
        Assert.Equal(RecallChannel.Lexical | RecallChannel.Vector, merged.Channels);
        var lexicalOnly = fused.Single(i => i.ChunkId == "c2");
        Assert.Equal(RecallChannel.Lexical, lexicalOnly.Channels);
    }

    // ---------- 无结果信号 ----------

    [Theory]
    [InlineData("zzzzqqqxxx 完全不存在的词", false, true)]
    [InlineData("E1001 错误码", false, true)]
    [InlineData("MCP 协议", false, false)]
    [InlineData("模型上下文协议", false, false)]
    public void QueryMatchSignals_DetectMisses(string query, bool usedRecallFloor, bool expectNoMatch)
    {
        var items = new List<RecallItem>
        {
            new("c1", "d1", "k1", "模型上下文协议（MCP）代理通过它通信", 0.1, ScoreKind.RRF, 1),
            new("c2", "d2", "k2", "今天天气很好", 0.09, ScoreKind.RRF, 1)
        };

        var (noMatch, reason) = QueryMatchSignals.Evaluate(query, items, usedRecallFloor);

        Assert.Equal(expectNoMatch, noMatch);
        if (expectNoMatch) Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void QueryMatchSignals_EmptyResultsIsNoMatch()
    {
        var (noMatch, reason) = QueryMatchSignals.Evaluate("MCP", Array.Empty<RecallItem>(), usedRecallFloor: false);
        Assert.True(noMatch);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
