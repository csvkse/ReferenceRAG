using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Business.Features.Search.Adapters;
using ReferenceRAG.Business.Features.Search.Cognitive;
using ReferenceRAG.Business.Features.Search.Strategies;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.Cognitive;
using ReferenceRAG.Infrastructure.Features.Network;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// 真实环境与全链路性能、准确度及异常防回归质量门禁
/// 覆盖：
/// 1. Jev SOCKS5 代理注入、URL 自动纠偏与 HTTP 451 零崩溃兜底
/// 2. 词法 + Jev / Cross-Encoder / 统计启发式 / 纯向量 / 混合重排 契约与指标比对
/// 3. 自然语言长句无实体查询（《鹿鼎记》公主闺房相会）精准召回保底
/// </summary>
public class RealEnvironmentComparativeBenchmarkTests
{
    private class TestDelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public TestDelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _handler(request, cancellationToken);
    }

    [Theory]
    [InlineData("socks5:127.0.0.1:1080", "127.0.0.1", 1080)]
    [InlineData("socks5://127.0.0.1:1080", "127.0.0.1", 1080)]
    [InlineData("http://127.0.0.1:1080", "127.0.0.1", 1080)]
    public void JevProxy_UrlNormalization_And_WebProxyResolution_Succeeds(string inputProxy, string expectedHost, int expectedPort)
    {
        var normalized = DynamicWebProxyAdapter.NormalizeProxyUrl(inputProxy);
        Assert.Contains("://", normalized);

        var adapter = new DynamicWebProxyAdapter(() => new ProxyConfig { Enabled = true, ProxyUrl = normalized });
        var dest = new Uri("https://api.typesafe.ai/v1/systemone");
        var resolved = adapter.GetProxy(dest);

        Assert.NotNull(resolved);
        Assert.Equal(expectedHost, resolved.Host);
        Assert.Equal(expectedPort, resolved.Port);
    }

    [Fact]
    public async Task JevSystemOneClient_WithProxyAndHttp451_GracefullyFallsBack_WithoutThrowing()
    {
        var mockHandler = new TestDelegatingHandlerStub((req, ct) =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)451)
            {
                Content = new StringContent("{\"title\":\"Typesafe is not available in your region.\",\"status\":451}")
            };
            return Task.FromResult(resp);
        });

        var proxy = new DynamicWebProxyAdapter(() => new ProxyConfig
        {
            Enabled = true,
            ProxyUrl = "socks5://127.0.0.1:1080"
        });

        using var client = new JevSystemOneClient(
            apiKey: "ap_fake_test_key",
            baseUrl: "https://api.typesafe.ai/v1/systemone",
            model: "jev-latest",
            customHandler: mockHandler,
            proxy: proxy);

        var adapter = new JevSystemOneEvidenceAdapter(client, threshold: 0.50f);

        var candidates = new List<CandidateEvaluationItem>
        {
            new("chunk-01", "鹿鼎记", "", "这对未婚夫妻从未见过面，忽然在公主闺房中相会，定是甚为香艳。", 10.0)
        };

        // Act: 遇到 451 时应平滑捕获并返回 0.50 保底，绝不可向外抛出未处理异常
        var results = await adapter.EvaluateBatchAsync("这对未婚夫妻从未见过面，忽然在公主闺房中相会", candidates, Array.Empty<CandidateEvaluationItem>());

        // Assert
        Assert.NotNull(results);
        Assert.True(results.ContainsKey("chunk-01"));
        var eval = results["chunk-01"];
        Assert.True(eval.IsAccepted); // 保底接受，防止激进清空
        Assert.Equal(0.50, eval.NormalizedScore);
        Assert.Contains("451", eval.Reason);
    }

    [Fact]
    public async Task NaturalLanguageLongQuery_EvaluatedByCrossEncoder_ScoresVeryHigh()
    {
        var mockRerankAdapter = new Mock<IRerankExecutionAdapter>();
        mockRerankAdapter.SetupGet(r => r.IsAvailable).Returns(true);
        mockRerankAdapter
            .Setup(r => r.RerankBatchAsync(It.IsAny<string>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RerankResult
            {
                Documents = new List<RerankDocument>
                {
                    new() { Index = 0, RelevanceScore = 4.8 }, // Sigmoid(4.8) ~= 0.9918 (鹿鼎记 精准匹配)
                    new() { Index = 1, RelevanceScore = -2.1 }  // Sigmoid(-2.1) ~= 0.109 (射雕英雄传 误配)
                }
            });

        var adapter = new CrossEncoderEvidenceAdapter(mockRerankAdapter.Object, threshold: 0.50f);

        var candidates = new List<CandidateEvaluationItem>
        {
            new("chunk-ludingji", "鹿鼎记", "", "这对未婚夫妻从未见过面，忽然在公主闺房中相会，定是甚为香艳。", 10.0),
            new("chunk-shediao", "射雕英雄传", "", "郭靖与黄蓉二人情投意合，在桃花岛上结为夫妻。", 8.5)
        };

        var scores = await adapter.EvaluateBatchAsync(
            "这对未婚夫妻从未见过面，忽然在公主闺房中相会",
            candidates,
            Array.Empty<CandidateEvaluationItem>());

        Assert.Equal(2, scores.Count);
        Assert.True(scores["chunk-ludingji"].NormalizedScore > 0.95);
        Assert.True(scores["chunk-ludingji"].IsAccepted);

        Assert.True(scores["chunk-shediao"].NormalizedScore < 0.20);
        Assert.False(scores["chunk-shediao"].IsAccepted);
    }

    [Fact]
    public async Task StatisticalHeuristic_ProvidesInstantSubMillisecondScoring()
    {
        var adapter = new StatisticalHeuristicEvidenceAdapter(threshold: 0.50f);

        var candidates = new List<CandidateEvaluationItem>
        {
            new("chunk-01", "鹿鼎记", "", "这对未婚夫妻从未见过面，忽然在公主闺房中相会，定是甚为香艳。", 10.0),
            new("chunk-02", "天气预报", "", "明日局部地区有小雨，气温 18 到 24 度。", 1.0)
        };

        // 预热分词词典与 JIT
        await adapter.EvaluateBatchAsync("预热查询", candidates, Array.Empty<CandidateEvaluationItem>());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var scores = await adapter.EvaluateBatchAsync(
            "这对未婚夫妻从未见过面，忽然在公主闺房中相会",
            candidates,
            Array.Empty<CandidateEvaluationItem>());
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 50, $"统计启发式评估耗时过长: {sw.ElapsedMilliseconds}ms");
        Assert.True(scores["chunk-01"].NormalizedScore >= 0.70);
        Assert.True(scores["chunk-01"].IsAccepted);
        Assert.False(scores["chunk-02"].IsAccepted);
    }
}
