using System.Text;
using ReferenceRAG.Business.Features.Chunking.ContinuousElements.Models;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Strategies;

/// <summary>
/// 多级列表树切分策略：祖先面包屑路径传承 (Ancestor Breadcrumb Carryover) + 列表项原子切分
/// </summary>
public class ListTreeContinuousChunkingStrategy : IContinuousChunkingStrategy
{
    public ChunkType SupportedType => ChunkType.List;

    public List<ChunkRecord> Chunk(
        object element,
        string[] allLines,
        ChunkingOptions options,
        ITokenizer? tokenizer,
        string fileId,
        string? headingPath,
        int headingLevel,
        ref int chunkIndexCounter)
    {
        var listTree = (ListTreeElement)element;
        var results = new List<ChunkRecord>();
        var localChunkIndex = chunkIndexCounter;
        var isAdaptive = string.Equals(options.Mode, "Adaptive", StringComparison.OrdinalIgnoreCase);

        var currentBuffer = new List<ListItemNode>();
        var currentBufferTokens = 0;
        var startLine = listTree.StartLine;

        void EmitCurrentChunk(bool isLast)
        {
            if (currentBuffer.Count == 0) return;

            var sb = new StringBuilder();

            // 若在自适应模式且首项具备祖先面包屑路径，注入前置上下文
            if (isAdaptive && currentBuffer[0].AncestorBreadcrumbs.Count > 0)
            {
                var breadcrumbStr = string.Join(" > ", currentBuffer[0].AncestorBreadcrumbs);
                sb.AppendLine($"[列表路径: {breadcrumbStr}]");
            }

            foreach (var item in currentBuffer)
            {
                var indent = new string(' ', item.IndentLevel * 2);
                sb.AppendLine($"{indent}{item.Marker} {item.Content}");
            }

            var chunkContent = sb.ToString().TrimEnd();
            var endLine = currentBuffer.Last().LineNumber;
            var tokenCount = tokenizer?.CountTokens(chunkContent) ?? TokenEstimator.EstimateTokens(chunkContent);

            int endColumn = allLines.Length > 0 && endLine > 0 && endLine <= allLines.Length
                ? allLines[Math.Min(endLine - 1, allLines.Length - 1)].Length
                : 0;

            results.Add(new ChunkRecord
            {
                Id = Guid.NewGuid().ToString(),
                FileId = fileId,
                ChunkIndex = localChunkIndex++,
                Content = chunkContent,
                TokenCount = tokenCount,
                StartLine = startLine,
                EndLine = endLine,
                StartColumn = 1,
                EndColumn = endColumn,
                HeadingPath = headingPath,
                Level = headingLevel,
                Weight = 1.0f,
                ChunkType = ChunkType.List
            });

            currentBuffer.Clear();
            currentBufferTokens = 0;
            if (!isLast)
            {
                startLine = endLine + 1;
            }
        }

        foreach (var item in listTree.Items)
        {
            var itemTokens = item.EstimatedTokens > 0 
                ? item.EstimatedTokens 
                : (tokenizer?.CountTokens(item.Content) ?? TokenEstimator.EstimateTokens(item.Content));

            if (currentBufferTokens + itemTokens > options.MaxTokens && currentBuffer.Count > 0)
            {
                EmitCurrentChunk(isLast: false);
            }

            currentBuffer.Add(item);
            currentBufferTokens += itemTokens;
        }

        EmitCurrentChunk(isLast: true);
        chunkIndexCounter = localChunkIndex;
        return results;
    }
}
