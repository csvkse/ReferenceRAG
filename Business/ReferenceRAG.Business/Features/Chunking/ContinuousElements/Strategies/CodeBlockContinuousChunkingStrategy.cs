using System.Text;
using ReferenceRAG.Business.Features.Chunking.ContinuousElements.Models;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Strategies;

/// <summary>
/// 代码块切分策略：自适应模式下支持围栏智能双向补齐 (Fence Auto-Wrap) 与 (continued) 续接标记；固定模式下保持兼容
/// </summary>
public class CodeBlockContinuousChunkingStrategy : IContinuousChunkingStrategy
{
    public ChunkType SupportedType => ChunkType.Code;

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
        var code = (CodeBlockElement)element;
        var results = new List<ChunkRecord>();
        var localChunkIndex = chunkIndexCounter;
        var isAdaptive = string.Equals(options.Mode, "Adaptive", StringComparison.OrdinalIgnoreCase);

        var lines = code.CodeLines;
        var buffer = new List<string>();
        var bufferStartLine = code.StartLine;
        var nextLine = code.StartLine + 1; // 内部行从开围栏下一行开始

        var openFence = code.OpenFence;
        var closeFence = code.CloseFence;

        void EmitChunk(bool isFirst, bool isLast)
        {
            if (buffer.Count == 0 && !isFirst && !isLast) return;

            var sb = new StringBuilder();

            if (isAdaptive)
            {
                // 自适应模式：双向自动包裹围栏，增强每块的自包含性
                sb.AppendLine(isFirst ? openFence : $"{openFence} (continued)");
                foreach (var line in buffer)
                {
                    sb.AppendLine(line);
                }
                sb.Append(closeFence);
            }
            else
            {
                // 固定模式：保持开围栏在首块、闭围栏在末块，拼接后恰好 2 次围栏，保持与已有测试完全一致
                if (isFirst) sb.AppendLine(openFence);
                foreach (var line in buffer)
                {
                    sb.AppendLine(line);
                }
                if (isLast) sb.Append(closeFence);
            }

            var chunkContent = sb.ToString().TrimEnd();
            var endLine = isLast ? code.EndLine : nextLine - 1;
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
                StartLine = bufferStartLine,
                EndLine = endLine,
                StartColumn = 1,
                EndColumn = endColumn,
                HeadingPath = headingPath,
                Level = headingLevel,
                Weight = 1.0f,
                ChunkType = ChunkType.Code
            });

            buffer.Clear();
            bufferStartLine = nextLine;
        }

        bool isFirstChunk = true;

        for (int i = 0; i < lines.Count; i++)
        {
            var currentLine = lines[i];

            if (buffer.Count > 0)
            {
                // 估算候选块 Token 数
                var candidate = string.Join("\n", buffer) + "\n" + currentLine;
                var candidateTokens = tokenizer?.CountTokens(candidate) ?? TokenEstimator.EstimateTokens(candidate);

                // 在自适应模式下需要预留补齐围栏的 Token 预算 (~15 tokens)
                var budget = isAdaptive ? Math.Max(20, options.MaxTokens - 15) : options.MaxTokens;

                if (candidateTokens > budget)
                {
                    EmitChunk(isFirst: isFirstChunk, isLast: false);
                    isFirstChunk = false;
                }
            }

            buffer.Add(currentLine);
            nextLine++;
        }

        EmitChunk(isFirst: isFirstChunk, isLast: true);
        chunkIndexCounter = localChunkIndex;
        return results;
    }
}
