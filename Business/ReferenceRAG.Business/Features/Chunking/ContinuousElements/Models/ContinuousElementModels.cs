namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Models;

/// <summary>
/// 结构化代码块元素
/// </summary>
public class CodeBlockElement
{
    /// <summary>
    /// 代码语言标签（如 csharp, python, json 等）
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// 开围栏标记（如 ```csharp）
    /// </summary>
    public string OpenFence { get; set; } = "```";

    /// <summary>
    /// 闭围栏标记（如 ```）
    /// </summary>
    public string CloseFence { get; set; } = "```";

    /// <summary>
    /// 代码内部行集合
    /// </summary>
    public List<string> CodeLines { get; set; } = new();

    /// <summary>
    /// 在文档中的起始行号（含开围栏）
    /// </summary>
    public int StartLine { get; set; }

    /// <summary>
    /// 在文档中的结束行号（含闭围栏）
    /// </summary>
    public int EndLine { get; set; }

    /// <summary>
    /// 是否合规闭合（防止文档 EOF 漏闭合导致误吞后续内容）
    /// </summary>
    public bool IsProperlyClosed { get; set; } = true;

    /// <summary>
    /// 完整代码块文本
    /// </summary>
    public string RawContent { get; set; } = string.Empty;
}

/// <summary>
/// 结构化多级列表/大纲树元素
/// </summary>
public class ListTreeElement
{
    /// <summary>
    /// 列表项节点列表
    /// </summary>
    public List<ListItemNode> Items { get; set; } = new();

    /// <summary>
    /// 列表起始行号
    /// </summary>
    public int StartLine { get; set; }

    /// <summary>
    /// 列表结束行号
    /// </summary>
    public int EndLine { get; set; }

    /// <summary>
    /// 完整列表原始文本
    /// </summary>
    public string RawContent { get; set; } = string.Empty;
}

/// <summary>
/// 列表项节点
/// </summary>
public class ListItemNode
{
    /// <summary>
    /// 缩进深度（0 表示顶层）
    /// </summary>
    public int IndentLevel { get; set; }

    /// <summary>
    /// 列表标记符号（-, *, +, 1. 等）
    /// </summary>
    public string Marker { get; set; } = "-";

    /// <summary>
    /// 该项具体正文内容
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 祖先层级路径面包屑链（如 ["1. 系统架构", "1.1 存储层"]）
    /// </summary>
    public List<string> AncestorBreadcrumbs { get; set; } = new();

    /// <summary>
    /// 该项在文档中的起始行号
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// 预估 Token 数
    /// </summary>
    public int EstimatedTokens { get; set; }
}
