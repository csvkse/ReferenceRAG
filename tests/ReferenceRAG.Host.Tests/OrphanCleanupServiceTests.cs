using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class OrphanCleanupServiceTests
{
    [Fact]
    public async Task ShutdownDuringInitialDelayCompletesWithoutCancellationError()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var service = new TestableOrphanCleanupService(
            services,
            services.GetRequiredService<ILogger<OrphanCleanupService>>());
        using var cancellation = new CancellationTokenSource();

        var running = service.RunAsync(cancellation.Token);
        cancellation.Cancel();

        await running;
    }

    private sealed class TestableOrphanCleanupService(
        IServiceProvider serviceProvider,
        ILogger<OrphanCleanupService> logger)
        : OrphanCleanupService(serviceProvider, logger)
    {
        public Task RunAsync(CancellationToken cancellationToken)
            => ExecuteAsync(cancellationToken);
    }
}
