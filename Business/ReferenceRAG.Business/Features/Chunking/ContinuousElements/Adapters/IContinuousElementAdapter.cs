namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Adapters;

/// <summary>
/// 连续结构元素适配器统一契约
/// </summary>
/// <typeparam name="TElement">解析出的结构化模型类型</typeparam>
public interface IContinuousElementAdapter<TElement>
{
    /// <summary>
    /// 探测给定的行流自 startIndex 开始是否匹配该结构
    /// </summary>
    /// <param name="lines">全量行数组</param>
    /// <param name="startIndex">当前扫描行索引</param>
    /// <param name="consumedLines">若匹配，消耗的行数</param>
    /// <returns>是否匹配</returns>
    bool CanAdapt(string[] lines, int startIndex, out int consumedLines);

    /// <summary>
    /// 将匹配的行流适配转换为结构化模型对象
    /// </summary>
    /// <param name="lines">全量行数组</param>
    /// <param name="startIndex">起始行索引</param>
    /// <param name="consumedLines">消耗行数</param>
    /// <param name="startLineNumber">原始行号（1-indexed）</param>
    /// <param name="precedingContext">可选的前置说明导语</param>
    /// <returns>领域模型对象</returns>
    TElement Adapt(string[] lines, int startIndex, int consumedLines, int startLineNumber, string? precedingContext = null);
}
