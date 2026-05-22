using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Core.Services.Search;

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
            expandedResults.Add(await ExpandSingleResultAsync(result, window, cancellationToken));
        }

        _logger?.LogDebug("Context expansion done: {Count} results, window +/-{Window}",
            expandedResults.Count, window);

        return expandedResults;
    }

    private async Task<ContextualSearchResult> ExpandSingleResultAsync(
        SearchResult result,
        int windowSize,
        CancellationToken cancellationToken)
    {
        var contextChunks = (await _vectorStore.GetAdjacentChunksByFileAsync(
            result.FileId,
            result.ChunkId,
            windowSize,
            cancellationToken)).ToList();

        if (contextChunks.Count == 0)
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

        var hitIndex = contextChunks.FindIndex(c => c.Id == result.ChunkId);
        if (hitIndex < 0) hitIndex = 0;

        var expandedContent = _options.MergeStrategy switch
        {
            ContextMergeStrategy.Concatenate => string.Join("\n\n", contextChunks.Select(c => c.Content)),
            ContextMergeStrategy.WithSeparator => string.Join(_options.Separator, contextChunks.Select(c => c.Content)),
            ContextMergeStrategy.SmartMerge => SmartMergeContent(contextChunks, hitIndex),
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
                WindowStart = 0,
                WindowEnd = Math.Max(0, contextChunks.Count - 1),
                TotalChunks = contextChunks.Count,
                PreContextChunks = hitIndex,
                PostContextChunks = Math.Max(0, contextChunks.Count - hitIndex - 1)
            },
            ContextChunks = _options.IncludeContextChunks ? contextChunks : null
        };
    }

    private string SmartMergeContent(List<ChunkRecord> chunks, int hitRelativeIndex)
    {
        var sb = new System.Text.StringBuilder();
        string? lastHeading = null;

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var content = chunk.Content.Trim();
            if (string.IsNullOrEmpty(content)) continue;

            if (!string.IsNullOrEmpty(chunk.HeadingPath) && chunk.HeadingPath != lastHeading)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine($"### {chunk.HeadingPath}");
                lastHeading = chunk.HeadingPath;
            }

            if (i == hitRelativeIndex && _options.HighlightHitChunk)
                sb.AppendLine($"**[HIT]** {content}");
            else
                sb.AppendLine(content);
        }

        return sb.ToString();
    }

    public async Task<List<ContextualSearchResult>> ExpandBatchAsync(
        IEnumerable<SearchResult> results,
        int? windowSize = null,
        CancellationToken cancellationToken = default)
    {
        var window = windowSize ?? _options.DefaultWindowSize;
        var expandedResults = new List<ContextualSearchResult>();

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            expandedResults.Add(await ExpandSingleResultAsync(result, window, cancellationToken));
        }

        return expandedResults;
    }
}

public class ContextualSearchResult
{
    public string ChunkId { get; set; } = string.Empty;
    public string FileId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? ExpandedContent { get; set; }
    public float Score { get; set; }
    public string? Source { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string? HeadingPath { get; set; }
    public bool IsContextExpanded { get; set; }
    public ContextRange? ContextRange { get; set; }
    public List<ChunkRecord>? ContextChunks { get; set; }
}

public class ContextRange
{
    public int HitChunkIndex { get; set; }
    public int WindowStart { get; set; }
    public int WindowEnd { get; set; }
    public int TotalChunks { get; set; }
    public int PreContextChunks { get; set; }
    public int PostContextChunks { get; set; }
}

public class ContextualRetrieverOptions
{
    public int DefaultWindowSize { get; set; } = 1;
    public ContextMergeStrategy MergeStrategy { get; set; } = ContextMergeStrategy.SmartMerge;
    public string Separator { get; set; } = "\n\n---\n\n";
    public bool HighlightHitChunk { get; set; } = true;
    public bool IncludeContextChunks { get; set; } = false;
}

public enum ContextMergeStrategy
{
    Concatenate,
    WithSeparator,
    SmartMerge
}
