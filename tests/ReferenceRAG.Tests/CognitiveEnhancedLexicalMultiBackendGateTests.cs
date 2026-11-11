using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Business.Features.Search.Adapters;
using ReferenceRAG.Business.Features.Search.Cognitive;
using ReferenceRAG.Business.Features.Search.StateMachine;
using ReferenceRAG.Business.Features.Search.Strategies;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.Cognitive;
using Xunit;

namespace ReferenceRAG.Tests;

public class CognitiveEnhancedLexicalMultiBackendGateTests
{
    [Fact]
    public async Task CrossEncoderAdapter_AugmentsQueryWithPriorEvidence_AndNormalizesLogits()
    {
        // Arrange
        var mockRerankAdapter = new Mock<IRerankExecutionAdapter>();
        mockRerankAdapter.SetupGet(r => r.IsAvailable).Returns(true);

        string? capturedQuery = null;
        mockRerankAdapter
            .Setup(r => r.RerankBatchAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, List<string>, CancellationToken>((q, docs, ct) => capturedQuery = q)
            .ReturnsAsync(new RerankResult
            {
                Documents = new List<RerankDocument>
                {
                    new() { Index = 0, RelevanceScore = 2.5 },   // Logit > 1 -> Sigmoid: ~0.924
                    new() { Index = 1, RelevanceScore = -1.5 }   // Logit < 0 -> Sigmoid: ~0.182
                }
            });

        var adapter = new CrossEncoderEvidenceAdapter(mockRerankAdapter.Object, threshold: 0.50f);

        var query = "韦小宝是谁？";
        var candidates = new List<CandidateEvaluationItem>
        {
            new("chunk-1", "鹿鼎记", "", "韦小宝是金庸武侠小说《鹿鼎记》的主人公。", 10.0),
            new("chunk-2", "射雕英雄传", "", "郭靖是《射雕英雄传》的主角。", 2.0)
        };
        var priorEvidence = new List<CandidateEvaluationItem>
        {
            new("chunk-0", "前序证据", "", "天地会总舵主陈近南收了一个关门弟子。", 8.0)
        };

        // Act
        var results = await adapter.EvaluateBatchAsync(query, candidates, priorEvidence, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedQuery);
        Assert.Contains("韦小宝是谁？", capturedQuery);
        Assert.Contains("[参考背景:", capturedQuery);
        Assert.Contains("天地会总舵主陈近南", capturedQuery);

        Assert.Equal(2, results.Count);
        var r1 = results["chunk-1"];
        Assert.True(r1.NormalizedScore > 0.85);
        Assert.True(r1.IsAccepted);

        var r2 = results["chunk-2"];
        Assert.True(r2.NormalizedScore < 0.30);
        Assert.False(r2.IsAccepted);
    }

    [Fact]
    public async Task JevSystemOneAdapter_PropagatesStateAndEvaluatesNoul()
    {
        // Arrange
        var mockJevClient = new Mock<ISystemOneClient>();
        mockJevClient.SetupGet(c => c.IsConfigured).Returns(true);

        mockJevClient
            .Setup(c => c.EvaluateAsync(It.IsAny<object>(), It.IsAny<IDictionary<string, SystemOneQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SystemOneResponse
            {
                Model = "jev-latest",
                Answers = new Dictionary<string, SystemOneAnswer>
                {
                    ["is_necessary_evidence"] = new SystemOneAnswer { Noul = 0.91 }
                }
            });

        var adapter = new JevSystemOneEvidenceAdapter(mockJevClient.Object, threshold: 0.60f);

        var candidates = new List<CandidateEvaluationItem>
        {
            new("jev-c1", "文档A", "", "关键事实证据内容", 8.5)
        };
        var priorEvidence = new List<CandidateEvaluationItem>
        {
            new("jev-p0", "前序文档", "", "前序已查出的线索事实", 7.0)
        };

        // Act
        var results = await adapter.EvaluateBatchAsync("测试查询", candidates, priorEvidence, CancellationToken.None);

        // Assert
        Assert.Single(results);
        var r = results["jev-c1"];
        Assert.Equal(0.91, r.NormalizedScore);
        Assert.True(r.IsAccepted);
        Assert.Contains("91.0%", r.Reason);
    }

    [Fact]
    public async Task StatisticalHeuristicAdapter_CalculatesContinuousScore_WithoutModel()
    {
        // Arrange
        var adapter = new StatisticalHeuristicEvidenceAdapter(threshold: 0.50f);

        var query = "天龙八部 萧峰 降龙十八掌";
        var candidates = new List<CandidateEvaluationItem>
        {
            new("stat-1", "天龙八部", "", "萧峰在杏子林展现了降龙十八掌的神威。", 9.0),
            new("stat-2", "天气预报", "", "今日北京晴天微风适宜出行。", 1.0)
        };
        var prior = new List<CandidateEvaluationItem>
        {
            new("stat-0", "前序", "", "丐帮前帮主乔峰（后名《萧峰》）身世曲折。", 8.0)
        };

        // Act
        var results = await adapter.EvaluateBatchAsync(query, candidates, prior, CancellationToken.None);

        // Assert
        Assert.Equal(2, results.Count);
        var hit1 = results["stat-1"];
        var hit2 = results["stat-2"];

        Assert.True(hit1.NormalizedScore > hit2.NormalizedScore);
        Assert.True(hit1.IsAccepted);
        Assert.False(hit2.IsAccepted);
    }

    [Fact]
    public void CognitiveFactory_ResolvesMatchingBackend_OrFallsBack()
    {
        // Arrange
        var mockRerank = new Mock<IRerankExecutionAdapter>();
        mockRerank.SetupGet(r => r.IsAvailable).Returns(true);

        var mockJev = new Mock<ISystemOneClient>();
        mockJev.SetupGet(j => j.IsConfigured).Returns(true);

        var factory = new CognitiveEvidenceEvaluatorFactory(mockRerank.Object, mockJev.Object);

        // Act & Assert
        // 1. Disabled
        var disabledEval = factory.GetEvaluator(new CognitiveLexicalConfig { Backend = CognitiveLexicalBackend.Disabled });
        Assert.Null(disabledEval);

        // 2. CrossEncoder
        var ceEval = factory.GetEvaluator(new CognitiveLexicalConfig { Backend = CognitiveLexicalBackend.CrossEncoder });
        Assert.NotNull(ceEval);
        Assert.Equal("CrossEncoder", ceEval.BackendName);

        // 3. Jev
        var jevEval = factory.GetEvaluator(new CognitiveLexicalConfig { Backend = CognitiveLexicalBackend.Jev });
        Assert.NotNull(jevEval);
        Assert.Equal("Jev", jevEval.BackendName);

        // 4. Statistical
        var statEval = factory.GetEvaluator(new CognitiveLexicalConfig { Backend = CognitiveLexicalBackend.Statistical });
        Assert.NotNull(statEval);
        Assert.Equal("Statistical", statEval.BackendName);

        // 5. Fallback: Jev unconfigured -> Falls back to Statistical when EnableFallback is true
        var unconfJevMock = new Mock<ISystemOneClient>();
        unconfJevMock.SetupGet(j => j.IsConfigured).Returns(false);
        var unconfFactory = new CognitiveEvidenceEvaluatorFactory(null, unconfJevMock.Object);
        var fallbackEval = unconfFactory.GetEvaluator(new CognitiveLexicalConfig { Backend = CognitiveLexicalBackend.Jev, EnableFallback = true });
        Assert.NotNull(fallbackEval);
        Assert.Equal("Statistical", fallbackEval.BackendName);
    }

    [Fact]
    public async Task CognitiveEnhancedLexicalStrategy_PreservesProtectedSeeds_AndEarlyExits()
    {
        // Arrange: Mock IKnowledgeBaseSearchService returning initial candidates
        var mockKbSearch = new Mock<IKnowledgeBaseSearchService>();
        var hop0Items = new List<RecallItem>
        {
            new("chunk-01", "doc-1", "novel/ludingji.md", "这对未婚夫妻从未见过面，忽然在公主闺房中相会。", 12.0, ScoreKind.BM25, 1),
            new("chunk-02", "doc-1", "novel/ludingji.md", "韦小宝心中暗暗好笑，看着眼前的建宁公主。", 10.0, ScoreKind.BM25, 1),
            new("chunk-03", "doc-2", "novel/shediao.md", "郭靖站在大漠之上练习降龙十八掌。", 8.0, ScoreKind.BM25, 1)
        };

        mockKbSearch
            .Setup(k => k.SearchAsync(It.IsAny<CrossKnowledgeBaseSearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CrossKnowledgeBaseSearchResponse(
                Query: "这对未婚夫妻从未见过面，忽然在公主闺房中相会",
                Items: hop0Items,
                SuccessfulKnowledgeBaseIds: new[] { "kb_main" },
                FailedKnowledgeBaseIds: Array.Empty<string>(),
                PartialSuccess: false,
                ScoreKind: ScoreKind.BM25,
                Elapsed: TimeSpan.FromMilliseconds(5)
            ));

        var mockCatalog = new Mock<ICatalogStore>();
        mockCatalog
            .Setup(c => c.ListKnowledgeBasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<KnowledgeBaseDefinition>
            {
                new("kb_main", "金庸武侠", "", true, 1L, 1L, "spec_default", DateTime.UtcNow, DateTime.UtcNow)
            });

        // Evaluator returns very high score for chunk-01 (>0.85 -> early exit), low for chunk-02 and chunk-03
        var mockEvaluator = new Mock<ICognitiveEvidenceEvaluator>();
        mockEvaluator.SetupGet(e => e.BackendName).Returns("MockCognitive");
        mockEvaluator.SetupGet(e => e.IsAvailable).Returns(true);
        mockEvaluator
            .Setup(e => e.EvaluateBatchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<CandidateEvaluationItem>>(), It.IsAny<IReadOnlyList<CandidateEvaluationItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, CognitiveScoreResult>
            {
                ["chunk-01"] = new("chunk-01", 0.95, true, "强事实匹配"),
                ["chunk-02"] = new("chunk-02", 0.30, false, "低分淘汰"),
                ["chunk-03"] = new("chunk-03", 0.20, false, "低分淘汰")
            });

        var config = new CognitiveLexicalConfig
        {
            Backend = CognitiveLexicalBackend.CrossEncoder,
            AcceptanceThreshold = 0.50f,
            EarlyExitThreshold = 0.85f,
            MaxHops = 2
        };

        var strategy = new CognitiveEnhancedLexicalStrategy(
            mockKbSearch.Object,
            mockCatalog.Object,
            mockEvaluator.Object,
            config);

        var sm = new SearchPipelineStateMachine(QueryMode.Lexical.ToString());
        var context = new SearchExecutionContext
        {
            Request = new AIQueryRequest
            {
                Query = "这对未婚夫妻从未见过面，忽然在公主闺房中相会",
                TopK = 3
            },
            StateMachine = sm,
            Config = new ObsidianRagConfig()
        };

        // Act
        var result = await strategy.ExecuteAsync(context, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.RerankApplied);
        Assert.Equal("Cognitive-MockCognitive", result.RerankStats?.ModelName);

        // Protected seeds check: Even though chunk-02 and chunk-03 were marked false by evaluator,
        // they were in Hop 0 Top 3, so they are protected with 0.50 floor score rather than zero recall!
        Assert.Equal(3, result.Results.Count);
        Assert.Equal("chunk-01", result.Results[0].ChunkId);
        Assert.Equal(0.95f, result.Results[0].Score, 2);

        // Verify state machine telemetry has cognitive phases
        var telemetry = sm.Complete(result.Results.Count);
        Assert.Contains(telemetry.Steps, s => s.Phase == SearchPipelinePhase.CognitiveScoringHop0.ToString());
        Assert.Contains(telemetry.Steps, s => s.Phase == SearchPipelinePhase.DppDiversitySelecting.ToString());
    }

    [Fact]
    public void EvaluatorFactory_WithJevConfigAndSocks5Proxy_ResolvesAvailableJevAdapter()
    {
        var config = new ObsidianRagConfig
        {
            Network = new NetworkConfig
            {
                Proxy = new ProxyConfig
                {
                    Enabled = true,
                    ProxyUrl = "socks5:127.0.0.1:1080"
                }
            },
            SystemOne = new SystemOneConfig
            {
                ApiKey = "ap_test_key_12345",
                BaseUrl = "https://api.typesafe.ai/v1/systemone",
                Model = "jev-latest",
                ProxyPolicy = ProxyPolicy.Inherit
            }
        };

        var factory = new CognitiveEvidenceEvaluatorFactory(
            rerankAdapterFactory: null,
            jevClientFactory: () =>
            {
                var proxy = ReferenceRAG.Infrastructure.Features.Network.DynamicWebProxyAdapter.ResolveProxy(
                    config,
                    config.SystemOne.ProxyPolicy,
                    config.SystemOne.CustomProxyUrl);
                return new JevSystemOneClient(
                    config.SystemOne.ApiKey,
                    config.SystemOne.BaseUrl,
                    config.SystemOne.Model,
                    proxy: proxy);
            });

        var evaluator = factory.GetEvaluator(new CognitiveLexicalConfig
        {
            Backend = CognitiveLexicalBackend.Jev,
            AcceptanceThreshold = 0.55f
        });

        Assert.NotNull(evaluator);
        Assert.Equal("Jev", evaluator.BackendName);
        Assert.True(evaluator.IsAvailable);
        Assert.True(evaluator.Capabilities.SupportsMultiHopContext);
        Assert.True(evaluator.Capabilities.RequiresNetwork);
    }
}
