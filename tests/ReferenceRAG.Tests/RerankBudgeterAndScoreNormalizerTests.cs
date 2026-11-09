using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services.Rerank;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Rerank;
using Xunit;

namespace ReferenceRAG.Tests;

public class RerankBudgeterAndScoreNormalizerTests
{
    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public void ScoreNormalizer_PreservesSigmoidScores_AndTransformsLogits()
    {
        var normalizer = ScoreNormalizer.Instance;

        // 1. 原生 Sigmoid 概率 [0, 1] 保持不变
        Assert.Equal(0.95, normalizer.Normalize(0.95));
        Assert.Equal(0.001, normalizer.Normalize(0.001));
        Assert.Equal(0.0, normalizer.Normalize(0.0));
        Assert.Equal(1.0, normalizer.Normalize(1.0));

        // 2. 超出 [0, 1] 的原始对数几率 (Logits) 转换为 (0, 1) 的概率
        // Sigmoid(5.0) ~ 0.9933
        var posLogit = normalizer.Normalize(5.0);
        Assert.True(posLogit > 0.99 && posLogit < 1.0);
        // Sigmoid(-5.0) ~ 0.0067
        var negLogit = normalizer.Normalize(-5.0);
        Assert.True(negLogit > 0.0 && negLogit < 0.01);

        // 3. NaN 和 Infinity 防御
        Assert.Equal(0.0, normalizer.Normalize(double.NaN));
        Assert.Equal(0.0, normalizer.Normalize(double.PositiveInfinity));
    }

    [Fact]
    public void RerankTokenBudgeter_TruncatesOversizedDocs_AndPartitionsBatches()
    {
        var budgeter = new RerankTokenBudgeter(maxDocChars: 100, maxBatchSize: 10);

        var longDoc = new string('A', 250);
        var docs = new List<string> { longDoc };
        for (int i = 1; i < 25; i++)
        {
            docs.Add($"Doc {i}");
        }

        var partitions = budgeter.Partition(docs);

        // 25篇文档，按每批10篇切分为 3 批：10, 10, 5
        Assert.Equal(3, partitions.Count);
        Assert.Equal(10, partitions[0].Documents.Count);
        Assert.Equal(10, partitions[1].Documents.Count);
        Assert.Equal(5, partitions[2].Documents.Count);

        // 首篇超长文档被安全截断
        Assert.True(partitions[0].Documents[0].Length <= 103);
        Assert.EndsWith("...", partitions[0].Documents[0]);

        // 索引映射完整且保真
        Assert.Equal(0, partitions[0].OriginalIndices[0]);
        Assert.Equal(10, partitions[1].OriginalIndices[0]);
        Assert.Equal(24, partitions[2].OriginalIndices[4]);
    }

    [Fact]
    public async Task OpenAIRerankService_RerankBatchAsync_ResilientFallback_WhenHttpFails()
    {
        var handler = new MockHttpMessageHandler(req =>
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Server Error", Encoding.UTF8, "text/plain")
            };
        });

        var config = new RerankConfig
        {
            ApiBaseUrl = "https://api.siliconflow.cn/v1",
            ApiKey = "test_key",
            ModelName = "BAAI/bge-reranker-v2-m3"
        };

        using var service = new OpenAIRerankService(config, customHandler: handler);

        var docs = new[] { "Doc A", "Doc B", "Doc C" };
        var result = await service.RerankBatchAsync("test query", docs);

        // 异常触发弹性降级，保证检索调用方不崩溃，并按原顺序赋予降序分值
        Assert.NotNull(result);
        Assert.Equal(3, result.Documents.Count);
        Assert.Equal("Doc A", result.Documents[0].Document);
        Assert.True(result.Documents[0].RelevanceScore > result.Documents[1].RelevanceScore);
        Assert.True(result.Documents[1].RelevanceScore > result.Documents[2].RelevanceScore);
    }
}
