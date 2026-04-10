using Xunit;

namespace ObsidianRAG.Tests;

/// <summary>
/// 重排评估指标测试
/// 验证 NDCG、MRR、MAP 等指标计算的正确性
/// </summary>
public class RerankMetricsTests
{
    #region 辅助方法：模拟指标计算

    /// <summary>
    /// 计算 NDCG (Normalized Discounted Cumulative Gain)
    /// </summary>
    private double CalculateNdcg(List<double> expectedRelevance, List<(int OriginalIndex, double Score)> results)
    {
        if (expectedRelevance.Count == 0 || results.Count == 0) return 0;

        // 计算 DCG
        double dcg = 0;
        for (int i = 0; i < results.Count; i++)
        {
            var doc = results[i];
            var expected = doc.OriginalIndex >= 0 && doc.OriginalIndex < expectedRelevance.Count
                ? expectedRelevance[doc.OriginalIndex]
                : 0;
            dcg += expected / Math.Log2(i + 2);
        }

        // 计算 IDCG（理想排序）
        var idealRelevance = expectedRelevance.OrderByDescending(r => r).ToList();
        double idcg = 0;
        for (int i = 0; i < idealRelevance.Count; i++)
        {
            idcg += idealRelevance[i] / Math.Log2(i + 2);
        }

        return idcg > 0 ? dcg / idcg : 0;
    }

    /// <summary>
    /// 计算 MRR (Mean Reciprocal Rank)
    /// </summary>
    private double CalculateMrr(List<double> expectedRelevance, List<(int OriginalIndex, double Score)> results)
    {
        if (expectedRelevance.Count == 0 || results.Count == 0) return 0;

        var maxExpected = expectedRelevance.Max();
        if (maxExpected <= 0) return 0;

        var topDocOriginalIndex = expectedRelevance.IndexOf(maxExpected);
        var topDocRank = results
            .Select((r, i) => new { Result = r, Rank = i + 1 })
            .FirstOrDefault(x => x.Result.OriginalIndex == topDocOriginalIndex);

        return topDocRank != null ? 1.0 / topDocRank.Rank : 0;
    }

    /// <summary>
    /// 计算 MAP (Mean Average Precision)
    /// </summary>
    private double CalculateMap(List<double> expectedRelevance, List<(int OriginalIndex, double Score)> results)
    {
        if (expectedRelevance.Count == 0 || results.Count == 0) return 0;

        const double relevanceThreshold = 0.5;
        var relevantCount = expectedRelevance.Count(r => r >= relevanceThreshold);
        if (relevantCount == 0) return 0;

        double sumPrecision = 0;
        int foundRelevant = 0;

        for (int i = 0; i < results.Count; i++)
        {
            var doc = results[i];
            var expected = doc.OriginalIndex >= 0 && doc.OriginalIndex < expectedRelevance.Count
                ? expectedRelevance[doc.OriginalIndex]
                : 0;

            if (expected >= relevanceThreshold)
            {
                foundRelevant++;
                sumPrecision += (double)foundRelevant / (i + 1);
            }
        }

        return sumPrecision / relevantCount;
    }

    /// <summary>
    /// 计算 MAE (Mean Absolute Error)
    /// </summary>
    private double CalculateMae(List<double> expectedRelevance, List<(int OriginalIndex, double Score)> results)
    {
        if (expectedRelevance.Count == 0 || results.Count == 0) return 0;

        var errors = new List<double>();
        foreach (var doc in results)
        {
            if (doc.OriginalIndex >= 0 && doc.OriginalIndex < expectedRelevance.Count)
            {
                var expected = expectedRelevance[doc.OriginalIndex];
                errors.Add(Math.Abs(doc.Score - expected));
            }
        }

        return errors.Count > 0 ? errors.Average() : 0;
    }

    #endregion

    #region TC-MET-001: 理想排序 NDCG=1.0

