namespace ObsidianRAG.Core.Models;

/// <summary>
/// 文本增强上下文
/// </summary>
public class EnhancementContext
{
    /// <summary>
    /// 文档标题
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// 标签列表
    /// </summary>
    public List<string>? Tags { get; set; }

    /// <summary>
    /// 章节路径
    /// </summary>
    public string? HeadingPath { get; set; }

    /// <summary>
    /// 文件路径
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// 父文件夹
    /// </summary>
    public string? ParentFolder { get; set; }
}
