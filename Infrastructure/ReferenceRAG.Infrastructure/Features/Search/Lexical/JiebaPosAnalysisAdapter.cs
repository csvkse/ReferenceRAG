using System.Text.RegularExpressions;
using JiebaNet.Segmenter.PosSeg;

namespace ReferenceRAG.Core.Features.Search.Lexical;

/// <summary>
/// 基于结巴分词的 POS 词性标注与信息熵分析适配器实现
/// 将长句中的问答套话、副词、代词过滤，并根据词性赋予差异化信息熵权重
/// </summary>
public sealed class JiebaPosAnalysisAdapter : IPosAnalysisAdapter
{
    private static readonly Lazy<JiebaPosAnalysisAdapter> _defaultInstance =
        new(() => new JiebaPosAnalysisAdapter());

    public static JiebaPosAnalysisAdapter Default => _defaultInstance.Value;

    private readonly PosSegmenter _posSeg = new();

    // 强实体与专有名词词性 (权重 3.0x)
    private static readonly HashSet<string> EntityPosTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "nr", "ns", "nt", "nz", "nw"
    };

    // 普通名词、名动词与成语 (权重 2.0x)
    private static readonly HashSet<string> NounPosTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "n", "vn", "an", "i"
    };

    // 动作动词与特异动词 (权重 1.5x)
    private static readonly HashSet<string> VerbPosTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "v", "vd"
    };

    // 噪音与无区分度功能词词性 (直接过滤剔除)
    private static readonly HashSet<string> FilterPosTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "r", "d", "c", "p", "u", "y", "e", "w", "o", "m", "q", "f", "s", "t"
    };

    // 口语套话与无信息量提问词
    private static readonly HashSet<string> ConversationalBoilerplate = new(StringComparer.OrdinalIgnoreCase)
    {
        "我想请问一下", "我想请问", "我想了解一下", "我想了解", "我想知道", "我想问", "我想",
        "请问一下", "请问", "请教", "能否告知", "能否告诉我", "你能告诉我", "请告诉我",
        "有没有", "有没有记载", "讲一个", "到底", "到底是在", "到底是在何处", "在何处", "在哪个地方",
        "在哪个", "在哪里", "在哪", "的故事", "的篇章", "武打片段", "片段", "篇章", "记载", "关于",
        "里面", "里头", "那个", "为什么", "怎么", "如何", "什么", "谁", "经历", "发生", "介绍",
        "是什么", "怎么回事", "发生了什么", "有哪些", "有什么"
    };

    public IReadOnlyList<WeightedToken> AnalyzeAndWeight(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<WeightedToken>();
        }

        // 清洗长句中的常见提问模板前缀
        var cleaned = query;
        foreach (var phrase in ConversationalBoilerplate)
        {
            cleaned = cleaned.Replace(phrase, " ");
        }

        var pairs = _posSeg.Cut(cleaned).ToList();
        var weightedTokens = new List<WeightedToken>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in pairs)
        {
            var word = p.Word.Trim();
            var tag = p.Flag ?? string.Empty;

            // 过滤单字符（除非为强专名）、过滤标点和功能词
            if (string.IsNullOrWhiteSpace(word) || word.Length < 2) continue;
            if (FilterPosTags.Contains(tag)) continue;
            if (ConversationalBoilerplate.Contains(word)) continue;
            if (!seen.Add(word)) continue;

            double weight = 1.0;
            bool isEntity = false;

            if (EntityPosTags.Contains(tag))
            {
                weight = 3.0;
                isEntity = true;
            }
            else if (NounPosTags.Contains(tag))
            {
                weight = 2.0;
            }
            else if (VerbPosTags.Contains(tag))
            {
                weight = 1.5;
            }

            weightedTokens.Add(new WeightedToken(word, tag, weight, isEntity));
        }

        return weightedTokens;
    }
}
