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

    /// <summary>
    /// 分块配置指纹（MaxTokens/MinTokens/OverlapTokens/Preserve* 的哈希）。
    /// 分块配置变更后，即使内容 hash 不变也必须重新分块。
    /// </summary>
    public string? ChunkingHash { get; set; }
    
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

    /// <summary>
    /// 索引状态：'pending' = Phase1已执行但Phase3未完成；'complete' = 全流程完成。
    /// 用于防止中断后因 hash 匹配而跳过未完成文件。
    /// </summary>
    public string IndexedStatus { get; set; } = "complete";
}
