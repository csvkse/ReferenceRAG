namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Models;

/// <summary>
/// 结构化 Markdown 表格元素
/// </summary>
public class TableElement
{
    /// <summary>
    /// 表头行列表（支持单行或复合多行表头）
    /// </summary>
    public List<string> HeaderRows { get; set; } = new();

    /// <summary>
    /// 表头分隔线行（如 |---|---|）
    /// </summary>
    public string SeparatorRow { get; set; } = string.Empty;

    /// <summary>
    /// 表格数据行列表
    /// </summary>
    public List<TableRowItem> DataRows { get; set; } = new();

    /// <summary>
    /// 表格紧邻的前置说明导语（如：*表 1-1：系统权限矩阵*）
    /// </summary>
    public string? PrecedingContext { get; set; }

    /// <summary>
    /// 表格在原始文档中的起始行号（1-indexed）
    /// </summary>
    public int StartLine { get; set; }

    /// <summary>
    /// 表格在原始文档中的结束行号（1-indexed）
    /// </summary>
    public int EndLine { get; set; }

    /// <summary>
    /// 预估的表头总 Token 数
    /// </summary>
    public int EstimatedHeaderTokens { get; set; }

    /// <summary>
    /// 表格在文档中的完整文本
    /// </summary>
    public string RawContent { get; set; } = string.Empty;
}

/// <summary>
/// 表格单行数据项
/// </summary>
public class TableRowItem
{
    /// <summary>
    /// 行原始文本
    /// </summary>
    public string RawContent { get; set; } = string.Empty;

    /// <summary>
    /// 在文档中的行号
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// 该行预估 Token 数
    /// </summary>
    public int EstimatedTokens { get; set; }
}
