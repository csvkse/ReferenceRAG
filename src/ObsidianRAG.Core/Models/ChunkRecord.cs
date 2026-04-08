namespace ObsidianRAG.Core.Models;

/// <summary>
/// 分段记录
/// </summary>
public class ChunkRecord
{
    // 主键
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileId { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    
    // 源信息（多源支持）
    public string? Source { get; set; }
    
    // 内容
    public string Content { get; set; } = string.Empty;
    public string? EnhancedContent { get; set; }
    public int TokenCount { get; set; }
    
    // 行范围（精确定位）
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int StartColumn { get; set; } = 1;
    public int EndColumn { get; set; }
    
    // 章节信息
    public string? HeadingPath { get; set; }
    public int Level { get; set; }
    
    // 聚合标识
    public AggregateType AggregateType { get; set; } = AggregateType.None;
    public string? AggregateRange { get; set; }
    public int ChildChunkCount { get; set; }
    
    // 分段类型
    public ChunkType ChunkType { get; set; } = ChunkType.Text;
    
    // 权重（用于聚合）
    public float Weight { get; set; } = 1.0f;
    
    // 元数据
    public List<string>? Tags { get; set; }
    public List<string>? Keywords { get; set; }
}
