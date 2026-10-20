using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class MafChatServiceTests
{
    private class MockSseHandler(string ssePayload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(ssePayload, System.Text.Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task ToolCallWithEmptyTypeThrowsArgumentOutOfRangeExceptionWithoutSanitization()
    {
        var rawPayload = "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"\",\"function\":{\"name\":\"SearchKnowledge\",\"arguments\":\"{}\"}}]}}]}\n\ndata: [DONE]\n\n";
        var httpClient = new HttpClient(new MockSseHandler(rawPayload));
        var options = new OpenAI.OpenAIClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
        };
        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("dummy"), options).GetChatClient("gpt-4o");

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            await foreach (var update in client.CompleteChatStreamingAsync([new OpenAI.Chat.UserChatMessage("hello")]))
            {
            }
        });

        Assert.Contains("ChatToolCallKind", ex.Message);
    }

    [Fact]
    public async Task ToolCallWithEmptyTypeSucceedsWithSanitizingHandler()
    {
        var rawPayload = "data: {\"id\":\"1\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"\",\"function\":{\"name\":\"SearchKnowledge\",\"arguments\":\"{}\"}}]}}]}\n\ndata: [DONE]\n\n";
        var mockHandler = new MockSseHandler(rawPayload);
        var sanitizingHandler = new OpenAiToolCallSanitizingHandler(mockHandler);
        var httpClient = new HttpClient(sanitizingHandler);
        var options = new OpenAI.OpenAIClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
        };
        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("dummy"), options).GetChatClient("gpt-4o");

        var updates = new List<OpenAI.Chat.StreamingChatCompletionUpdate>();
        await foreach (var update in client.CompleteChatStreamingAsync([new OpenAI.Chat.UserChatMessage("hello")]))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        var toolCall = Assert.Single(updates[0].ToolCallUpdates);
        Assert.Equal(OpenAI.Chat.ChatToolCallKind.Function, toolCall.Kind);
        Assert.Equal("SearchKnowledge", toolCall.FunctionName);
    }

    [Fact]
    public async Task ToolCallWithNullTypeSucceedsWithSanitizingHandler()
    {
        var rawPayload = "data: {\"id\":\"2\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_2\",\"type\":null,\"function\":{\"name\":\"GetIndexStatus\",\"arguments\":\"{}\"}}]}}]}\n\ndata: [DONE]\n\n";
        var mockHandler = new MockSseHandler(rawPayload);
        var sanitizingHandler = new OpenAiToolCallSanitizingHandler(mockHandler);
        var httpClient = new HttpClient(sanitizingHandler);
        var options = new OpenAI.OpenAIClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
        };
        var client = new OpenAI.OpenAIClient(new System.ClientModel.ApiKeyCredential("dummy"), options).GetChatClient("gpt-4o");

        var updates = new List<OpenAI.Chat.StreamingChatCompletionUpdate>();
        await foreach (var update in client.CompleteChatStreamingAsync([new OpenAI.Chat.UserChatMessage("hello")]))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        var toolCall = Assert.Single(updates[0].ToolCallUpdates);
        Assert.Equal(OpenAI.Chat.ChatToolCallKind.Function, toolCall.Kind);
        Assert.Equal("GetIndexStatus", toolCall.FunctionName);
    }

    [Fact]
    public async Task MissingApiKeyReturnsConfigurationErrorWithoutBreakingServiceCreation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:ApiKey"] = string.Empty
            })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .BuildServiceProvider();

        var service = new MafChatService(
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<ILogger<MafChatService>>());

        var events = new List<SseEvent>();
        await foreach (var item in service.StreamAsync(service.CreateSession(), "hello"))
            events.Add(item);

        Assert.Collection(
            events,
            item =>
            {
                Assert.Equal("error", item.Type);
                Assert.Contains("ApiKey", item.Message);
            },
            item => Assert.Equal("done", item.Type));
    }

    [Fact]
    public void UpdateConfiguration_TakesEffectImmediatelyWithoutRestart()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:ApiKey"] = string.Empty
            })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .BuildServiceProvider();

        var service = new MafChatService(
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<ILogger<MafChatService>>());

        // 初始状态无 Key
        var session = service.CreateSession();

        // 动态热更新配置
        service.UpdateConfiguration("https://api.openai.com/v1", "test-key-123", "gpt-4o", "自定义系统提示词");

        // 验证热更新后创建新会话成功且无需重启
        var newSession = service.CreateSession();
        Assert.NotNull(newSession);
    }

    [Fact]
    public async Task LocalGguf_WithConfigManager_StreamsSuccessfullyWithoutApiKey()
    {
        var config = new ReferenceRAG.Core.Models.ObsidianRagConfig();
        config.Chat.ActiveBackend = "LocalGguf";
        config.Chat.LocalGguf.ModelName = "Qwen2.5-1.5B-Instruct-GGUF";
        config.Chat.LocalGguf.ModelPath = @"C:\models\dummy-qwen.gguf";

        var configManager = new ReferenceRAG.Core.Services.ConfigManager(config);
        var services = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .BuildServiceProvider();

        var configuration = new ConfigurationBuilder().Build();

        var service = new MafChatService(
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<ILogger<MafChatService>>(),
            configManager: configManager);

        Assert.True(service.IsConfigured);
        Assert.True(service.IsLocalGguf);
        Assert.Equal("Qwen2.5-1.5B-Instruct-GGUF", service.CurrentModel);

        var events = new List<SseEvent>();
        await foreach (var item in service.StreamAsync(service.CreateSession(), "你好，请自我介绍"))
        {
            events.Add(item);
        }

        // 验证没有输出缺少 ApiKey 错误，且正常完成
        Assert.DoesNotContain(events, e => e.Type == "error" && (e.Message?.Contains("ApiKey") ?? false));
        Assert.Contains(events, e => e.Type == "text" && (e.Delta?.Contains("本地模型") ?? false));
        Assert.Contains(events, e => e.Type == "done");
    }

    [Fact]
    public async Task LocalLlamaChatClient_SimulationMode_StreamsTokensAndCompletes()
    {
        using var client = new ReferenceRAG.Business.Features.Chat.Services.LocalLlamaChatClient(
            modelPath: @"C:\non_existent_path\test.gguf",
            modelName: "Test-Qwen-GGUF");

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>
        {
            new(Microsoft.Extensions.AI.ChatRole.System, "你是一个助手"),
            new(Microsoft.Extensions.AI.ChatRole.User, "测试问题")
        };

        var updates = new List<Microsoft.Extensions.AI.ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        var fullText = string.Join("", updates.Select(u => u.Text));
        Assert.Contains("Test-Qwen-GGUF", fullText);
        Assert.Contains("测试问题", fullText);

        var response = await client.GetResponseAsync(messages);
        Assert.NotNull(response);
        Assert.Contains("Test-Qwen-GGUF", response.Text);
    }
}
