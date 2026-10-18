using Microsoft.Extensions.Logging;
using ReferenceRAG.Business.Features.Indexing.Adapters;
using ReferenceRAG.Business.Features.Indexing.StateMachine;
using ReferenceRAG.Business.Features.Indexing.Strategies;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Indexing.Services;

/// <summary>
/// 分块准入网关接口
/// </summary>
public interface IChunkGatekeeperService
{
    (List<ChunkRecord> FilteredChunks, ChunkIngestionTelemetry Telemetry) GatekeepChunks(
        IReadOnlyList<ChunkRecord> rawChunks,
        string filePath);
}

/// <summary>
/// 分块准入网关实现：基于适配器责任链、处置策略与生命周期状态机，阻断入库噪音并节约推理算力
/// </summary>
public class ChunkGatekeeperService : IChunkGatekeeperService
{
    private readonly IChunkQualificationAdapter _qualificationAdapter;
    private readonly Dictionary<IngestionActionType, IChunkIngestionStrategy> _strategies;
    private readonly ILogger<ChunkGatekeeperService>? _logger;

    public ChunkGatekeeperService(
        IChunkQualificationAdapter? qualificationAdapter = null,
        ILogger<ChunkGatekeeperService>? logger = null)
    {
        _qualificationAdapter = qualificationAdapter ?? new CompositeChunkQualificationAdapter();
        _logger = logger;
        _strategies = new Dictionary<IngestionActionType, IChunkIngestionStrategy>
        {
            [IngestionActionType.AbsorbToAdjacent] = new AbsorbToAdjacentStrategy(),
            [IngestionActionType.Discard] = new DiscardNoiseStrategy(),
            [IngestionActionType.Accept] = new AdaptiveIngestionStrategy()
        };
    }

    public (List<ChunkRecord> FilteredChunks, ChunkIngestionTelemetry Telemetry) GatekeepChunks(
        IReadOnlyList<ChunkRecord> rawChunks,
        string filePath)
    {
        var stateMachine = new ChunkIngestionStateMachine(filePath, _logger);
        stateMachine.RecordExtracted(rawChunks.Count);

        if (rawChunks.Count == 0)
        {
            stateMachine.TransitionToEmbeddingReady(0);
            return (new List<ChunkRecord>(), stateMachine.Telemetry);
        }

        stateMachine.TransitionToEvaluating();
        var accepted = new List<ChunkRecord>();

        for (int i = 0; i < rawChunks.Count; i++)
        {
            var chunk = rawChunks[i];
            var evalContext = new ChunkEvaluationContext
            {
                ChunkIndex = i,
                TotalChunks = rawChunks.Count,
                FilePath = filePath,
                HasPreviousChunk = accepted.Count > 0,
                HasNextChunk = i + 1 < rawChunks.Count
            };

            var qualResult = _qualificationAdapter.Evaluate(chunk, evalContext);
            if (qualResult.IsQualified)
            {
                stateMachine.RecordQualified(i);
                accepted.Add(chunk);
            }
            else
            {
                if (!_strategies.TryGetValue(qualResult.RecommendedAction, out var strategy))
                {
                    strategy = _strategies[IngestionActionType.AbsorbToAdjacent];
                }

                var stratResult = strategy.Apply(chunk, i, rawChunks, accepted);
                if (stratResult.ChunkRetained)
                {
                    accepted.Add(chunk);
                }
                else if (qualResult.RecommendedAction == IngestionActionType.AbsorbToAdjacent)
                {
                    stateMachine.RecordAbsorbed(i, stratResult.OperationSummary);
                }
                else
                {
                    stateMachine.RecordDiscarded(i, stratResult.OperationSummary);
                }
            }
        }

        // 重新规范化 ChunkIndex
        for (int i = 0; i < accepted.Count; i++)
        {
            accepted[i].ChunkIndex = i;
        }

        stateMachine.TransitionToEmbeddingReady(accepted.Count);

        if (stateMachine.Telemetry.SavedEmbeddingsCount > 0)
        {
            _logger?.LogInformation(
                "文件入库防噪完成: {File} | 原始: {Total} -> 准入: {Accepted} (吸收: {Absorbed}, 丢弃: {Discarded}, 节约推理: {Saved} 次, 预估节约GPU: {GpuMs:F1}ms)",
                Path.GetFileName(filePath),
                stateMachine.Telemetry.TotalExtracted,
                accepted.Count,
                stateMachine.Telemetry.AbsorbedCount,
                stateMachine.Telemetry.DiscardedCount,
                stateMachine.Telemetry.SavedEmbeddingsCount,
                stateMachine.Telemetry.SavedGpuTimeEstimateMs);
        }

        return (accepted, stateMachine.Telemetry);
    }
}