    [Fact]
    public void TC_MET_001_PerfectRanking_NdcgEqualsOne()
    {
        Console.WriteLine("=== TC-MET-001: 理想排序 NDCG=1.0 ===");

        // 期望相关性: 文档0最相关，文档1次相关，文档2最不相关
        var expectedRelevance = new List<double> { 1.0, 0.5, 0.0 };

        // 完美排序: 按分数降序排列，与期望一致
        var results = new List<(int OriginalIndex, double Score)>
        {
            (0, 0.95),  // 最相关的排第一
            (1, 0.60),  // 次相关的排第二
            (2, 0.10)   // 最不相关的排第三
        };

        var ndcg = CalculateNdcg(expectedRelevance, results);

        Console.WriteLine($"期望相关性: [{string.Join(", ", expectedRelevance)}]");
        Console.WriteLine($"结果排序: [{string.Join(", ", results.Select(r => $"(doc{r.OriginalIndex}, {r.Score:F2})"))}]");
        Console.WriteLine($"NDCG: {ndcg:F4}");

        Assert.Equal(1.0, ndcg, 2); // 允许 0.01 误差

        Console.WriteLine("✓ 理想排序 NDCG 测试通过");
    }

    #endregion

    #region TC-MET-002: 最差排序 NDCG 接近 0

    [Fact]
    public void TC_MET_002_WorstRanking_NdcgNearZero()
    {
        Console.WriteLine("=== TC-MET-002: 最差排序 NDCG 接近 0 ===");

        // 期望相关性: 文档0最相关，文档1次相关，文档2最不相关
        var expectedRelevance = new List<double> { 1.0, 0.5, 0.0 };

        // 最差排序: 完全逆序
        var results = new List<(int OriginalIndex, double Score)>
        {
            (2, 0.95),  // 最不相关的排第一
            (1, 0.60),  // 次相关的排第二
            (0, 0.10)   // 最相关的排最后
        };

        var ndcg = CalculateNdcg(expectedRelevance, results);

        Console.WriteLine($"期望相关性: [{string.Join(", ", expectedRelevance)}]");
        Console.WriteLine($"结果排序: [{string.Join(", ", results.Select(r => $"(doc{r.OriginalIndex}, {r.Score:F2})"))}]");
        Console.WriteLine($"NDCG: {ndcg:F4}");

        // 最差排序时 NDCG 应该较低
        Assert.True(ndcg < 0.8, $"NDCG 应该较低，实际值: {ndcg}");

        Console.WriteLine("✓ 最差排序 NDCG 测试通过");
    }

    #endregion

    #region TC-MET-003: 首位命中 MRR=1.0

    [Fact]
    public void TC_MET_003_TopDocumentFirst_MrrEqualsOne()
    {
        Console.WriteLine("=== TC-MET-003: 首位命中 MRR=1.0 ===");

        var expectedRelevance = new List<double> { 0.3, 1.0, 0.5 }; // 文档1 最相关

        // 最相关文档排在第一位
        var results = new List<(int OriginalIndex, double Score)>
        {
            (1, 0.95),  // 文档1 (最相关) 排第一
            (2, 0.60),
            (0, 0.30)
        };

        var mrr = CalculateMrr(expectedRelevance, results);

        Console.WriteLine($"最相关文档索引: {expectedRelevance.IndexOf(expectedRelevance.Max())}");
        Console.WriteLine($"结果中首位文档索引: {results[0].OriginalIndex}");
        Console.WriteLine($"MRR: {mrr:F4}");

        Assert.Equal(1.0, mrr, 2);

        Console.WriteLine("✓ 首位命中 MRR 测试通过");
    }

    #endregion

    #region TC-MET-004: 第三位命中 MRR≈0.33

    [Fact]
    public void TC_MET_004_TopDocumentThird_MrrApproxThird()
    {
        Console.WriteLine("=== TC-MET-004: 第三位命中 MRR≈0.33 ===");

        var expectedRelevance = new List<double> { 0.3, 0.5, 1.0 }; // 文档2 最相关

        // 最相关文档排在第三位
        var results = new List<(int OriginalIndex, double Score)>
        {
            (0, 0.50),
            (1, 0.40),
            (2, 0.95)   // 文档2 (最相关) 排第三
        };

        var mrr = CalculateMrr(expectedRelevance, results);

        Console.WriteLine($"最相关文档索引: {expectedRelevance.IndexOf(expectedRelevance.Max())}");
        Console.WriteLine($"最相关文档排名: 3");
        Console.WriteLine($"MRR: {mrr:F4}");
        Console.WriteLine($"预期 MRR: {1.0/3:F4}");

        Assert.Equal(1.0 / 3, mrr, 2);

        Console.WriteLine("✓ 第三位命中 MRR 测试通过");
    }

    #endregion

    #region TC-MET-005: MAP 计算正确性

