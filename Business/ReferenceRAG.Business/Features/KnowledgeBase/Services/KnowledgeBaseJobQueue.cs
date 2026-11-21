using System.Threading.Channels;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Business.Features.KnowledgeBase.Services;

public enum KnowledgeBaseJobKind
{
    Sync,
    Rechunk
}

/// <summary>
/// 知识库受管后台作业请求载荷
/// </summary>
public sealed record KnowledgeBaseJobRequest(
    KnowledgeBaseJobKind Kind,
    JobDefinition Job,
    ConnectorDefinition? Connector,
    IReadOnlyList<ConnectorDefinition>? AllConnectors,
    CancellationTokenSource JobCts)
{
    public static KnowledgeBaseJobRequest ForSync(JobDefinition job, ConnectorDefinition connector, CancellationTokenSource cts)
        => new(KnowledgeBaseJobKind.Sync, job, connector, null, cts);

    public static KnowledgeBaseJobRequest ForRechunk(JobDefinition job, IReadOnlyList<ConnectorDefinition> connectors, CancellationTokenSource cts)
        => new(KnowledgeBaseJobKind.Rechunk, job, null, connectors, cts);
}

/// <summary>
/// 知识库后台作业队列接口
/// </summary>
public interface IKnowledgeBaseJobQueue
{
    ValueTask EnqueueAsync(KnowledgeBaseJobRequest request, CancellationToken ct = default);
    ValueTask<KnowledgeBaseJobRequest> DequeueAsync(CancellationToken ct = default);
}

/// <summary>
/// 知识库后台作业队列实现（基于 Channel 有界队列，提供背压与线程安全）
/// </summary>
public sealed class KnowledgeBaseJobQueue : IKnowledgeBaseJobQueue
{
    private readonly Channel<KnowledgeBaseJobRequest> _channel =
        Channel.CreateBounded<KnowledgeBaseJobRequest>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    public ValueTask EnqueueAsync(KnowledgeBaseJobRequest request, CancellationToken ct = default)
    {
        return _channel.Writer.WriteAsync(request, ct);
    }

    public ValueTask<KnowledgeBaseJobRequest> DequeueAsync(CancellationToken ct = default)
    {
        return _channel.Reader.ReadAsync(ct);
    }
}
