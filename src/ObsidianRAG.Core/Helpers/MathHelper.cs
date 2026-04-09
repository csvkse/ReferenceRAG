namespace ObsidianRAG.Core.Helpers;

/// <summary>
/// 数学工具类 - 共享数学计算方法
/// </summary>
public static class MathHelper
{
    /// <summary>
    /// 计算余弦相似度
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;

        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator < 1e-10f ? 0 : dot / denominator;
    }
}