    [Fact]
    public void TC_MET_005_MapCalculation_CorrectValue()
    {
        Console.WriteLine("=== TC-MET-005: MAP 计算正确性 ===");

        // 3 个相关文档 (>=0.5)，2 个不相关文档
        var expectedRelevance = new List<double> { 0.9, 0.2, 0.7, 0.3, 0.8 };

        // 排序结果: 相关、不相关、相关、不相关、相关
        var results = new List<(int OriginalIndex, double Score)>
        {
            (0, 0.90),  // 相关 @ 1
            (1, 0.50),  // 不相关
            (2, 0.70),  // 相关 @ 3
            (3, 0.40),  // 不相关
            (4, 0.80)   // 相关 @ 5
        };

        var map = CalculateMap(expectedRelevance, results);

        // 手工计算:
        // 相关文档索引: 0, 2, 4
        // 排名: 1, 3, 5
        // Precision@1 = 1/1 = 1.0
        // Precision@3 = 2/3 ≈ 0.67
        // Precision@5 = 3/5 = 0.6
        // MAP = (1.0 + 0.67 + 0.6) / 3 ≈ 0.756

        double expectedMap = (1.0 + 2.0/3.0 + 3.0/5.0) / 3;

        Console.WriteLine($"期望相关性: [{string.Join(", ", expectedRelevance)}]");
        Console.WriteLine($"MAP: {map:F4}");
        Console.WriteLine($"预期 MAP: {expectedMap:F4}");

        Assert.Equal(expectedMap, map, 2);

        Console.WriteLine("✓ MAP 计算测试通过");
    }

    #endregion

    #region TC-MET-006: MAE 计算正确性

    [Fact]
    public void TC_MET_006_MaeCalculation_CorrectValue()
    {
        Console.WriteLine("=== TC-MET-006: MAE 计算正确性 ===");

        var expectedRelevance = new List<double> { 0.9, 0.5, 0.1 };

        var results = new List<(int OriginalIndex, double Score)>
        {
            (0, 0.85),  // 误差 = |0.85 - 0.9| = 0.05
            (1, 0.60),  // 误差 = |0.60 - 0.5| = 0.10
            (2, 0.20)   // 误差 = |0.20 - 0.1| = 0.10
        };

        var mae = CalculateMae(expectedRelevance, results);

        // MAE = (0.05 + 0.10 + 0.10) / 3 = 0.0833
        double expectedMae = (0.05 + 0.10 + 0.10) / 3;

        Console.WriteLine($"MAE: {mae:F4}");
        Console.WriteLine($"预期 MAE: {expectedMae:F4}");

        Assert.Equal(expectedMae, mae, 3);

        Console.WriteLine("✓ MAE 计算测试通过");
    }

    #endregion

    #region TC-MET-007: 空输入处理

    [Fact]
    public void TC_MET_007_EmptyInput_ReturnsZero()
    {
        Console.WriteLine("=== TC-MET-007: 空输入处理 ===");

        var emptyRelevance = new List<double>();
        var emptyResults = new List<(int, double)>();

        var ndcg = CalculateNdcg(emptyRelevance, emptyResults);
        var mrr = CalculateMrr(emptyRelevance, emptyResults);
        var map = CalculateMap(emptyRelevance, emptyResults);
        var mae = CalculateMae(emptyRelevance, emptyResults);

        Console.WriteLine($"空输入 NDCG: {ndcg}");
        Console.WriteLine($"空输入 MRR: {mrr}");
        Console.WriteLine($"空输入 MAP: {map}");
        Console.WriteLine($"空输入 MAE: {mae}");

        Assert.Equal(0.0, ndcg);
        Assert.Equal(0.0, mrr);
        Assert.Equal(0.0, map);
        Assert.Equal(0.0, mae);

        Console.WriteLine("✓ 空输入处理测试通过");
    }

    #endregion

    #region TC-MET-008: 单文档情况

    [Fact]
    public void TC_MET_008_SingleDocument_CalculatesCorrectly()
    {
        Console.WriteLine("=== TC-MET-008: 单文档情况 ===");

        var expectedRelevance = new List<double> { 0.8 };
        var results = new List<(int OriginalIndex, double Score)> { (0, 0.75) };

        var ndcg = CalculateNdcg(expectedRelevance, results);
        var mrr = CalculateMrr(expectedRelevance, results);
        var map = CalculateMap(expectedRelevance, results);
        var mae = CalculateMae(expectedRelevance, results);

        Console.WriteLine($"单文档 NDCG: {ndcg:F4}");
        Console.WriteLine($"单文档 MRR: {mrr:F4}");
        Console.WriteLine($"单文档 MAP: {map:F4}");
        Console.WriteLine($"单文档 MAE: {mae:F4}");

        // 单文档情况下
        Assert.Equal(1.0, ndcg, 2); // NDCG 应该是 1.0
        Assert.Equal(1.0, mrr, 2);   // MRR 应该是 1.0
        Assert.Equal(1.0, map, 2);   // MAP 应该是 1.0
        Assert.Equal(0.05, mae, 2);  // MAE = |0.75 - 0.8| = 0.05

        Console.WriteLine("✓ 单文档情况测试通过");
    }

