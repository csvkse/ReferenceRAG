namespace ObsidianRAG.Core.Models;

/// <summary>
/// 聚合类型
/// </summary>
public enum AggregateType
{
    /// <summary>
    /// 普通分段
    /// </summary>
    None = 0,
    
    /// <summary>
    /// 文档级聚合
    /// </summary>
    Document = 1,
    
    /// <summary>
    /// 章节级聚合
    /// </summary>
    Section = 2
}

/// <summary>
/// 分段类型
/// </summary>
public enum ChunkType
{
    /// <summary>
    /// 普通文本
    /// </summary>
    Text = 0,
    
    /// <summary>
    /// 代码块
    /// </summary>
    Code = 1,
    
    /// <summary>
    /// 表格
    /// </summary>
    Table = 2,
    
    /// <summary>
    /// 列表
    /// </summary>
    List = 3,
    
    /// <summary>
    /// 图片
    /// </summary>
    Image = 4,
    
    /// <summary>
    /// 强制分段
    /// </summary>
    Forced = 5
}

/// <summary>
/// 查询模式
/// </summary>
public enum QueryMode
{
    /// <summary>
    /// 快速模式：~1000 tokens
    /// </summary>
    Quick = 0,

    /// <summary>
    /// 标准模式：~3000 tokens（默认）
    /// </summary>
    Standard = 1,

    /// <summary>
    /// 深度模式：~6000 tokens
    /// </summary>
    Deep = 2,

    /// <summary>
    /// 混合模式：BM25 + Embedding 混合搜索
    /// </summary>
    Hybrid = 3
}

/// <summary>
/// 文件变动类型
/// </summary>
public enum ChangeType
{
    Created = 0,
    Modified = 1,
    Deleted = 2,
    Renamed = 3,
    FolderRenamed = 4
}

/// <summary>
/// 分段配置
/// </summary>
public class ChunkingOptions
{
    /// <summary>
    /// 最大 token 数
    /// </summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>
    /// 最小 token 数
    /// </summary>
    public int MinTokens { get; set; } = 50;

    /// <summary>
    /// 重叠 token 数
    /// </summary>
    public int OverlapTokens { get; set; } = 50;

    /// <summary>
    /// 是否保留代码块完整性
    /// </summary>
    public bool PreserveCodeBlocks { get; set; } = true;

    /// <summary>
    /// 是否保留表格完整性
    /// </summary>
    public bool PreserveTables { get; set; } = true;

    /// <summary>
    /// 是否保留标题结构
    /// </summary>
    public bool PreserveHeadings { get; set; } = true;
}
