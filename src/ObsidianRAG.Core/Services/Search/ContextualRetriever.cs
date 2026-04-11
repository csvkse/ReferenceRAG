using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;
using Microsoft.Extensions.Logging;

namespace ObsidianRAG.Core.Services.Search;

/// <summary>
/// 上下文检索器：向量命中后扩展返回 ±N 节窗口
/// 解决分段后语义完整性问题，提供更完整的上下文
/// </summary>
public class ContextualRetriever
{
    private readonly IVectorStore _vectorStore;
    private readonly ContextualRetrieverOptions _options;
    private readonly ILogger<ContextualRetriever>? _logger;

    public ContextualRetriever(
        IVectorStore vectorStore,
        ContextualRetrieverOptions? options = null,
        ILogger<ContextualRetriever>? logger = null)
    {
        _vectorStore = vectorStore;
        _options = options ?? new ContextualRetrieverOptions();
        _logger = logger;
    }

    /// <summary>
    /// 扩展搜索结果的上下文窗口
    /// </summary>
    /// <param name="results">原始搜索结果</param>
    /// <param name="windowSize">窗口大小（±N 节），null 使用默认配置</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>扩展后的搜索结果</returns>
    public async Task<List<ContextualSearchResult>> ExpandAsync(
        IEnumerable<SearchResult> results,
        int? windowSize = null,
        CancellationToken cancellationToken = default)
    {
        var window = windowSize ?? _options.DefaultWindowSize;
        var expandedResults = new List<ContextualSearchResult>();

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var expanded = await ExpandSingleResultAsync(result, window, cancellationToken);
            expandedResults.Add(expanded);
        }

        _logger?.LogDebug("上下文扩展完成: 原始 {Original} 个结果, 窗口大小 ±{Window}",
            expandedResults.Count, window);

