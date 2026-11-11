using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.KnowledgeBase.Connectors;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Features.ModelManagement.Runtime;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services.Graph;

namespace ReferenceRAG.Core.Features.KnowledgeBase.Jobs;

/// <summary>
/// 知识库同步与索引执行器（单库单连接器执行，支持向量与文本双通道）
/// </summary>
public sealed class KnowledgeBaseSyncWorker
{
    private readonly ICatalogStore _catalog;
    private readonly IKnowledgeBaseStorageManager _storageManager;
    private readonly IModelRouter? _modelRouter;
    private readonly ILogger<KnowledgeBaseSyncWorker>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly bool _enableEmbedding;
    private readonly IMarkdownChunker? _chunker;
    private readonly WikiLinkExtractor _linkExtractor = new();

    public KnowledgeBaseSyncWorker(
        ICatalogStore catalog,
        IKnowledgeBaseStorageManager storageManager,
        IModelRouter? modelRouter = null,
        ILogger<KnowledgeBaseSyncWorker>? logger = null,
        ILoggerFactory? loggerFactory = null,
        bool enableEmbedding = true,
        IMarkdownChunker? chunker = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _storageManager = storageManager ?? throw new ArgumentNullException(nameof(storageManager));
        _modelRouter = modelRouter;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _enableEmbedding = enableEmbedding;
        _chunker = chunker;
    }

