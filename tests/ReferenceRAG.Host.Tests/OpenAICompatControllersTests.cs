using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class OpenAICompatControllersTests
{
    private class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private static (MafChatService chatService, Mock<IEmbeddingService> embeddingMock, Mock<IRerankService> rerankMock) CreateTestServices(
        string? apiKey = "sk-test-key",
        string model = "gpt-4o-mini",
        string responseText = "Hello from ReferenceRAG!")
    {
        var embeddingMock = new Mock<IEmbeddingService>();
        embeddingMock.Setup(e => e.ModelName).Returns("bge-small-zh-v1.5");

        var rerankMock = new Mock<IRerankService>();
        rerankMock.Setup(r => r.ModelName).Returns("bge-reranker-base");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:Endpoint"] = "https://api.openai.com/v1",
                ["Chat:ApiKey"] = apiKey ?? "",
                ["Chat:Model"] = model,
                ["Chat:SystemPrompt"] = "You are a test assistant."
            })
            .Build();

        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        scopeMock.Setup(s => s.ServiceProvider).Returns(sp);
        scopeFactoryMock.Setup(f => f.CreateScope()).Returns(scopeMock.Object);

        var mockHandler = new MockHttpMessageHandler(req =>
        {
            var isStream = false;
            if (req.Content != null)
            {
                var body = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                isStream = body.Contains("\"stream\":true");
            }

            if (isStream)
            {
                var sse = $"data: {{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"delta\":{{\"role\":\"assistant\",\"content\":\"{responseText}\"}},\"finish_reason\":null}}]}}\n\ndata: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
                };
            }
            else
            {
                var json = $"{{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"created\":1700000000,\"model\":\"{model}\",\"choices\":[{{\"index\":0,\"message\":{{\"role\":\"assistant\",\"content\":\"{responseText}\"}},\"finish_reason\":\"stop\"}}],\"usage\":{{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        });

        var clientFactoryMock = new Mock<IHttpClientFactory>();
        clientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(mockHandler));

        var chatService = new MafChatService(
            config,
            scopeFactoryMock.Object,
            clientFactoryMock.Object,
            NullLogger<MafChatService>.Instance,
            timeProvider: null,
            httpMessageHandler: mockHandler);

        return (chatService, embeddingMock, rerankMock);
    }

    [Fact]
    public void OpenAIModelsController_GetModels_Returns_StandardOpenAIList_With_AllModels()
    {
        var (chatService, embeddingMock, rerankMock) = CreateTestServices();
        var controller = new OpenAIModelsController(
            embeddingMock.Object,
            rerankMock.Object,
            chatService,
            NullLogger<OpenAIModelsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var actionResult = controller.GetModels();
        var contentResult = Assert.IsType<ContentResult>(actionResult);
        Assert.Equal(200, controller.Response.StatusCode);
        Assert.Equal("application/json", contentResult.ContentType);

        using var doc = JsonDocument.Parse(contentResult.Content!);
        var root = doc.RootElement;
        Assert.Equal("list", root.GetProperty("object").GetString());

        var data = root.GetProperty("data").EnumerateArray().ToList();
        var modelIds = data.Select(m => m.GetProperty("id").GetString()).ToList();

        Assert.Contains("reference-rag", modelIds);
        Assert.Contains("gpt-4o-mini", modelIds);
        Assert.Contains("bge-small-zh-v1.5", modelIds);
        Assert.Contains("bge-reranker-base", modelIds);
    }

    [Fact]
    public void OpenAIModelsController_GetModel_WithValidId_Returns_ModelMetadata()
    {
        var (chatService, embeddingMock, rerankMock) = CreateTestServices();
        var controller = new OpenAIModelsController(
            embeddingMock.Object,
            rerankMock.Object,
            chatService,
            NullLogger<OpenAIModelsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var actionResult = controller.GetModel("reference-rag");
        var contentResult = Assert.IsType<ContentResult>(actionResult);
        Assert.Equal(200, controller.Response.StatusCode);

        using var doc = JsonDocument.Parse(contentResult.Content!);
        Assert.Equal("reference-rag", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("model", doc.RootElement.GetProperty("object").GetString());
    }

    [Fact]
    public void OpenAIModelsController_GetModel_WithEmptyId_Returns_BadRequest()
    {
        var (chatService, embeddingMock, rerankMock) = CreateTestServices();
        var controller = new OpenAIModelsController(
            embeddingMock.Object,
            rerankMock.Object,
            chatService,
            NullLogger<OpenAIModelsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var actionResult = controller.GetModel("");
        var contentResult = Assert.IsType<ContentResult>(actionResult);
        Assert.Equal(400, controller.Response.StatusCode);

        using var doc = JsonDocument.Parse(contentResult.Content!);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task OpenAIChatController_MissingMessages_Returns_BadRequest()
    {
        var (chatService, _, _) = CreateTestServices();
        var controller = new OpenAIChatController(
            chatService,
            new SimpleTokenizer(),
            NullLogger<OpenAIChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        using var doc = JsonDocument.Parse("{\"model\": \"reference-rag\"}");
        var actionResult = await controller.CreateChatCompletion(doc.RootElement, CancellationToken.None);
        var contentResult = Assert.IsType<ContentResult>(actionResult);
        Assert.Equal(400, controller.Response.StatusCode);

        using var resDoc = JsonDocument.Parse(contentResult.Content!);
        var error = resDoc.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Contains("messages is required", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OpenAIChatController_EmptyMessages_Returns_BadRequest()
    {
        var (chatService, _, _) = CreateTestServices();
        var controller = new OpenAIChatController(
            chatService,
            new SimpleTokenizer(),
            NullLogger<OpenAIChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        using var doc = JsonDocument.Parse("{\"messages\": []}");
        var actionResult = await controller.CreateChatCompletion(doc.RootElement, CancellationToken.None);
        var contentResult = Assert.IsType<ContentResult>(actionResult);
        Assert.Equal(400, controller.Response.StatusCode);

        using var resDoc = JsonDocument.Parse(contentResult.Content!);
        var error = resDoc.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Contains("cannot be empty", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OpenAIChatController_WhenApiKeyNotConfigured_Returns_503()
    {
        var (chatService, _, _) = CreateTestServices(apiKey: null);
        var controller = new OpenAIChatController(
            chatService,
            new SimpleTokenizer(),
            NullLogger<OpenAIChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        using var doc = JsonDocument.Parse("{\"messages\": [{\"role\": \"user\", \"content\": \"hello\"}]}");
        var actionResult = await controller.CreateChatCompletion(doc.RootElement, CancellationToken.None);
        var contentResult = Assert.IsType<ContentResult>(actionResult);
        Assert.Equal(503, controller.Response.StatusCode);

        using var resDoc = JsonDocument.Parse(contentResult.Content!);
        var error = resDoc.RootElement.GetProperty("error");
        Assert.Equal("service_unavailable", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task OpenAIChatController_NonStreaming_Returns_OpenAICompletion()
    {
        var (chatService, _, _) = CreateTestServices(
            apiKey: "sk-valid-key",
            responseText: "Hello from ReferenceRAG!");

        var controller = new OpenAIChatController(
            chatService,
            new SimpleTokenizer(),
            NullLogger<OpenAIChatController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        using var doc = JsonDocument.Parse("{\"model\": \"gpt-4o-mini\", \"messages\": [{\"role\": \"user\", \"content\": \"hi\"}], \"stream\": false}");
        var actionResult = await controller.CreateChatCompletion(doc.RootElement, CancellationToken.None);
        var contentResult = Assert.IsType<ContentResult>(actionResult);
        Assert.Equal(200, controller.Response.StatusCode);

        using var resDoc = JsonDocument.Parse(contentResult.Content!);
        var root = resDoc.RootElement;
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());

        var choices = root.GetProperty("choices").EnumerateArray().ToList();
        Assert.Single(choices);
        var message = choices[0].GetProperty("message");
        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.Equal("Hello from ReferenceRAG!", message.GetProperty("content").GetString());

        var usage = root.GetProperty("usage");
        Assert.True(usage.GetProperty("prompt_tokens").GetInt32() > 0);
        Assert.True(usage.GetProperty("completion_tokens").GetInt32() > 0);
        Assert.True(usage.GetProperty("total_tokens").GetInt32() > 0);
    }
}
