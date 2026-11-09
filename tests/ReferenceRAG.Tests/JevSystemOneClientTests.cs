using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReferenceRAG.Infrastructure.Features.Cognitive;
using Xunit;

namespace ReferenceRAG.Tests;

public class JevSystemOneClientTests
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
    public async Task EvaluateAsync_ParsesChoiceNoulScoreCorrectly()
    {
        var mockResponseJson = @"
{
  ""model"": ""jev-1.14.0"",
  ""answers"": {
    ""intent"": {
      ""type"": ""choice"",
      ""choice"": ""rag_search"",
      ""confidence"": 0.98,
      ""probabilities"": { ""rag_search"": 0.98, ""chit_chat"": 0.02 }
    },
    ""requires_retrieval"": {
      ""type"": ""noul"",
      ""noul"": 0.91
    },
    ""technical_depth"": {
      ""type"": ""score"",
      ""score"": 1.85,
      ""level"": ""Deep architecture""
    }
  },
  ""usage"": {
    ""input_tokens"": 120,
    ""output_tokens"": 45
  }
}";

        var handler = new MockHttpMessageHandler(req =>
        {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal("test_key", req.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockResponseJson, Encoding.UTF8, "application/json")
            };
        });

        using var client = new JevSystemOneClient("test_key", "https://api.typesafe.ai/v1/systemone", "jev-latest", handler);

        var questions = new Dictionary<string, SystemOneQuestion>
        {
            ["intent"] = SystemOneQuestion.Choice("Intent?", new Dictionary<string, string> { ["rag_search"] = "Search", ["chit_chat"] = "Chat" }),
            ["requires_retrieval"] = SystemOneQuestion.Noul("Needs retrieval?"),
            ["technical_depth"] = SystemOneQuestion.Score("Depth?", new[] { "Low", "Medium", "High" })
        };

        var response = await client.EvaluateAsync("What is the architecture?", questions);

        Assert.Equal("jev-1.14.0", response.Model);
        Assert.Equal(3, response.Answers.Count);

        var intentAnswer = response.Answers["intent"];
        Assert.Equal("rag_search", intentAnswer.Choice);
        Assert.Equal(0.98, intentAnswer.Confidence);
        Assert.Equal(0.98, intentAnswer.Probabilities?["rag_search"]);

        var noulAnswer = response.Answers["requires_retrieval"];
        Assert.Equal(0.91, noulAnswer.Noul);

        var scoreAnswer = response.Answers["technical_depth"];
        Assert.Equal(1.85, scoreAnswer.Score);
        Assert.Equal("Deep architecture", scoreAnswer.Level);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task EvaluateAsync_RetriesOn429_AndSucceeds()
    {
        int attempts = 0;
        var successJson = @"{
  ""model"": ""jev-1.14.0"",
  ""answers"": {
    ""check"": { ""type"": ""noul"", ""noul"": 1.0 }
  }
}";

        var handler = new MockHttpMessageHandler(req =>
        {
            attempts++;
            if (attempts == 1)
            {
                return new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("{\"detail\":\"rate limit\"}", Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(successJson, Encoding.UTF8, "application/json")
            };
        });

        using var client = new JevSystemOneClient("test_key", "https://api.typesafe.ai/v1/systemone", "jev-latest", handler);

        var response = await client.EvaluateAsync("test text", new Dictionary<string, SystemOneQuestion>
        {
            ["check"] = SystemOneQuestion.Noul("check")
        });

        Assert.Equal(2, attempts);
        Assert.Equal(1.0, response.Answers["check"].Noul);
    }

    [Fact]
    public async Task EvaluateAsync_ThrowsWhenUnconfigured()
    {
        using var client = new JevSystemOneClient(null);
        Assert.False(client.IsConfigured);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.EvaluateAsync("text", new Dictionary<string, SystemOneQuestion>
            {
                ["q"] = SystemOneQuestion.Noul("q")
            }));
    }
}
