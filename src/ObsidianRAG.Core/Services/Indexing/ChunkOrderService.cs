using System.Security.Cryptography;
using System.Text;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services.Indexing;

/// <summary>
/// 分段排序服务：提供小数排序中间值计算和内容哈希功能
/// </summary>
public static class ChunkOrderService
{
    /// <summary>
    /// 计算小数排序的中间值，支持在任意位置插入新分段
    /// </summary>
    /// <param name="prev">前一个分段的排序值（null 表示开头）</param>
    /// <param name="next">后一个分段的排序值（null 表示末尾）</param>
    /// <returns>新的排序值，位于 prev 和 next 之间</returns>
    /// <remarks>
    /// 策略：
    /// - 首个分段：(null, null) → 1.0
    /// - 开头插入：(null, n) → n / 2.0
    /// - 末尾追加：(p, null) → p + 1.0
    /// - 中间插入：(p, n) → (p + n) / 2.0
    /// </remarks>
    public static double Midpoint(double? prev, double? next) => (prev, next) switch
    {
        (null, null) => 1.0,
        (null, double n) => n / 2.0,
        (double p, null) => p + 1.0,
        (double p, double n) => (p + n) / 2.0
    };

    /// <summary>
    /// 检测小数排序精度是否耗尽，需要重新平衡
    /// </summary>
    /// <param name="chunks">已排序的分段集合</param>
    /// <returns>true 表示精度耗尽，需要重新平衡排序值</returns>
    /// <remarks>
    /// 当相邻分段排序值差值小于 1e-10 时，认为精度耗尽
    /// </remarks>
    public static bool NeedsRebalance(IEnumerable<ChunkRecord> chunks)
    {
        double? prev = null;
        foreach (var c in chunks.OrderBy(x => x.ChunkOrder))
        {
            if (prev is not null && c.ChunkOrder - prev < 1e-10) return true;
            prev = c.ChunkOrder;
        }
        return false;
    }

    /// <summary>
    /// 计算内容哈希，用于精确去重和变更检测
    /// </summary>
    /// <param name="content">分段内容</param>
    /// <returns>SHA256 哈希的十六进制字符串（前16位）</returns>
    public static string ComputeContentHash(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;
        
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        
        // 转换为十六进制字符串，取前16位（64位精度足够去重）
        return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
    }

    /// <summary>
    /// 重新平衡所有分段的排序值
    /// </summary>
    /// <param name="chunks">需要重新平衡的分段集合</param>
    /// <remarks>
    /// 按 ChunkOrder 排序后，均匀分配排序值（1.0, 2.0, 3.0, ...）
    /// </remarks>
    public static void Rebalance(IEnumerable<ChunkRecord> chunks)
    {
        var orderedChunks = chunks.OrderBy(x => x.ChunkOrder).ToList();
        for (int i = 0; i < orderedChunks.Count; i++)
        {
            orderedChunks[i].ChunkOrder = i + 1.0;
        }
    }
}
