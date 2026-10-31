using ReferenceRAG.Core.Features.KnowledgeBase.Domain;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Connectors;

/// <summary>
/// 数据源连接器契约
/// </summary>
public interface IDataConnector : IAsyncDisposable, IDisposable
{
    string Id { get; }
    string KnowledgeBaseId { get; }
    string Kind { get; }

    /// <summary>
    /// 扫描数据源并产出规范化变更事件
    /// </summary>
    IAsyncEnumerable<SourceChange> ScanAsync(SyncRequest request, CancellationToken ct = default);

    /// <summary>
    /// 读取特定变更源的原始数据流
    /// </summary>
    ValueTask<Stream> OpenReadAsync(SourceChange item, CancellationToken ct = default);
}
