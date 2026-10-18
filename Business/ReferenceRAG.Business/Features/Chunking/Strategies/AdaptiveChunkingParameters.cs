namespace ReferenceRAG.Business.Features.Chunking.Strategies;

/// <summary>
/// 自适应分段计算输出参数
/// </summary>
public class AdaptiveChunkingParameters
{
    /// <summary>计算的目标 Token 数</summary>
    public int TargetTokens { get; set; } = 360;

    /// <summary>允许的最大 Token 数（预留元数据余量）</summary>
    public int MaxTokens { get; set; } = 400;

    /// <summary>最小 Token 数（低于此阈值短节将被自动合并）</summary>
    public int MinTokens { get; set; } = 50;

    /// <summary>重叠 Token 数</summary>
    public int OverlapTokens { get; set; } = 36;

    /// <summary>是否保留标题结构</summary>
    public bool PreserveHeadings { get; set; } = true;

    /// <summary>是否保护代码块原子性</summary>
    public bool PreserveCodeBlocks { get; set; } = true;

    /// <summary>是否保护表格原子性</summary>
    public bool PreserveTables { get; set; } = true;

    /// <summary>代码块重叠率（代码建议 0% 重叠避免语法混乱）</summary>
    public float CodeOverlapRatio { get; set; } = 0.0f;

    /// <summary>策略名称</summary>
    public string StrategyName { get; set; } = string.Empty;
}