        return expandedResults;
    }

    /// <summary>
    /// 扩展单个搜索结果的上下文
    /// </summary>
    private async Task<ContextualSearchResult> ExpandSingleResultAsync(
        SearchResult result,
        int windowSize,
        CancellationToken cancellationToken)
    {
        // 获取同文件的所有分段
        var allChunks = await _vectorStore.GetChunksByFileAsync(result.FileId);
        var orderedChunks = allChunks
            .OrderBy(c => c.ChunkOrder)  // 使用小数排序值
            .ThenBy(c => c.ChunkIndex)   // 回退到 ChunkIndex
            .ToList();

        // 找到命中分段的索引
        var hitIndex = orderedChunks.FindIndex(c => c.Id == result.ChunkId);

        if (hitIndex < 0)
        {
            // 未找到，返回原始结果
            return new ContextualSearchResult
            {
                ChunkId = result.ChunkId,
                FileId = result.FileId,
                FilePath = result.FilePath,
                Title = result.Title,
                Content = result.Content,
                Score = result.Score,
                Source = result.Source,
                StartLine = result.StartLine,
                EndLine = result.EndLine,
                HeadingPath = result.HeadingPath,
                IsContextExpanded = false
            };
        }

        // 计算窗口范围
        var startIndex = Math.Max(0, hitIndex - windowSize);
        var endIndex = Math.Min(orderedChunks.Count - 1, hitIndex + windowSize);

        // 构建扩展上下文
        var contextChunks = orderedChunks
            .Skip(startIndex)
            .Take(endIndex - startIndex + 1)
            .ToList();

        // 合并内容
        var expandedContent = _options.MergeStrategy switch
        {
            ContextMergeStrategy.Concatenate => string.Join("\n\n", contextChunks.Select(c => c.Content)),
            ContextMergeStrategy.WithSeparator => string.Join(_options.Separator, contextChunks.Select(c => c.Content)),
            ContextMergeStrategy.SmartMerge => SmartMergeContent(contextChunks, hitIndex - startIndex),
            _ => string.Join("\n\n", contextChunks.Select(c => c.Content))
        };

        // 构建扩展范围信息
        var contextRange = new ContextRange
        {
            HitChunkIndex = hitIndex,
            WindowStart = startIndex,
            WindowEnd = endIndex,
            TotalChunks = orderedChunks.Count,
            PreContextChunks = hitIndex - startIndex,
            PostContextChunks = endIndex - hitIndex
        };

        return new ContextualSearchResult
        {
            ChunkId = result.ChunkId,
            FileId = result.FileId,
            FilePath = result.FilePath,
            Title = result.Title,
            Content = result.Content,          // 原始命中内容
            ExpandedContent = expandedContent, // 扩展后的完整上下文
            Score = result.Score,
            Source = result.Source,
            StartLine = contextChunks.First().StartLine,  // 扩展后的行范围
            EndLine = contextChunks.Last().EndLine,
            HeadingPath = result.HeadingPath,
            IsContextExpanded = contextChunks.Count > 1,
            ContextRange = contextRange,
            ContextChunks = _options.IncludeContextChunks ? contextChunks : null
        };
    }

    /// <summary>
    /// 智能合并内容：保留段落结构，避免重复标题
    /// </summary>
    private string SmartMergeContent(List<ChunkRecord> chunks, int hitRelativeIndex)
    {
        var sb = new System.Text.StringBuilder();
        string? lastHeading = null;

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var content = chunk.Content.Trim();

            // 跳过空内容
            if (string.IsNullOrEmpty(content)) continue;

            // 处理标题重复
            if (!string.IsNullOrEmpty(chunk.HeadingPath) && chunk.HeadingPath != lastHeading)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine($"### {chunk.HeadingPath}");
                lastHeading = chunk.HeadingPath;
            }

            // 高亮命中分段
            if (i == hitRelativeIndex && _options.HighlightHitChunk)
            {
                sb.AppendLine($"**[命中]** {content}");
            }
            else
            {
                sb.AppendLine(content);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 批量扩展多个搜索结果（支持去重）
    /// </summary>
    public async Task<List<ContextualSearchResult>> ExpandBatchAsync(
        IEnumerable<SearchResult> results,
        int? windowSize = null,
        CancellationToken cancellationToken = default)
    {
        var window = windowSize ?? _options.DefaultWindowSize;
        var expandedResults = new List<ContextualSearchResult>();
        var processedFiles = new HashSet<string>();

        // 按文件分组，避免重复加载同一文件的分段
        var groupedResults = results.GroupBy(r => r.FileId);

        foreach (var fileGroup in groupedResults)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileId = fileGroup.Key;
            var fileResults = fileGroup.ToList();

            // 获取该文件的所有分段（只加载一次）
            var allChunks = await _vectorStore.GetChunksByFileAsync(fileId);
            var orderedChunks = allChunks
                .OrderBy(c => c.ChunkOrder)
                .ThenBy(c => c.ChunkIndex)
                .ToList();

            // 处理该文件中的每个命中结果
            foreach (var result in fileResults)
            {
                var expanded = ExpandWithPreloadedChunks(result, orderedChunks, window);
                expandedResults.Add(expanded);
            }
        }

        return expandedResults;
    }

    /// <summary>
    /// 使用预加载的分段扩展上下文
    /// </summary>
    private ContextualSearchResult ExpandWithPreloadedChunks(
        SearchResult result,
        List<ChunkRecord> orderedChunks,
        int windowSize)
    {
        var hitIndex = orderedChunks.FindIndex(c => c.Id == result.ChunkId);

        if (hitIndex < 0)
        {
            return new ContextualSearchResult
            {
                ChunkId = result.ChunkId,
                FileId = result.FileId,
                FilePath = result.FilePath,
                Title = result.Title,
                Content = result.Content,
                Score = result.Score,
                Source = result.Source,
                StartLine = result.StartLine,
                EndLine = result.EndLine,
                HeadingPath = result.HeadingPath,
                IsContextExpanded = false
            };
        }

        var startIndex = Math.Max(0, hitIndex - windowSize);
        var endIndex = Math.Min(orderedChunks.Count - 1, hitIndex + windowSize);

        var contextChunks = orderedChunks
            .Skip(startIndex)
            .Take(endIndex - startIndex + 1)
            .ToList();

        var expandedContent = _options.MergeStrategy switch
        {
            ContextMergeStrategy.Concatenate => string.Join("\n\n", contextChunks.Select(c => c.Content)),
            ContextMergeStrategy.WithSeparator => string.Join(_options.Separator, contextChunks.Select(c => c.Content)),
            ContextMergeStrategy.SmartMerge => SmartMergeContent(contextChunks, hitIndex - startIndex),
            _ => string.Join("\n\n", contextChunks.Select(c => c.Content))
        };

        return new ContextualSearchResult
        {
            ChunkId = result.ChunkId,
            FileId = result.FileId,
            FilePath = result.FilePath,
            Title = result.Title,
            Content = result.Content,
            ExpandedContent = expandedContent,
            Score = result.Score,
            Source = result.Source,
            StartLine = contextChunks.First().StartLine,
            EndLine = contextChunks.Last().EndLine,
            HeadingPath = result.HeadingPath,
            IsContextExpanded = contextChunks.Count > 1,
            ContextRange = new ContextRange
            {
                HitChunkIndex = hitIndex,
                WindowStart = startIndex,
                WindowEnd = endIndex,
                TotalChunks = orderedChunks.Count,
                PreContextChunks = hitIndex - startIndex,
                PostContextChunks = endIndex - hitIndex
            }
        };
    }
}

