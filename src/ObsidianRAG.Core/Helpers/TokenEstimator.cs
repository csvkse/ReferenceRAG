namespace ObsidianRAG.Core.Helpers;

/// <summary>
/// Token 估算工具类 - 统一的 token 数量估算逻辑
/// </summary>
public static class TokenEstimator
{
    /// <summary>
    /// 估算文本的 token 数量（中英混合）
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var chineseCount = text.Count(c => c > 0x4E00 && c < 0x9FFF);
        var otherCount = text.Length - chineseCount;
        return (int)(chineseCount / 1.5 + otherCount / 4);
    }
}
