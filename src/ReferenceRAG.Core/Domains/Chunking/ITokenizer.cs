namespace ReferenceRAG.Core.Interfaces;

/// <summary>
/// Token 计数器接口
/// </summary>
public interface ITokenizer
{
    /// <summary>
    /// 计算 token 数量
    /// </summary>
    int CountTokens(string text);
    
    /// <summary>
    /// 批量计算 token 数量
    /// </summary>
    Dictionary<string, int> CountTokensBatch(IEnumerable<string> texts);
}
