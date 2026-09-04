namespace ReferenceRAG.Core.Models;

/// <summary>
/// 向量记录
/// </summary>
public class VectorRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ChunkId { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    
    // 向量数据
    public float[] Vector { get; set; } = Array.Empty<float>();
    public int Dimension { get; set; }
    
    // 内容信息
    public string? Content { get; set; }
    public string? Source { get; set; }
    public string? FilePath { get; set; }
    public string? Title { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? HeadingPath { get; set; }
    
    // 模型信息
    public string ModelName { get; set; } = "bge-small-zh-v1.5";
    public string? ModelVersion { get; set; }
    
    // 系统字段
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
