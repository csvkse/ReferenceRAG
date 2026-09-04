using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class MafChatServiceTests
{
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
                Assert.Contains("Chat__ApiKey", item.Message);
            },
            item => Assert.Equal("done", item.Type));
    }
}
