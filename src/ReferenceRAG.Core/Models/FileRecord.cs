namespace ReferenceRAG.Core.Models;

/// <summary>
/// 文件记录
/// </summary>
public class FileRecord
{
    // 主键
    public string Id { get; set; } = Guid.NewGuid().ToString();
    
    // 路径信息
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? ParentFolder { get; set; }
    
    // 源信息（多源支持）
    public string? Source { get; set; }
    
    // 内容指纹
    public string ContentHash { get; set; } = string.Empty;
    public long ContentLength { get; set; }
    
    // 元数据（从 frontmatter 提取）
    public string? Title { get; set; }
    public List<string>? Tags { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    
    // 统计信息
    public int ChunkCount { get; set; }
    public long TotalTokens { get; set; }
    
    // 流行度（用于去偏）
    public int AccessCount { get; set; }
    public int ReferenceCount { get; set; }
    
    // 系统字段
    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
    public int Version { get; set; } = 1;
}
