using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Infrastructure.Features.Search.Adapters;

/// <summary>
/// 搜索结果规范化适配器（Adapter Pattern）
/// 统一屏蔽底层存储（KnowledgeBase、VectorStore、BM25、Cognitive）的模型差异，保证位置与元数据完整透传
/// </summary>
public static class SearchResultAdapter
{
    /// <summary>
    /// 将知识库召回项适配为标准 SearchResult
    /// </summary>
    public static SearchResult FromRecallItem(RecallItem item, float score)
    {
        ArgumentNullException.ThrowIfNull(item);

        var title = Path.GetFileNameWithoutExtension(item.SourceKey);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = item.SourceKey;
        }

        return new SearchResult
        {
            ChunkId = item.ChunkId,
            FileId = item.DocumentId,
            FilePath = item.SourceKey,
            Source = item.ConnectorId ?? "knowledge-base",
            Title = title,
            Content = item.Text,
            Score = score,
            BM25Score = item.ScoreKind == ScoreKind.BM25 ? (float)item.Score : score,
            EmbeddingScore = item.ScoreKind == ScoreKind.Cosine ? (float)item.Score : 0f,
            StartLine = item.StartLine,
            EndLine = item.EndLine,
            HeadingPath = item.HeadingPath,
            Locator = item.Locator
        };
    }

    /// <summary>
    /// 将底层 ChunkRecord + FileRecord 适配为标准 SearchResult
    /// </summary>
    public static SearchResult FromChunkRecord(
        ChunkRecord chunk,
        FileRecord file,
        float score,
        float? bm25Score = null,
        float? embeddingScore = null)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(file);

        return new SearchResult
        {
            ChunkId = chunk.Id,
            FileId = file.Id,
            FilePath = file.Path,
            Source = file.Source,
            Title = file.Title ?? file.FileName,
            Content = chunk.Content,
            Score = score,
            BM25Score = bm25Score ?? 0f,
            EmbeddingScore = embeddingScore ?? 0f,
            StartLine = chunk.StartLine,
            EndLine = chunk.EndLine,
            HeadingPath = chunk.HeadingPath,
            Level = chunk.Level,
            ChildChunkCount = chunk.ChildChunkCount,
            AggregateType = chunk.AggregateType
        };
    }

    /// <summary>
    /// 将标准 SearchResult 转换为对外暴露的 ChunkResult
    /// </summary>
    public static ChunkResult ToChunkResult(SearchResult r, string? customLink = null)
    {
        ArgumentNullException.ThrowIfNull(r);

        var link = customLink ?? FormatObsidianLink(r.FilePath, r.StartLine, r.EndLine, r.Title);

        return new ChunkResult
        {
            RefId = r.ChunkId,
            FileId = r.FileId,
            FilePath = r.FilePath,
            Source = r.Source,
            Title = r.Title,
            Content = r.Content,
            Score = r.Score,
            BM25Score = r.BM25Score > 0 ? r.BM25Score : null,
            EmbeddingScore = r.EmbeddingScore > 0 ? r.EmbeddingScore : null,
            RerankScore = r.RerankScore,
            StartLine = r.StartLine,
            EndLine = r.EndLine,
            HeadingPath = r.HeadingPath,
            Locator = r.Locator,
            ObsidianLink = link
        };
    }

    /// <summary>
    /// 格式化 Obsidian 链接，彻底杜绝 #L0-L0 缺陷
    /// </summary>
    public static string FormatObsidianLink(string filePath, int startLine, int endLine, string? title = null)
    {
        var rawPath = (filePath ?? string.Empty).Replace('\\', '/');
        var fileName = Path.GetFileNameWithoutExtension(rawPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = rawPath;
        }

        // 行号未知或非法时优雅回退，严禁生成 #L0 或 #L0-L0
        if (startLine <= 0)
        {
            return !string.IsNullOrWhiteSpace(title) && !string.Equals(title, fileName, StringComparison.OrdinalIgnoreCase)
                ? $"[[{fileName}|{title}]]"
                : $"[[{fileName}]]";
        }

        var anchor = (startLine == endLine || endLine <= 0) ? $"#L{startLine}" : $"#L{startLine}-L{endLine}";

        return !string.IsNullOrWhiteSpace(title) && !string.Equals(title, fileName, StringComparison.OrdinalIgnoreCase)
            ? $"[[{fileName}{anchor}|{title}]]"
            : $"[[{fileName}{anchor}]]";
    }
}
