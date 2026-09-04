using Microsoft.AspNetCore.SignalR;
using ReferenceRAG.Business.Features.Indexing.Contracts;

namespace ReferenceRAG.Service.Hubs;

public sealed class IndexEventPublisher(IHubContext<IndexHub> hub, ILogger<IndexEventPublisher> logger) : IIndexEventPublisher
{
    public event Action<string, object>? Published;
    public async Task PublishAsync(string name, object payload, CancellationToken cancellationToken = default)
    {
        // Desktop subscribes to the same business events without a SignalR connection.
        foreach (var subscriber in Published?.GetInvocationList() ?? [])
        {
            try { ((Action<string, object>)subscriber)(name, payload); }
            catch (Exception ex) { logger.LogWarning(ex, "Index event subscriber disconnected"); }
        }
        await hub.Clients.Group("index-watchers").SendAsync(name, payload, cancellationToken);
    }
}
