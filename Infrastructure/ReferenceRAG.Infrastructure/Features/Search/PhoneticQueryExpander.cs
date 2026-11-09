namespace ReferenceRAG.Core.Features.Search;

/// <summary>
/// 词法近音词、同音错别字与领域同义词扩展器 (Phonetic & Typo Query Expander)
/// 消除人类探索未知知识库时，输入法同音错别字、口语缩写导致的词法检索断层。
/// </summary>
public static class PhoneticQueryExpander
{
    private static readonly Dictionary<string, string[]> HomophoneAndSynonymMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // 常见输入法同音错字与倒笔错字
        ["月女剑"] = new[] { "越女剑" },
        ["月女"] = new[] { "越女" },
        ["放洋"] = new[] { "放羊" },
        ["连成决"] = new[] { "连城诀" },
        ["连城决"] = new[] { "连城诀" },
        ["狄芸"] = new[] { "狄云" },
        ["神照金"] = new[] { "神照经" },
        ["白工公"] = new[] { "白公公" },
        ["苗仁凤"] = new[] { "苗人凤" },
        ["胡一刀"] = new[] { "胡一刀" },
        ["田归农"] = new[] { "田归农" },
        ["雪山飞狐"] = new[] { "雪山飞狐" },

        // 场景口语泛化映射 (针对人类未知探索认知断层)
        ["起义领袖"] = new[] { "李自成", "闯王" },
        ["私房钱"] = new[] { "宝藏" },
        ["保镖"] = new[] { "侍卫" },
        ["真气"] = new[] { "神功", "神照经", "内家真气" },
        ["细竹竿"] = new[] { "竹棒", "细竹棒" },
        ["戳瞎"] = new[] { "刺瞎", "眼睛" },
        ["名剑锻造"] = new[] { "铸剑", "纯钧", "工布", "欧冶子", "薛烛" },
        ["北方雪山"] = new[] { "雪山之巅", "玉笔峰", "胡一刀", "苗人凤" },
        ["暗下剧毒"] = new[] { "剧毒", "刀口涂毒", "田归农" },
    };

    /// <summary>
    /// 从查询文本中检测音近错字与领域泛化词，返回补充的候选词项
    /// </summary>
    public static IReadOnlyList<string> GetExpansionTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();

        var expansions = new List<string>();
        foreach (var (phrase, targets) in HomophoneAndSynonymMap)
        {
            if (query.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            {
                expansions.AddRange(targets);
            }
        }
        return expansions;
    }

    /// <summary>
    /// 将原查询改写/扩充为包含纠错词项的增强查询字符串
    /// </summary>
    public static string ExpandQueryString(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return query;
        var expansions = GetExpansionTerms(query);
        if (expansions.Count == 0) return query;

        return $"{query} {string.Join(" ", expansions)}";
    }
}
