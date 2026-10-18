namespace ReferenceRAG.Business.Features.Indexing.StateMachine;

/// <summary>
/// 分块入库防噪与生命周期遥测快照
/// </summary>
public sealed class ChunkIngestionTelemetry
{
    public string FilePath { get; set; } = string.Empty;
    public int TotalExtracted { get; set; }
    public int QualifiedCount { get; set; }
    public int AbsorbedCount { get; set; }
    public int DiscardedCount { get; set; }
    public int SavedEmbeddingsCount => AbsorbedCount + DiscardedCount;
    public int PersistedCount { get; set; }
    public double SavedGpuTimeEstimateMs => SavedEmbeddingsCount * 3.5; // 按单块平均3.5ms预估
    public List<string> AuditLogs { get; } = new();
}
