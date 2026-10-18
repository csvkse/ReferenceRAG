using ReferenceRAG.Core.Models;

namespace ReferenceRAG.Business.Features.Indexing.Adapters;

/// <summary>
/// 信息密度准入适配器：拦截有效字符极少、纯标点、无语义超短碎片
/// </summary>
public class InformationDensityAdapter : IChunkQualificationAdapter
{
    public string AdapterName => "InformationDensityAdapter";
    public int Priority => 20; // 次高优先级

    private const int MinMeaningfulChars = 6;

    public ChunkQualificationResult Evaluate(ChunkRecord chunk, ChunkEvaluationContext context)
    {
        var text = chunk.Content?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return ChunkQualificationResult.Unqualified(
                "分块无文本",
                NoiseCategory.PunctuationOnly,
                IngestionActionType.Discard);
        }

        // 若分块包含有效标题路径或以 Markdown 标题开头，允许短标题保留
        if (!string.IsNullOrEmpty(chunk.HeadingPath) || text.StartsWith('#'))
        {
            return ChunkQualificationResult.Qualified();
        }

        int meaningfulCharCount = 0;
        int punctuationOrWhitespaceCount = 0;

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || IsCjk(ch))
            {
                meaningfulCharCount++;
            }
            else if (char.IsPunctuation(ch) || char.IsWhiteSpace(ch) || char.IsSymbol(ch))
            {
                punctuationOrWhitespaceCount++;
            }
        }

        // 纯标点或符号
        if (meaningfulCharCount == 0)
        {
            var action = (context.HasPreviousChunk || context.HasNextChunk)
                ? IngestionActionType.AbsorbToAdjacent
                : IngestionActionType.Discard;

            return ChunkQualificationResult.Unqualified(
                "分块不含任何自然语言文字或数字",
                NoiseCategory.PunctuationOnly,
                action);
        }

        // 有效信息量过低（文字数 < 6 且非标题）
        if (meaningfulCharCount < MinMeaningfulChars)
        {
            var action = (context.HasPreviousChunk || context.HasNextChunk)
                ? IngestionActionType.AbsorbToAdjacent
                : IngestionActionType.Discard;

            return ChunkQualificationResult.Unqualified(
                $"有效字符数 ({meaningfulCharCount}) 低于阈值 ({MinMeaningfulChars})",
                NoiseCategory.LowDensityFragment,
                action);
        }

        return ChunkQualificationResult.Qualified();
    }

    private static bool IsCjk(char ch)
    {
        // 覆盖 CJK 统一表意文字基本区与扩展 A 区
        return (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF);
    }
}