    public async Task<JobDefinition> ExecuteSyncAsync(
        JobDefinition job,
        IDataConnector connector,
        CancellationToken ct = default)
    {
        var smLogger = _loggerFactory?.CreateLogger<JobStateMachine>();
        var stateMachine = new JobStateMachine(job, _catalog, smLogger);

        await stateMachine.TransitionToAsync(OperationPhase.Scanning, JobStatus.Running, ct: ct);

        try
        {
            var kbDef = await _catalog.GetKnowledgeBaseAsync(job.KnowledgeBaseId, ct);
            var specId = kbDef?.EmbeddingSpecId ?? "default-spec";
            var chunkSpecId = kbDef?.ChunkingSpecId ?? "spec_chunk_default";
            var chunkSpec = await _catalog.GetChunkingSpecAsync(chunkSpecId, ct)
                ?? await _catalog.GetChunkingSpecAsync("spec_chunk_default", ct)
                ?? new ChunkingSpec("spec_chunk_default");

            // 1. 扫描全部变更项
            var changes = new List<SourceChange>();
            await foreach (var change in connector.ScanAsync(new SyncRequest(FullScan: true), ct))
            {
                changes.Add(change);
            }

            await stateMachine.UpdateProgressAsync(0, changes.Count, ct);

            // 2. 依次处理并分块提交
            await stateMachine.TransitionToAsync(OperationPhase.Chunking, ct: ct);

            int processed = 0;
            int errors = 0;

            foreach (var change in changes)
            {
                if (stateMachine.Status == JobStatus.Cancelling || ct.IsCancellationRequested)
                {
                    _logger?.LogInformation("[KnowledgeBaseSyncWorker] 检测到取消请求，停止后续文档处理: {JobId}", job.Id);
                    await stateMachine.TransitionToAsync(OperationPhase.None, JobStatus.Cancelled, ct: ct);
                    return stateMachine.CurrentJob;
                }

                if (change.Kind == ChangeKind.Delete)
                {
                    await using var lease = await _storageManager.AcquireLeaseAsync(job.KnowledgeBaseId, StorageAccess.ReadWrite, ct);
                    if (!_enableEmbedding || string.Equals(specId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase))
                        await lease.Store.EnsureLexicalIndexAsync(ct);
                    await lease.Store.DeleteDocumentAsync(connector.Id, change.SourceKey, ct);
                    await lease.Store.GraphStore.DeleteNodeAsync(change.SourceKey, ct);
                    processed++;
                    await stateMachine.UpdateProgressAsync(processed, ct: ct);
                    continue;
                }

                try
                {
                    string textContent;
                    await using (var stream = await connector.OpenReadAsync(change, ct))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        textContent = await reader.ReadToEndAsync(ct);
                    }

                    var hash = ComputeSha256(textContent);
                    var chunks = ChunkTextWithSpec(textContent, chunkSpec, _chunker);

                    // 可选向量提取（纯词法模式下直接旁路跳过）
                    var isPureLexical = string.Equals(specId, "spec_lexical_only", StringComparison.OrdinalIgnoreCase);
                    List<float[]>? vectors = null;
                    if (_enableEmbedding && !isPureLexical && _modelRouter != null && chunks.Count > 0)
                    {
                        try
                        {
                            await using var modelLease = await _modelRouter.AcquireLeaseAsync<IEmbeddingBackendAdapter>(
                                new ModelSelection(specId, "embedding"),
                                new WorkloadContext($"job_{job.Id}", Priority: -1),
                                ct);
                            var texts = chunks.Select(c => c.EmbeddingText ?? c.Text).ToList();
                            var rawVectors = await modelLease.Client.EncodeBatchAsync(texts, null, ct);
                            vectors = rawVectors.ToList();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug("[KnowledgeBaseSyncWorker] 模型向量提取未就绪，降级为纯文本索引: {Msg}", ex.Message);
                        }
                    }

                    await using var lease = await _storageManager.AcquireLeaseAsync(job.KnowledgeBaseId, StorageAccess.ReadWrite, ct);

                    if (!_enableEmbedding || isPureLexical) await lease.Store.EnsureLexicalIndexAsync(ct);
                    var batch = new IndexBatch(
                        KnowledgeBaseId: job.KnowledgeBaseId,
                        ExpectedIndexEpoch: lease.IndexEpochSnapshot,
                        ExpectedDocumentVersion: 0,
                        ConnectorId: connector.Id,
                        SourceKey: change.SourceKey,
                        Locator: change.Locator,
                        SourceRevision: change.SourceRevision,
                        ContentHash: hash,
                        Chunks: chunks,
                        Vectors: vectors,
                        ProducerCohort: specId
                    );

                    var commitResult = await lease.Store.CommitDocumentAsync(batch, ct);
                    if (!commitResult.Success)
                    {
                        _logger?.LogWarning("[KnowledgeBaseSyncWorker] 提交文档失败: {Key}, 错误: {Err}", change.SourceKey, commitResult.ErrorMessage);
                        errors++;
                    }
                    else
                    {
                        // 提取并同步写入物理单库图谱节点与拓扑边
                        try
                        {
                            var graphLinks = _linkExtractor.Extract(textContent);
                            var fileNode = new GraphNode
                            {
                                Id = change.SourceKey,
                                Title = Path.GetFileNameWithoutExtension(change.SourceKey),
                                Type = "document",
                                ChunkIds = chunks.Select(c => c.ChunkId).ToList()
                            };
                            var extraNodes = new List<GraphNode>();
                            var edges = new List<GraphEdge>();
                            foreach (var (target, heading, type, line) in graphLinks)
                            {
                                var targetId = target;
                                if (!string.IsNullOrEmpty(heading))
                                {
                                    targetId = $"{target}#{heading}";
                                    extraNodes.Add(new GraphNode
                                    {
                                        Id = targetId,
                                        Title = heading,
                                        Type = "heading"
                                    });
                                }
                                else if (type == "tag")
                                {
                                    targetId = $"#{target}";
                                    extraNodes.Add(new GraphNode
                                    {
                                        Id = targetId,
                                        Title = target,
                                        Type = "tag"
                                    });
                                }
                                edges.Add(new GraphEdge
                                {
                                    FromId = change.SourceKey,
                                    ToId = targetId,
                                    Type = type,
                                    LineNumber = line
                                });
                            }
                            await lease.Store.GraphStore.UpsertFileGraphAsync(change.SourceKey, fileNode, extraNodes, edges, ct);
                        }
                        catch (Exception gEx)
                        {
                            _logger?.LogWarning(gEx, "[KnowledgeBaseSyncWorker] 单库图谱构建降级: {Key}", change.SourceKey);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[KnowledgeBaseSyncWorker] 处理文档异常: {Key}", change.SourceKey);
                    errors++;
                }

                processed++;
                await stateMachine.UpdateProgressAsync(processed, ct: ct);
            }

            await stateMachine.TransitionToAsync(OperationPhase.Publishing, ct: ct);

            var finalStatus = errors == 0 ? JobStatus.Succeeded :
                (errors < processed ? JobStatus.PartiallySucceeded : JobStatus.Failed);

            await stateMachine.TransitionToAsync(
                OperationPhase.None,
                finalStatus,
                errorMessage: errors > 0 ? $"{errors} 个文档处理失败" : null,
                ct: ct);

            return stateMachine.CurrentJob;
        }
        catch (OperationCanceledException)
        {
            await stateMachine.TransitionToAsync(OperationPhase.None, JobStatus.Cancelled, ct: CancellationToken.None);
            return stateMachine.CurrentJob;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[KnowledgeBaseSyncWorker] 任务执行致命异常: {JobId}", job.Id);
            await stateMachine.TransitionToAsync(OperationPhase.None, JobStatus.Failed, errorMessage: ex.Message, ct: CancellationToken.None);
            return stateMachine.CurrentJob;
        }
    }

    private static IReadOnlyList<ChunkData> ChunkTextWithSpec(
        string text,
        ChunkingSpec chunkSpec,
        IMarkdownChunker? chunker)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<ChunkData>();

        if (chunker != null)
        {
            var opt = new ChunkingOptions
            {
                MaxTokens = chunkSpec.MaxTokens > 0 ? chunkSpec.MaxTokens : 512,
                MinTokens = chunkSpec.MinTokens > 0 ? chunkSpec.MinTokens : 50,
                OverlapTokens = chunkSpec.OverlapTokens >= 0 ? chunkSpec.OverlapTokens : 50,
                PreserveCodeBlocks = chunkSpec.PreserveCodeBlocks,
                PreserveTables = chunkSpec.PreserveTables,
                PreserveHeadings = chunkSpec.PreserveHeadings,
                Mode = chunkSpec.Mode ?? "Adaptive",
                AdaptiveStrategyType = chunkSpec.StrategyType ?? "Balanced",
                AdaptiveLevel = chunkSpec.AdaptiveLevel ?? "Level2",
                TargetWindowUtilizationPercent = chunkSpec.TargetWindowUtilizationPercent > 0 ? chunkSpec.TargetWindowUtilizationPercent : 70
            };

            var records = chunker.Chunk(text, opt);
            if (records != null && records.Count > 0)
            {
                var result = new List<ChunkData>(records.Count);
                for (int i = 0; i < records.Count; i++)
                {
                    var r = records[i];
                    result.Add(new ChunkData(
                        ChunkId: Guid.NewGuid().ToString("N"),
                        Ordinal: i,
                        Text: r.Content,
                        TokenCount: r.TokenCount > 0 ? r.TokenCount : Math.Max(1, r.Content.Length / 4),
                        EmbeddingText: r.EnhancedContent ?? r.Content,
                        StartLine: r.StartLine,
                        EndLine: r.EndLine,
                        HeadingPath: r.HeadingPath
                    ));
                }
                return result;
            }
        }

        return FallbackLineChunk(text, chunkSpec);
    }