    #endregion

    #region TC-MET-009: 全部不相关文档

    [Fact]
    public void TC_MET_009_AllIrrelevant_HandlesGracefully()
    {
        Console.WriteLine("=== TC-MET-009: 全部不相关文档 ===");

        // 所有关联性都是 0 或低于阈值
        var expectedRelevance = new List<double> { 0.1, 0.2, 0.3 };
        var results = new List<(int OriginalIndex, double Score)>
        {
            (0, 0.5),
            (1, 0.4),
            (2, 0.3)
        };

        var ndcg = CalculateNdcg(expectedRelevance, results);
        var mrr = CalculateMrr(expectedRelevance, results);
        var map = CalculateMap(expectedRelevance, results);

        Console.WriteLine($"NDCG (全部不相关): {ndcg:F4}");
        Console.WriteLine($"MRR (全部不相关): {mrr:F4}");
        Console.WriteLine($"MAP (全部不相关): {map:F4}");

        // 应该返回 0 或有效值，不应该抛出异常
        Assert.True(ndcg >= 0 && ndcg <= 1);
        Assert.Equal(0.0, mrr); // 没有相关文档，MRR = 0
        Assert.Equal(0.0, map); // 没有相关文档，MAP = 0

        Console.WriteLine("✓ 全部不相关文档测试通过");
    }

    #endregion

    #region TC-MET-010: 完整场景测试

    [Fact]
    public void TC_MET_010_CompleteScenario_AllMetricsCalculated()
    {
        Console.WriteLine("=== TC-MET-010: 完整场景测试 ===");

        // 模拟真实的重排结果
        var expectedRelevance = new List<double>
        {
            0.95,  // 文档0: 高度相关
            0.10,  // 文档1: 不相关
            0.85,  // 文档2: 相关
            0.30,  // 文档3: 低相关
            0.90   // 文档4: 高度相关
        };

        // 模拟重排结果（按预测分数排序）
        var results = new List<(int OriginalIndex, double Score)>
        {
            (0, 0.92),  // 高度相关排第一 - 正确
            (4, 0.88),  // 高度相关排第二 - 正确
            (2, 0.75),  // 相关排第三 - 正确
            (3, 0.40),  // 低相关排第四 - 正确
            (1, 0.15)   // 不相关排最后 - 正确
        };

        var ndcg = CalculateNdcg(expectedRelevance, results);
        var mrr = CalculateMrr(expectedRelevance, results);
        var map = CalculateMap(expectedRelevance, results);
        var mae = CalculateMae(expectedRelevance, results);

        Console.WriteLine("\n=== 完整场景结果 ===");
        Console.WriteLine($"期望相关性: [{string.Join(", ", expectedRelevance.Select(e => e.ToString("F2")))}]");
        Console.WriteLine("\n重排结果:");
        foreach (var r in results)
        {
            var exp = expectedRelevance[r.OriginalIndex];
            Console.WriteLine($"  文档{r.OriginalIndex}: 预测={r.Score:F2}, 期望={exp:F2}, 误差={Math.Abs(r.Score - exp):F2}");
        }
        Console.WriteLine($"\n评估指标:");
        Console.WriteLine($"  NDCG: {ndcg:F4}");
        Console.WriteLine($"  MRR:  {mrr:F4}");
        Console.WriteLine($"  MAP:  {map:F4}");
        Console.WriteLine($"  MAE:  {mae:F4}");

        // 验证所有指标在合理范围内
        Assert.InRange(ndcg, 0.8, 1.0);  // 排序很好，NDCG 应该高
        Assert.Equal(1.0, mrr, 2);        // 最相关文档排第一
        Assert.InRange(map, 0.7, 1.0);    // MAP 应该较高
        Assert.InRange(mae, 0.0, 0.2);    // MAE 应该较低

        Console.WriteLine("\n✓ 完整场景测试通过");
    }

    #endregion
}
