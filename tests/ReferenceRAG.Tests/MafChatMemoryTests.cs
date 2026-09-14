using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Service.Services;

namespace ReferenceRAG.Tests;

public class MafChatMemoryTests
{
    [Fact]
    public void CreateSession_EvictsOldestSession_WhenConfiguredCapacityIsExceeded()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:ApiKey"] = string.Empty,
                ["Chat:MaxSessions"] = "2"
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

        var oldest = service.CreateSession();
        service.CreateSession();
        service.CreateSession();

        Assert.False(service.DeleteSession(oldest));
    }

    [Fact]
    public void CreateSession_RemovesSessionsThatExceededIdleTimeout()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:ApiKey"] = string.Empty,
                ["Chat:SessionIdleMinutes"] = "10"
            })
            .Build();
        var services = new ServiceCollection().AddLogging().AddHttpClient().BuildServiceProvider();
        var service = new MafChatService(
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<ILogger<MafChatService>>(),
            clock);

        var expired = service.CreateSession();
        clock.Advance(TimeSpan.FromMinutes(11));
        service.CreateSession();

        Assert.False(service.DeleteSession(expired));
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
