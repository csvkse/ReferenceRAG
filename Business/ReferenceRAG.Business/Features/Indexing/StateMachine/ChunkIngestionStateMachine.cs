using Microsoft.Extensions.Logging;

namespace ReferenceRAG.Business.Features.Indexing.StateMachine;

/// <summary>
/// 分块入库生命周期状态机：全程跟踪从提取、评估、处置到推理持久化的每个状态跃迁
/// </summary>
public class ChunkIngestionStateMachine
{
    private readonly ILogger? _logger;
    private readonly object _lock = new();

    public ChunkIngestionPhase CurrentPhase { get; private set; } = ChunkIngestionPhase.Extracted;
    public ChunkIngestionTelemetry Telemetry { get; } = new();

    public ChunkIngestionStateMachine(string filePath, ILogger? logger = null)
    {
        Telemetry.FilePath = filePath;
        _logger = logger;
    }

    public void RecordExtracted(int totalCount)
    {
        lock (_lock)
        {
            CurrentPhase = ChunkIngestionPhase.Extracted;
            Telemetry.TotalExtracted = totalCount;
            AddAuditLog($"[Extracted] 提取原始分块 {totalCount} 个");
        }
    }

    public void TransitionToEvaluating()
    {
        lock (_lock)
        {
            CurrentPhase = ChunkIngestionPhase.Evaluating;
            AddAuditLog("[Evaluating] 开始准入与信息密度评估");
        }
    }

    public void RecordQualified(int chunkIndex)
    {
        lock (_lock)
        {
            CurrentPhase = ChunkIngestionPhase.Qualified;
            Telemetry.QualifiedCount++;
            AddAuditLog($"[Qualified] 分块 #{chunkIndex} 准入通过");
        }
    }

    public void RecordAbsorbed(int chunkIndex, string detail)
    {
        lock (_lock)
        {
            CurrentPhase = ChunkIngestionPhase.Absorbed;
            Telemetry.AbsorbedCount++;
            AddAuditLog($"[Absorbed] 分块 #{chunkIndex} 已吸收: {detail}");
            _logger?.LogDebug("入库防噪: 分块 #{Index} 已吸收: {Detail}", chunkIndex, detail);
        }
    }

    public void RecordDiscarded(int chunkIndex, string detail)
    {
        lock (_lock)
        {
            CurrentPhase = ChunkIngestionPhase.Discarded;
            Telemetry.DiscardedCount++;
            AddAuditLog($"[Discarded] 分块 #{chunkIndex} 已丢弃: {detail}");
            _logger?.LogInformation("入库防噪拦截: 分块 #{Index} 已彻底丢弃: {Detail}", chunkIndex, detail);
        }
    }

    public void TransitionToEmbeddingReady(int readyCount)
    {
        lock (_lock)
        {
            CurrentPhase = ChunkIngestionPhase.EmbeddingReady;
            AddAuditLog($"[EmbeddingReady] {readyCount} 个分块准备就绪，进入向量流水线 (已节省 {Telemetry.SavedEmbeddingsCount} 次推理)");
        }
    }

    public void RecordEmbedded(int count)
    {
        lock (_lock)
        {
            CurrentPhase = ChunkIngestionPhase.Embedded;
            AddAuditLog($"[Embedded] 完成 {count} 个分块的向量推理");
        }
    }

    public void RecordPersisted(int count)
    {
        lock (_lock)
        {
            CurrentPhase = ChunkIngestionPhase.Persisted;
            Telemetry.PersistedCount = count;
            AddAuditLog($"[Persisted] {count} 个净水分块已写入向量库与BM25索引");
        }
    }

    private void AddAuditLog(string message)
    {
        var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        Telemetry.AuditLogs.Add($"[{timestamp}] {message}");
    }
}
