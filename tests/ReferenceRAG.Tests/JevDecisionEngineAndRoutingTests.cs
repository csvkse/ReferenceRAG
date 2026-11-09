using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Moq;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Infrastructure.Features.Cognitive;
using Xunit;

namespace ReferenceRAG.Tests;

public class JevDecisionEngineAndRoutingTests
{
    [Fact]
    public async Task JevDecisionEngine_ClassifyIntentAsync_RAGQuery_IdentifiesCorrectly()
    {
        var mockClient = new Mock<ISystemOneClient>();
        mockClient.Setup(c => c.IsConfigured).Returns(true);
        mockClient.Setup(c => c.EvaluateAsync(
            It.IsAny<object>(),
            It.IsAny<IDictionary<string, SystemOneQuestion>>(),
            It.IsAny<CancellationToken>()))
        .ReturnsAsync(new SystemOneResponse
        {
            Model = "jev-1.14.0",
            Answers = new Dictionary<string, SystemOneAnswer>
            {
                ["intent"] = new SystemOneAnswer { Choice = "rag_search", Confidence = 0.95 },
                ["requires_retrieval"] = new SystemOneAnswer { Noul = 0.88 }
            }
        });

        var engine = new JevDecisionEngine(mockClient.Object, enabled: true, retrievalThreshold: 0.65f);

        var result = await engine.ClassifyIntentAsync("请帮我查一下知识库架构设计规范");

        Assert.Equal("rag_search", result.PrimaryIntent);
        Assert.True(result.RequiresRetrieval);
        Assert.Equal(0.88, result.RetrievalProbability);
        Assert.Equal(0.95, result.Confidence);
    }

    [Fact]
    public async Task JevSystemOneRoutingStrategy_MatchesSearchFunction_WhenRetrievalRequired()
    {
        var mockEngine = new Mock<IJevDecisionEngine>();
        mockEngine.Setup(e => e.IsEnabled).Returns(true);
        mockEngine.Setup(e => e.ClassifyIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentClassificationResult(
                PrimaryIntent: "rag_search",
                RequiresRetrieval: true,
                RetrievalProbability: 0.9,
                Confidence: 0.95,
                Rationale: "RAG query detected"));

        var strategy = new JevSystemOneRoutingStrategy(mockEngine.Object);

        var searchFunc = AIFunctionFactory.Create((string query) => "result", "SearchKnowledgeBase", "Search docs");
        var tools = new List<AITool> { searchFunc };

        var decision = await strategy.RouteAsync("如何配置 BGE-M3 嵌入？", tools);

        Assert.True(decision.Handled);
        Assert.Equal("SearchKnowledgeBase", decision.FunctionName);
        Assert.Equal("JevSystemOne", decision.StrategyName);
        Assert.Equal(95, decision.ConfidenceScore);
        Assert.Equal("如何配置 BGE-M3 嵌入？", decision.Arguments?["query"]);
    }

    [Fact]
    public async Task JevSystemOneRoutingStrategy_ShortCircuitsToUnhandledChitChat_WhenNoRetrievalRequired()
    {
        var mockEngine = new Mock<IJevDecisionEngine>();
        mockEngine.Setup(e => e.IsEnabled).Returns(true);
        mockEngine.Setup(e => e.ClassifyIntentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntentClassificationResult(
                PrimaryIntent: "chit_chat",
                RequiresRetrieval: false,
                RetrievalProbability: 0.05,
                Confidence: 0.99,
                Rationale: "Greetings"));

        var strategy = new JevSystemOneRoutingStrategy(mockEngine.Object);
        var searchFunc = AIFunctionFactory.Create((string query) => "result", "SearchKnowledgeBase", "Search docs");
        var tools = new List<AITool> { searchFunc };

        var decision = await strategy.RouteAsync("你好呀！今天过得怎么样？", tools);

        Assert.False(decision.Handled);
        Assert.Equal("JevSystemOne", decision.StrategyName);
        Assert.Contains("闲聊模式", decision.Reason);
    }

    [Fact]
    public async Task JevDecisionEngine_PruneCandidateChunksAsync_FiltersIrrelevant()
    {
        var mockClient = new Mock<ISystemOneClient>();
        mockClient.Setup(c => c.IsConfigured).Returns(true);

        mockClient.Setup(c => c.EvaluateAsync(
            It.IsAny<object>(),
            It.IsAny<IDictionary<string, SystemOneQuestion>>(),
            It.IsAny<CancellationToken>()))
        .Returns<object, IDictionary<string, SystemOneQuestion>, CancellationToken>((state, q, ct) =>
        {
            var json = JsonSerializer.Serialize(state);
            bool isRelevant = json.Contains("sqlite-vec");

            return Task.FromResult(new SystemOneResponse
            {
                Model = "jev-1.14.0",
                Answers = new Dictionary<string, SystemOneAnswer>
                {
                    ["is_relevant"] = new SystemOneAnswer { Noul = isRelevant ? 0.95 : 0.05 },
                    ["relevance_level"] = new SystemOneAnswer { Score = isRelevant ? 2.0 : 0.0 }
                }
            });
        });

        var engine = new JevDecisionEngine(mockClient.Object, enabled: true);

        var chunks = new[]
        {
            "SQLite 中使用 sqlite-vec 扩展创建 1024 维度的向量索引虚拟表。",
            "制作披萨时，需要将饼皮烤箱预热到 220 度烘烤 15 分钟。"
        };

        var decisions = await engine.PruneCandidateChunksAsync("向量索引", chunks);

        Assert.Equal(2, decisions.Count);
        Assert.True(decisions[0].IsRelevant);
        Assert.False(decisions[1].IsRelevant);
    }
}
