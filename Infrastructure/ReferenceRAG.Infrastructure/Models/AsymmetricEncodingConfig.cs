namespace ReferenceRAG.Core.Models;

/// <summary>
/// 非对称编码配置（用于 BGE、E5 等需要 query/passage 前缀的模型）
/// </summary>
public class AsymmetricEncodingConfig
{
    /// <summary>
    /// 查询侧前缀，如 "query: "
    /// </summary>
    public string QueryPrefix { get; set; } = "query: ";

    /// <summary>
    /// 文档侧前缀，如 "passage: "
    /// </summary>
    public string DocumentPrefix { get; set; } = "passage: ";

    /// <summary>
    /// 验证配置合法性（前缀长度限制）
    /// </summary>
    public void Validate()
    {
        const int MaxPrefixLength = 64;
        if (QueryPrefix?.Length > MaxPrefixLength)
            throw new ArgumentException($"QueryPrefix 不能超过 {MaxPrefixLength} 字符");
        if (DocumentPrefix?.Length > MaxPrefixLength)
            throw new ArgumentException($"DocumentPrefix 不能超过 {MaxPrefixLength} 字符");
    }
}
