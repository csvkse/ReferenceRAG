using Microsoft.Extensions.Logging;
using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;
using System.Collections.Concurrent;

namespace ReferenceRAG.Business.Features.Indexing.Strategies;

/// <summary>
/// 索引策略执行上下文
/// </summary>
public class IndexExecutionContext
{
    public required IndexRequest Request { get; init; }
    public required IIndexStorageAdapter StorageAdapter { get; init; }
    public required IFileIndexPipeline Pipeline { get; init; }
    public required IVectorStore VectorStore { get; init; }
    public required IEmbeddingService EmbeddingService { get; init; }
    public required ConfigManager ConfigManager { get; init; }
    public required IFileProcessingGuard Guard { get; init; }
    public required ConcurrentDictionary<string, byte> ActiveFiles { get; init; }
    public required ConcurrentDictionary<string, byte> HeldFiles { get; init; }
    public required ILogger Logger { get; init; }
}