    private static IReadOnlyList<ChunkData> FallbackLineChunk(string text, ChunkingSpec chunkSpec)
    {
        var rawLines = text.Replace("\r\n", "\n").Split('\n');
        string? currentHeading = null;
        var list = new List<ChunkData>();
        var currentChunkBuilder = new StringBuilder();
        int chunkStartLine = 1;
        int ordinal = 0;

        int maxChars = Math.Max(128, (chunkSpec.MaxTokens > 0 ? chunkSpec.MaxTokens : 512) * 2);
        int minChars = Math.Min(30, (chunkSpec.MinTokens > 0 ? chunkSpec.MinTokens : 50));
        bool preserveCode = chunkSpec.PreserveCodeBlocks;
        bool insideCodeFence = false;

        for (int lineIdx = 0; lineIdx < rawLines.Length; lineIdx++)
        {
            var line = rawLines[lineIdx];
            int lineNum = lineIdx + 1;

            if (preserveCode && line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                insideCodeFence = !insideCodeFence;
            }

            if (!insideCodeFence && line.TrimStart().StartsWith('#'))
            {
                currentHeading = line.TrimStart('#', ' ').Trim();
            }

            if (currentChunkBuilder.Length == 0)
            {
                chunkStartLine = lineNum;
            }

            currentChunkBuilder.AppendLine(line);

            bool isSplitBoundary = !insideCodeFence && (
                (string.IsNullOrWhiteSpace(line) && currentChunkBuilder.Length >= minChars) ||
                currentChunkBuilder.Length >= maxChars
            );

            if (isSplitBoundary)
            {
                var chunkText = currentChunkBuilder.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(chunkText))
                {
                    list.Add(new ChunkData(
                        ChunkId: Guid.NewGuid().ToString("N"),
                        Ordinal: ordinal++,
                        Text: chunkText,
                        TokenCount: Math.Max(1, chunkText.Length / 4),
                        EmbeddingText: chunkText,
                        StartLine: chunkStartLine,
                        EndLine: lineNum,
                        HeadingPath: currentHeading
                    ));
                }
                currentChunkBuilder.Clear();
            }
        }

        if (currentChunkBuilder.Length > 0)
        {
            var remaining = currentChunkBuilder.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(remaining))
            {
                list.Add(new ChunkData(
                    ChunkId: Guid.NewGuid().ToString("N"),
                    Ordinal: ordinal++,
                    Text: remaining,
                    TokenCount: Math.Max(1, remaining.Length / 4),
                    EmbeddingText: remaining,
                    StartLine: chunkStartLine,
                    EndLine: rawLines.Length,
                    HeadingPath: currentHeading
                ));
            }
        }

        return list;
    }

    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
