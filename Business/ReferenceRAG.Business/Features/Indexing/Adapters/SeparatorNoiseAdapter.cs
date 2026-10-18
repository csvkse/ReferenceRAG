using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Indexing.Adapters;

/// <summary>
/// 纯分隔符与页码标记准入适配器：拦截 === (43-48) ===, ---, ***, ====== 等无独立语义噪音
/// </summary>
public class SeparatorNoiseAdapter : IChunkQualificationAdapter
{
    public string AdapterName => "SeparatorNoiseAdapter";
    public int Priority => 10; // 高优先级

    public ChunkQualificationResult Evaluate(ChunkRecord chunk, ChunkEvaluationContext context)
    {
        var content = chunk.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return ChunkQualificationResult.Unqualified(
                "分块内容为空",
                NoiseCategory.PunctuationOnly,
                IngestionActionType.Discard);
        }

        if (NoiseChunkDetector.IsNoiseOrSeparator(content))
        {
            bool hasPagination = content.Contains('(') && content.Contains(')') &&
                System.Text.RegularExpressions.Regex.IsMatch(content, @"\d+[\-~至到/,\s]\d+");

            var category = hasPagination ? NoiseCategory.PaginationMarker : NoiseCategory.SeparatorLine;
            var action = (context.HasPreviousChunk || context.HasNextChunk)
                ? IngestionActionType.AbsorbToAdjacent
                : IngestionActionType.Discard;

            return ChunkQualificationResult.Unqualified(
                $"识别为纯分隔符或分页标记 ({category})",
                category,
                action);
        }

        return ChunkQualificationResult.Qualified();
    }
}
