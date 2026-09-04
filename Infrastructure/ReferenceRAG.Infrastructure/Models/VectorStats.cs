namespace ReferenceRAG.Core.Models;

/// <summary>
/// 向量统计信息
/// </summary>
public class VectorStats
{
    public string ModelName { get; set; } = string.Empty;
    public int Dimension { get; set; }
    public int VectorCount { get; set; }
    public long StorageBytes { get; set; }
    public bool ModelExists { get; set; }
    public DateTime? LastUpdated { get; set; }
}
