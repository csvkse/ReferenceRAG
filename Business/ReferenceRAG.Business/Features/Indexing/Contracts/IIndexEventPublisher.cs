namespace ReferenceRAG.Business.Features.Indexing.Contracts;

public interface IIndexEventPublisher
{
    Task PublishAsync(string name, object payload, CancellationToken cancellationToken = default);
}
