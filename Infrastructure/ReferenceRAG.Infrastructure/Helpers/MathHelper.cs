using System.Numerics.Tensors;

namespace ReferenceRAG.Core.Helpers;

/// <summary>
/// 数学工具类 - 共享数学计算方法 (全面支持硬件 SIMD AVX2/AVX-512/ARM Neon 指令集加速)
/// </summary>
public static class MathHelper
{
    /// <summary>
    /// 计算余弦相似度 (基于 SIMD 硬件指令加速)
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length != b.Length || a.Length == 0) return 0f;
        return TensorPrimitives.CosineSimilarity(a, b);
    }

    /// <summary>
    /// 计算跨度余弦相似度 (零内存分配，直接计算 ReadOnlySpan)
    /// </summary>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0f;
        return TensorPrimitives.CosineSimilarity(a, b);
    }
}
