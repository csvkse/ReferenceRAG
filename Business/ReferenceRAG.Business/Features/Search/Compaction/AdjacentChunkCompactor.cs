using System;
using System.Collections.Generic;
using System.Linq;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Infrastructure.Features.Search.Adapters;

namespace ReferenceRAG.Business.Features.Search.Compaction;

/// <summary>
/// 相邻分块压缩与连续性聚合器
/// 对同一文件内行号相邻或重叠的切片进行聚合，避免在 Context 中反复输出多余标题，减少 Token 浪费并提升自然语义连贯度
/// </summary>
public static class AdjacentChunkCompactor
{
    /// <summary>
    /// 对同文件且行号相邻或重叠的分块进行智能聚合
    /// </summary>
    /// <param name="chunks">原始候选分块列表</param>
    /// <param name="maxGapLines">允许视为连续分块的最大行间隔容差（默认 2 行空行）</param>
    /// <returns>合并压缩后的分块列表</returns>
    public static List<ChunkResult> CompactAdjacentChunks(IReadOnlyList<ChunkResult> chunks, int maxGapLines = 2)
    {
        if (chunks == null || chunks.Count <= 1)
        {
            return chunks?.ToList() ?? new List<ChunkResult>();
        }

        var result = new List<ChunkResult>();
        // 按文件分组，同时保留第一次出现时的物理顺序
        var grouped = new Dictionary<string, List<ChunkResult>>(StringComparer.OrdinalIgnoreCase);
        var groupKeysInOrder = new List<string>();

        foreach (var chunk in chunks)
        {
            var key = !string.IsNullOrWhiteSpace(chunk.FileId)
                ? chunk.FileId
                : (!string.IsNullOrWhiteSpace(chunk.FilePath) ? chunk.FilePath : Guid.NewGuid().ToString("N"));

            if (!grouped.TryGetValue(key, out var list))
            {
                list = new List<ChunkResult>();
                grouped[key] = list;
                groupKeysInOrder.Add(key);
            }
            list.Add(chunk);
        }

        foreach (var key in groupKeysInOrder)
        {
            var list = grouped[key];
            if (list.Count == 1)
            {
                result.Add(list[0]);
                continue;
            }

            // 检查是否有有效行号
            bool hasValidLines = list.All(c => c.StartLine > 0 && c.EndLine >= c.StartLine);
            if (!hasValidLines)
            {
                // 无有效行号信息时不进行跨分块缝合，避免语义混乱
                result.AddRange(list);
                continue;
            }

            // 按行号升序排列依次扫描可合并区间
            var sortedByLine = list.OrderBy(c => c.StartLine).ToList();
            var currentMerged = sortedByLine[0];

            for (int i = 1; i < sortedByLine.Count; i++)
            {
                var next = sortedByLine[i];
                // 若后一块的起始行 <= 前一块的结束行 + 容差 + 1，则视为连续或重叠区间
                if (next.StartLine <= currentMerged.EndLine + maxGapLines + 1)
                {
                    var newStart = Math.Min(currentMerged.StartLine, next.StartLine);
                    var newEnd = Math.Max(currentMerged.EndLine, next.EndLine);
                    var newContent = MergeContent(currentMerged.Content, next.Content);
                    var newScore = Math.Max(currentMerged.Score, next.Score);
                    var heading = currentMerged.HeadingPath ?? next.HeadingPath;

                    currentMerged = new ChunkResult
                    {
                        RefId = currentMerged.RefId,
                        FileId = currentMerged.FileId,
                        FilePath = currentMerged.FilePath,
                        Source = currentMerged.Source,
                        Title = currentMerged.Title,
                        Content = newContent,
                        Score = newScore,
                        BM25Score = currentMerged.BM25Score,
                        EmbeddingScore = currentMerged.EmbeddingScore,
                        RerankScore = currentMerged.RerankScore,
                        StartLine = newStart,
                        EndLine = newEnd,
                        HeadingPath = heading,
                        Locator = currentMerged.Locator ?? next.Locator,
                        ObsidianLink = SearchResultAdapter.FormatObsidianLink(currentMerged.FilePath, newStart, newEnd, currentMerged.Title)
                    };
                }
                else
                {
                    result.Add(currentMerged);
                    currentMerged = next;
                }
            }
            result.Add(currentMerged);
        }

        // 最终保持按相关性分数（Score）降序排列
        return result.OrderByDescending(r => r.Score).ToList();
    }

    private static string MergeContent(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first)) return second;
        if (string.IsNullOrWhiteSpace(second)) return first;
        if (first.Contains(second, StringComparison.Ordinal)) return first;
        if (second.Contains(first, StringComparison.Ordinal)) return second;

        return first.TrimEnd() + "\n\n" + second.TrimStart();
    }
}