/// <summary>
/// 上下文搜索结果
/// </summary>
public class ContextualSearchResult
{
    /// <summary>
    /// 命中分段 ID
    /// </summary>
    public string ChunkId { get; set; } = string.Empty;

    /// <summary>
    /// 文件 ID
    /// </summary>
    public string FileId { get; set; } = string.Empty;

    /// <summary>
    /// 文件路径
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// 标题
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// 原始命中内容
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 扩展后的完整上下文内容
    /// </summary>
    public string? ExpandedContent { get; set; }

    /// <summary>
    /// 搜索分数
    /// </summary>
    public float Score { get; set; }

    /// <summary>
    /// 数据源
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// 扩展后的起始行号
    /// </summary>
    public int StartLine { get; set; }

    /// <summary>
    /// 扩展后的结束行号
    /// </summary>
    public int EndLine { get; set; }

    /// <summary>
    /// 章节路径
    /// </summary>
    public string? HeadingPath { get; set; }

    /// <summary>
    /// 是否扩展了上下文
    /// </summary>
    public bool IsContextExpanded { get; set; }

    /// <summary>
    /// 上下文范围信息
    /// </summary>
    public ContextRange? ContextRange { get; set; }

    /// <summary>
    /// 上下文分段列表（可选）
    /// </summary>
    public List<ChunkRecord>? ContextChunks { get; set; }
}

/// <summary>
/// 上下文范围信息
/// </summary>
public class ContextRange
{
    /// <summary>
    /// 命中分段在文件中的索引
    /// </summary>
    public int HitChunkIndex { get; set; }

    /// <summary>
    /// 窗口起始索引
    /// </summary>
    public int WindowStart { get; set; }

    /// <summary>
    /// 窗口结束索引
    /// </summary>
    public int WindowEnd { get; set; }

    /// <summary>
    /// 文件总分段数
    /// </summary>
    public int TotalChunks { get; set; }

    /// <summary>
    /// 命中前的上下文分段数
    /// </summary>
    public int PreContextChunks { get; set; }

    /// <summary>
    /// 命中后的上下文分段数
    /// </summary>
    public int PostContextChunks { get; set; }
}

/// <summary>
/// 上下文检索器配置
/// </summary>
public class ContextualRetrieverOptions
{
    /// <summary>
    /// 默认窗口大小（±N 节）
    /// </summary>
    public int DefaultWindowSize { get; set; } = 1;

    /// <summary>
    /// 内容合并策略
    /// </summary>
    public ContextMergeStrategy MergeStrategy { get; set; } = ContextMergeStrategy.SmartMerge;

    /// <summary>
    /// 分隔符（仅 WithSeparator 策略使用）
    /// </summary>
    public string Separator { get; set; } = "\n\n---\n\n";

    /// <summary>
    /// 是否高亮命中分段
    /// </summary>
    public bool HighlightHitChunk { get; set; } = true;

    /// <summary>
    /// 是否在结果中包含上下文分段列表
    /// </summary>
    public bool IncludeContextChunks { get; set; } = false;
}

/// <summary>
/// 上下文合并策略
/// </summary>
public enum ContextMergeStrategy
{
    /// <summary>
    /// 直接拼接（用双换行分隔）
    /// </summary>
    Concatenate,

    /// <summary>
    /// 使用自定义分隔符
    /// </summary>
    WithSeparator,

    /// <summary>
    /// 智能合并（保留段落结构，去重标题）
    /// </summary>
    SmartMerge
}
