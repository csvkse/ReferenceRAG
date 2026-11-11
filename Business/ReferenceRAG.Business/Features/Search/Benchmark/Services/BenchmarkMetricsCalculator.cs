using System;
using System.Collections.Generic;
using System.Linq;
using ReferenceRAG.Business.Features.Search.Benchmark.Contracts;

namespace ReferenceRAG.Business.Features.Search.Benchmark.Services;

/// <summary>
/// 检索单次查询的评测结果项
/// </summary>
public sealed record QueryEvaluationResult(
    string QuestionId,
    BenchmarkQuestionCase TestCase,
    IReadOnlyList<string> RetrievedChunkIdsOrTitles,
    double LatencyMs,
    bool Success = true
);

/// <summary>
/// 通用知识库评测学术与工业指标计算器
/// </summary>
public static class BenchmarkMetricsCalculator
{
    public static BenchmarkEvaluationMetrics CalculateMetrics(
        IReadOnlyList<QueryEvaluationResult> results,
        double totalDurationSeconds)
    {
        if (results == null || results.Count == 0)
        {
            return new BenchmarkEvaluationMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        int total = results.Count;
        int top1Hits = 0;
        int top3Hits = 0;
        int top5Hits = 0;
        int top10Hits = 0;
        double sumMrr = 0.0;
        double sumNdcg = 0.0;
        double sumSupportingRecall = 0.0;
        int supportingExactMatches = 0;

        var latencies = new List<double>(total);

        foreach (var res in results)
        {
            latencies.Add(res.LatencyMs);
            var goldTargets = new HashSet<string>(
                res.TestCase.GoldChunkIds.Concat(res.TestCase.SupportingEvidenceTitles),
                StringComparer.OrdinalIgnoreCase);

            var retrieved = res.RetrievedChunkIdsOrTitles ?? Array.Empty<string>();

            // 1. Hit@K & MRR@10
            int firstRank = -1;
            for (int r = 0; r < Math.Min(retrieved.Count, 10); r++)
            {
                var cand = retrieved[r];
                if (goldTargets.Any(g => cand.Contains(g, StringComparison.OrdinalIgnoreCase) || g.Contains(cand, StringComparison.OrdinalIgnoreCase)))
                {
                    firstRank = r + 1; // 1-based rank
                    break;
                }
            }

            if (firstRank > 0)
            {
                if (firstRank <= 1) top1Hits++;
                if (firstRank <= 3) top3Hits++;
                if (firstRank <= 5) top5Hits++;
                if (firstRank <= 10) top10Hits++;
                sumMrr += 1.0 / firstRank;
            }

            // 2. NDCG@10
            double dcg = 0.0;
            int relevantFound = 0;
            for (int r = 0; r < Math.Min(retrieved.Count, 10); r++)
            {
                var cand = retrieved[r];
                if (goldTargets.Any(g => cand.Contains(g, StringComparison.OrdinalIgnoreCase) || g.Contains(cand, StringComparison.OrdinalIgnoreCase)))
                {
                    relevantFound++;
                    dcg += 1.0 / (Math.Log2(r + 2)); // rel = 1
                }
            }
            double idcg = 0.0;
            int maxPossibleRelevant = Math.Min(goldTargets.Count, 10);
            for (int r = 0; r < maxPossibleRelevant; r++)
            {
                idcg += 1.0 / (Math.Log2(r + 2));
            }
            sumNdcg += idcg > 0 ? dcg / idcg : (dcg > 0 ? 1.0 : 0.0);

            // 3. Supporting-Facts Recall & Exact Match
            if (res.TestCase.SupportingEvidenceTitles.Count > 0)
            {
                int matchedSupporting = 0;
                foreach (var sup in res.TestCase.SupportingEvidenceTitles)
                {
                    if (retrieved.Any(cand => cand.Contains(sup, StringComparison.OrdinalIgnoreCase) || sup.Contains(cand, StringComparison.OrdinalIgnoreCase)))
                    {
                        matchedSupporting++;
                    }
                }

                double recall = (double)matchedSupporting / res.TestCase.SupportingEvidenceTitles.Count;
                sumSupportingRecall += recall;
                if (matchedSupporting == res.TestCase.SupportingEvidenceTitles.Count)
                {
                    supportingExactMatches++;
                }
            }
            else
            {
                sumSupportingRecall += firstRank > 0 ? 1.0 : 0.0;
                if (firstRank > 0) supportingExactMatches++;
            }
        }

        latencies.Sort();
        double p50 = GetPercentile(latencies, 0.50);
        double p95 = GetPercentile(latencies, 0.95);
        double p99 = GetPercentile(latencies, 0.99);
        double avg = latencies.Average();
        double qps = totalDurationSeconds > 0 ? total / totalDurationSeconds : 0;

        return new BenchmarkEvaluationMetrics(
            TotalQueries: total,
            Top1HitRate: (double)top1Hits / total,
            Top3HitRate: (double)top3Hits / total,
            Top5HitRate: (double)top5Hits / total,
            Top10HitRate: (double)top10Hits / total,
            Mrr10: sumMrr / total,
            Ndcg10: sumNdcg / total,
            SupportingFactsRecall: sumSupportingRecall / total,
            SupportingFactsExactMatchRate: (double)supportingExactMatches / total,
            LatencyP50Ms: p50,
            LatencyP95Ms: p95,
            LatencyP99Ms: p99,
            AverageLatencyMs: avg,
            Qps: qps
        );
    }

    private static double GetPercentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Ceiling(percentile * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}
