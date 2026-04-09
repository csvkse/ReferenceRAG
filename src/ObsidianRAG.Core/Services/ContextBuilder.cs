using ObsidianRAG.Core.Interfaces;
using ObsidianRAG.Core.Models;

namespace ObsidianRAG.Core.Services;

/// <summary>
/// 上下文构建器 - 智能组装检索上下文
/// </summary>
public class ContextBuilder
{
    private readonly IVectorStore _vectorStore;
    private readonly ITokenizer _tokenizer;
    private readonly ObsidianLinkGenerator _linkGenerator;

    public ContextBuilder(IVectorStore vectorStore, ITokenizer tokenizer, ObsidianLinkGenerator linkGenerator)
    {
        _vectorStore = vectorStore;
        _tokenizer = tokenizer;
        _linkGenerator = linkGenerator;
    }

    /// <summary>
    /// 构建上下文 - 带上下文窗口
    /// </summary>
    public async Task<string> BuildContextAsync(
        List<SearchResult> results,
        int contextWindow,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        var contextParts = new List<string>();
        var currentTokens = 0;

        foreach (var result in results)
        {
            // 获取扩展上下文
            var expandedContent = await GetExpandedContentAsync(result, contextWindow, cancellationToken);
            var tokens = _tokenizer.CountTokens(expandedContent);

            if (currentTokens + tokens > maxTokens)
            {
                // 超出限制，截断
                var remaining = maxTokens - currentTokens;
                if (remaining > 100)
                {
                    var truncated = TruncateContent(expandedContent, remaining);
                    contextParts.Add(truncated);
                }
                break;
            }

            contextParts.Add(expandedContent);
            currentTokens += tokens;
        }

        return FormatContext(contextParts);
    }

    /// <summary>
    /// 获取扩展内容
    /// </summary>
    private async Task<string> GetExpandedContentAsync(
        SearchResult result,
        int contextWindow,
        CancellationToken cancellationToken)
    {
        if (contextWindow <= 0)
        {
            return FormatSingleResult(result);
        }

        // 获取相邻分段
        var chunks = await _vectorStore.GetChunksByFileAsync(result.FileId, cancellationToken);
        var chunkList = chunks.ToList();
        var currentChunk = chunkList.FirstOrDefault(c => c.Id == result.ChunkId);

        if (currentChunk == null)
        {
            return FormatSingleResult(result);
        }

        var currentIndex = chunkList.IndexOf(currentChunk);
        var startIndex = Math.Max(0, currentIndex - contextWindow);
        var endIndex = Math.Min(chunkList.Count - 1, currentIndex + contextWindow);

        var expandedChunks = chunkList.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();

        // 组装内容
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📍 **来源**: {result.FilePath}");
        
        if (!string.IsNullOrEmpty(result.HeadingPath))
        {
            sb.AppendLine($"📑 **章节**: {result.HeadingPath}");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var chunk in expandedChunks)
        {
            if (chunk.Id == result.ChunkId)
            {
                // 高亮主要匹配
                sb.AppendLine($"> {chunk.Content}");
            }
            else
            {
                sb.AppendLine(chunk.Content);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化单个结果
    /// </summary>
    private string FormatSingleResult(SearchResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📍 **来源**: {result.FilePath} (L{result.StartLine}-L{result.EndLine})");
        
        if (!string.IsNullOrEmpty(result.HeadingPath))
        {
            sb.AppendLine($"📑 **章节**: {result.HeadingPath}");
        }

        sb.AppendLine();
        sb.AppendLine(result.Content);

        return sb.ToString();
    }

    /// <summary>
    /// 格式化上下文
    /// </summary>
    private string FormatContext(List<string> parts)
    {
        return string.Join("\n\n---\n\n", parts);
    }

    /// <summary>
    /// 截断内容
    /// </summary>
    private string TruncateContent(string content, int maxTokens)
    {
        // 按句子截断
        var sentences = content.Split(new[] { '。', '！', '？', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new System.Text.StringBuilder();
        var currentTokens = 0;

        foreach (var sentence in sentences)
        {
            var tokens = _tokenizer.CountTokens(sentence);
            if (currentTokens + tokens > maxTokens)
            {
                break;
            }

            result.Append(sentence);
            result.Append('。');
            currentTokens += tokens;
        }

        return result.ToString();
    }

    /// <summary>
    /// 构建引用列表
    /// </summary>
    public List<ChunkResult> BuildReferences(List<SearchResult> results)
    {
        return results.Select((r, i) => new ChunkResult
        {
            RefId = $"@{i + 1}",
            FileId = r.FileId,
            FilePath = r.FilePath,
            Title = r.Title,
            Content = r.Content,
            Score = r.Score,
            StartLine = r.StartLine,
            EndLine = r.EndLine,
            HeadingPath = r.HeadingPath,
            ObsidianLink = _linkGenerator.GenerateLink(r.FilePath, r.StartLine, r.EndLine)
        }).ToList();
    }

}
