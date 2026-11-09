namespace ReferenceRAG.Core.Features.Search.Lexical;

/// <summary>
/// 词性加权词元
/// </summary>
public sealed record WeightedToken(
    string Word,
    string PosTag,
    double Weight,
    bool IsEntity = false
);

/// <summary>
/// 词性标注与信息熵分析适配器接口
/// </summary>
public interface IPosAnalysisAdapter
{
    /// <summary>
    /// 对长句进行词性标注、停用词与套话过滤，产出带信息熵权重的词元列表
    /// </summary>
    IReadOnlyList<WeightedToken> AnalyzeAndWeight(string query);
}
