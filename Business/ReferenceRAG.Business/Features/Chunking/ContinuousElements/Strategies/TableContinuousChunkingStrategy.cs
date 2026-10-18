using System.Text;
using ReferenceRAG.Business.Features.Chunking.ContinuousElements.Models;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Chunking.ContinuousElements.Strategies;

/// <summary>
/// 表格专属切分策略：表头全量传承 (Header Carryover) + 行级对齐 (Row-Aligned) + 数据行零重叠 (Zero Row Overlap)
/// </summary>
public class TableContinuousChunkingStrategy : IContinuousChunkingStrategy
{
    public ChunkType SupportedType => ChunkType.Table;

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
        var table = (TableElement)element;
        var results = new List<ChunkRecord>();
        var localChunkIndex = chunkIndexCounter;

        // 构建固定的表头字符串模板
        var headerTemplate = string.Join("\n", table.HeaderRows) + "\n" + table.SeparatorRow;
        var headerTokens = tokenizer?.CountTokens(headerTemplate) ?? TokenEstimator.EstimateTokens(headerTemplate);

        // 如果配置的 MaxTokens 比表头还小，强制动态扩展至表头大小 + 至少 30 tokens 数据行空间
        var effectiveMaxTokens = Math.Max(options.MaxTokens, headerTokens + 30);
        var dataRowBudget = effectiveMaxTokens - headerTokens;

        var currentBuffer = new List<TableRowItem>();
        var currentBufferTokens = 0;
        var startLine = table.StartLine;

        void EmitCurrentChunk(bool isLast)
        {
            if (currentBuffer.Count == 0) return;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(table.PrecedingContext))
            {
                sb.AppendLine(results.Count == 0 
                    ? table.PrecedingContext 
                    : table.PrecedingContext + " (续)");
            }

            sb.AppendLine(headerTemplate);
            foreach (var row in currentBuffer)
            {
                sb.AppendLine(row.RawContent);
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
                ChunkType = ChunkType.Table
            });

            currentBuffer.Clear();
            currentBufferTokens = 0;
            if (!isLast)
            {
                startLine = endLine + 1;
            }
        }

        foreach (var row in table.DataRows)
        {
            var rowTokens = row.EstimatedTokens > 0 
                ? row.EstimatedTokens 
                : (tokenizer?.CountTokens(row.RawContent) ?? TokenEstimator.EstimateTokens(row.RawContent));

            // 如果单行自身极其巨大超过整个数据预算，先落盘已累加的行，再单独将该超大行处理
            if (rowTokens > dataRowBudget)
            {
                EmitCurrentChunk(isLast: false);
                currentBuffer.Add(row);
                EmitCurrentChunk(isLast: false);
                continue;
            }

            if (currentBufferTokens + rowTokens > dataRowBudget && currentBuffer.Count > 0)
            {
                EmitCurrentChunk(isLast: false);
            }

            currentBuffer.Add(row);
            currentBufferTokens += rowTokens;
        }

        EmitCurrentChunk(isLast: true);
        chunkIndexCounter = localChunkIndex;
        return results;
    }
}
