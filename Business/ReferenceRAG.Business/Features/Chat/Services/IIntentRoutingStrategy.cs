namespace ReferenceRAG.Business.Features.Chat.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ReferenceRAG.Core.Interfaces;

/// <summary>
/// 意图路由决断结果
/// </summary>
public record RoutingDecision(
    bool Handled,
    string? FunctionName,
    IDictionary<string, object?>? Arguments,
    string StrategyName,
    int ConfidenceScore,
    string Reason)
{
    public static RoutingDecision Unhandled(string strategyName, string reason = "未匹配到特定路由意图") =>
        new(false, null, null, strategyName, 0, reason);

    public static RoutingDecision Match(string functionName, IDictionary<string, object?> arguments, string strategyName, int confidenceScore, string reason) =>
        new(true, functionName, arguments, strategyName, confidenceScore, reason);
}

/// <summary>
/// 意图路由策略接口（策略模式 Strategy Pattern）
/// 负责在提问进入大模型前执行确定性/语义先验分流，降低延迟、Token 开销与小模型幻觉。
/// </summary>
public interface IIntentRoutingStrategy
{
    string StrategyName { get; }
    int Priority { get; }

    Task<RoutingDecision> RouteAsync(
        string userText,
        IList<AITool>? availableTools,
        CancellationToken ct = default);
}

/// <summary>
/// 精确前缀与命令路由策略（优先级 100：最高）
/// 支持 '搜索:', '搜索知识库中:', 'BM25:', 'GetIndexStatus' 等明确自然语言与指令前缀，并自愈多行文本。
/// </summary>
public class ExactPrefixRoutingStrategy : IIntentRoutingStrategy
{
    public string StrategyName => "ExactPrefix";
    public int Priority => 100;

    public Task<RoutingDecision> RouteAsync(
        string userText,
        IList<AITool>? availableTools,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText) || availableTools == null || availableTools.Count == 0)
        {
            return Task.FromResult(RoutingDecision.Unhandled(StrategyName, "输入文本为空或无可用工具"));
        }

        var availableFuncs = availableTools.OfType<AIFunction>().ToList();
        if (availableFuncs.Count == 0)
        {
            return Task.FromResult(RoutingDecision.Unhandled(StrategyName, "无 AIFunction 工具定义"));
        }

        var trimmed = userText.Trim();
        string cleanCommand = StripCommandPrefixes(trimmed);

        // 1. 容错结构化 XML / Markdown / JSON 标签包裹的指令
        if (cleanCommand.StartsWith("<tool_call>", StringComparison.OrdinalIgnoreCase) ||
            cleanCommand.StartsWith("{", StringComparison.OrdinalIgnoreCase) ||
            cleanCommand.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = ChatToolCallingHelper.TryParseToolCall(cleanCommand);
            if (parsed.Success && !string.IsNullOrEmpty(parsed.Name))
            {
                var matched = FindMatchingFunction(availableFuncs, parsed.Name);
                if (matched != null)
                {
                    var args = parsed.Arguments ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    return Task.FromResult(RoutingDecision.Match(matched.Name, args, StrategyName, 100, "匹配到显式结构化工具调用标签"));
                }
            }
        }

        // 2. 检查完全匹配函数名（如 'GetIndexStatus', 'GetFileStructure'）
        var matchedExact = FindMatchingFunction(availableFuncs, cleanCommand);
        if (matchedExact != null)
        {
            return Task.FromResult(RoutingDecision.Match(matchedExact.Name, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), StrategyName, 100, "完全匹配函数名"));
        }

        // 3. 检查状态指令: "查询知识库状态" / "获取知识库状态" / "知识库状态" / "索引状态" / "有多少笔记"
        if (cleanCommand.Contains("索引状态") || cleanCommand.Contains("知识库状态") || cleanCommand.Contains("查询状态") || cleanCommand.Contains("有多少笔记") || cleanCommand.Contains("有多少文件"))
        {
            var statusFunc = availableFuncs.FirstOrDefault(f => f.Name.Contains("GetIndexStatus", StringComparison.OrdinalIgnoreCase));
            if (statusFunc != null)
            {
                return Task.FromResult(RoutingDecision.Match(statusFunc.Name, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), StrategyName, 95, "匹配状态查询关键词"));
            }
        }

        // 4. 检查目录结构指令: "获取目录结构" / "查看文件结构" / "文件树" / "包含哪些文件" / "有哪些文件"
        if (cleanCommand.Contains("目录结构") || cleanCommand.Contains("文件树") || cleanCommand.Contains("文件结构") ||
            cleanCommand.Contains("有哪些文件") || cleanCommand.Contains("包含哪些文件") || cleanCommand.Contains("文件列表") || cleanCommand.Contains("笔记列表"))
        {
            var structFunc = availableFuncs.FirstOrDefault(f => f.Name.Contains("GetFileStructure", StringComparison.OrdinalIgnoreCase));
            if (structFunc != null)
            {
                return Task.FromResult(RoutingDecision.Match(structFunc.Name, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), StrategyName, 95, "匹配文件结构查询关键词"));
            }
        }

        // 4.1 检查工具与能力清单指令: "介绍当前工具" / "有哪些工具" / "可用工具" / "你有什么工具" / "工具列表" / "挂载工具"
        if (cleanCommand.Contains("当前工具") || cleanCommand.Contains("可用工具") || cleanCommand.Contains("有哪些工具") ||
            cleanCommand.Contains("你有什么工具") || cleanCommand.Contains("介绍工具") || cleanCommand.Contains("介绍当前工具") ||
            cleanCommand.Contains("工具列表") || cleanCommand.Contains("支持哪些工具") || cleanCommand.Contains("挂载哪些工具") ||
            cleanCommand.Contains("挂载了哪些工具") || cleanCommand.Contains("挂载工具") || cleanCommand.Contains("系统工具") ||
            cleanCommand.Contains("内置工具") || cleanCommand.Contains("查看工具") || cleanCommand.Contains("能调用什么工具") ||
            cleanCommand.Contains("能使用什么工具") || cleanCommand.Contains("当前有什么工具"))
        {
            var toolsFunc = availableFuncs.FirstOrDefault(f =>
                f.Name.Contains("GetAvailableTools", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("ListTools", StringComparison.OrdinalIgnoreCase));
            if (toolsFunc != null)
            {
                return Task.FromResult(RoutingDecision.Match(toolsFunc.Name, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), StrategyName, 98, "匹配工具清单查询关键词"));
            }
        }

        // 5. 检查搜索指令变体
        var searchFunc = availableFuncs.FirstOrDefault(f =>
            f.Name.Contains("SearchKnowledge", StringComparison.OrdinalIgnoreCase) ||
            f.Name.Contains("BM25Search", StringComparison.OrdinalIgnoreCase));

        if (searchFunc != null)
        {
            var (matched, targetName, query) = MatchSearchQuery(cleanCommand);
            if (matched && !string.IsNullOrWhiteSpace(query))
            {
                var normQuery = string.Join(" ", query.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
                var matchedFn = FindMatchingFunction(availableFuncs, targetName) ?? searchFunc;
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = normQuery,
                    ["keyword"] = normQuery,
                    ["topK"] = 5
                };
                return Task.FromResult(RoutingDecision.Match(matchedFn.Name, args, StrategyName, 98, $"匹配搜索前缀指令，提取关键词: {normQuery}"));
            }
        }

        return Task.FromResult(RoutingDecision.Unhandled(StrategyName));
    }

    private static string StripCommandPrefixes(string text)
    {
        string[] prefixes = ["调用:", "调用：", "tool_call:", "tool_call：", "tool_call ", "tool:", "tool：", "tool ", "/call ", "call ", "/"];
        foreach (var p in prefixes)
        {
            if (text.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return text.Substring(p.Length).Trim();
            }
        }
        return text;
    }

    private static AIFunction? FindMatchingFunction(IEnumerable<AIFunction> funcs, string targetName)
    {
        return funcs.FirstOrDefault(f =>
            string.Equals(f.Name, targetName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Name + "Async", targetName, StringComparison.OrdinalIgnoreCase) ||
            (f.Name.EndsWith("Async") && string.Equals(f.Name[..^5], targetName, StringComparison.OrdinalIgnoreCase)));
    }

    private static (bool Matched, string TargetName, string? Query) MatchSearchQuery(string cmd)
    {
        if (cmd.StartsWith("SearchKnowledge", StringComparison.OrdinalIgnoreCase))
        {
            var rem = cmd.Substring(15).Trim();
            var q = rem.TrimStart(':', '：', '搜', '索', ' ').Trim();
            return (true, "SearchKnowledge", q);
        }
        if (cmd.StartsWith("BM25Search", StringComparison.OrdinalIgnoreCase) || cmd.StartsWith("BM25:", StringComparison.OrdinalIgnoreCase) || cmd.StartsWith("BM25：", StringComparison.OrdinalIgnoreCase))
        {
            var rem = cmd.StartsWith("BM25Search", StringComparison.OrdinalIgnoreCase) ? cmd.Substring(10).Trim() : cmd.Substring(5).Trim();
            return (true, "BM25Search", rem.TrimStart(':', '：', '搜', '索', ' ').Trim());
        }

        string[] prefixes =
        [
            "搜索知识库中:", "搜索知识库中：", "搜索知识库中",
            "搜索知识库关于:", "搜索知识库关于：", "搜索知识库关于",
            "搜索知识库:", "搜索知识库：", "搜索知识库",
            "检索知识库中:", "检索知识库中：", "检索知识库中",
            "检索知识库关于:", "检索知识库关于：", "检索知识库关于",
            "检索知识库:", "检索知识库：", "检索知识库",
            "查询知识库关于:", "查询知识库关于：", "查询知识库关于",
            "查询知识库:", "查询知识库：", "查询知识库",
            "知识库搜索:", "知识库搜索：", "知识库搜索",
            "知识库检索:", "知识库检索：", "知识库检索",
            "在知识库中搜索:", "在知识库中搜索：", "在知识库中搜索",
            "在知识库中查找:", "在知识库中查找：", "在知识库中查找",
            "从知识库中检索:", "从知识库中检索：", "从知识库中检索",
            "从知识库检索:", "从知识库检索：", "从知识库检索",
            "从知识库搜索:", "从知识库搜索：", "从知识库搜索",
            "再查一下关于:", "再查一下关于：", "再查一下关于",
            "再查一下:", "再查一下：", "再查一下",
            "再搜索一下关于:", "再搜索一下关于：", "再搜索一下关于",
            "再搜索一下:", "再搜索一下：", "再搜索一下",
            "再检索一下关于:", "再检索一下关于：", "再检索一下关于",
            "再检索一下:", "再检索一下：", "再检索一下",
            "再找找关于:", "再找找关于：", "再找找关于",
            "再找找:", "再找找：", "再找找",
            "再搜关于:", "再搜关于：", "再搜关于",
            "再搜一下:", "再搜一下：", "再搜一下",
            "再查关于:", "再查关于：", "再查关于",
            "继续搜索:", "继续搜索：", "继续搜索",
            "继续检索:", "继续检索：", "继续检索",
            "帮我查一下关于:", "帮我查一下关于：", "帮我查一下关于",
            "帮我查一下:", "帮我查一下：", "帮我查一下",
            "帮我查关于:", "帮我查关于：", "帮我查关于",
            "帮我查:", "帮我查：", "帮我查",
            "帮我搜索关于:", "帮我搜索关于：", "帮我搜索关于",
            "帮我搜索:", "帮我搜索：", "帮我搜索",
            "帮我检索关于:", "帮我检索关于：", "帮我检索关于",
            "帮我检索:", "帮我检索：", "帮我检索",
            "查一下关于:", "查一下关于：", "查一下关于",
            "查一下:", "查一下：", "查一下",
            "搜索关于:", "搜索关于：", "搜索关于",
            "检索关于:", "检索关于：", "检索关于",
            "搜索:", "搜索：", "搜索 ",
            "检索:", "检索：", "检索 ",
            "查询:", "查询：", "查询 ",
            "查找:", "查找：", "查找 "
        ];

        foreach (var p in prefixes)
        {
            if (cmd.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return (true, "SearchKnowledge", cmd.Substring(p.Length).Trim());
            }
        }

        return (false, "SearchKnowledge", null);
    }
}

/// <summary>
/// 语义向量路由策略（优先级 80：中）
/// 借鉴 Aurelio AI 的 Semantic Router 与业界语义质心架构，利用语义空间锚点聚类与余弦相似度判定模糊意图，
/// 结合字符二元语法（Bigram Jaccard）语义重叠度量，彻底摆脱脆弱死板的关键字硬匹配，实现对多样化自然语言的高精度识别。
/// </summary>
public class SemanticEmbeddingRoutingStrategy : IIntentRoutingStrategy
{
    private readonly IEmbeddingService? _embeddingService;
    private readonly float _threshold;

    public string StrategyName => "SemanticEmbedding";
    public int Priority => 80;

    // 预定义的语义锚点聚类（Utterance Anchors）
    private static readonly string[] ToolInquiryAnchors =
    [
        "介绍当前工具", "你有什么工具", "你能做什么", "有哪些可用工具",
        "系统挂载了哪些函数", "介绍一下你的功能", "展示你的技能", "你能帮我做什么",
        "你都有什么本事", "列出所有工具"
    ];

    private static readonly string[] StatusAnchors =
    [
        "查询知识库索引状态", "知识库当前有多少笔记和文件", "查看知识库目录结构树", "查看已索引的文件列表"
    ];

    public SemanticEmbeddingRoutingStrategy(IEmbeddingService? embeddingService = null, float threshold = 0.65f)
    {
        _embeddingService = embeddingService;
        _threshold = threshold;
    }

    public async Task<RoutingDecision> RouteAsync(
        string userText,
        IList<AITool>? availableTools,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText) || availableTools == null || availableTools.Count == 0)
        {
            return RoutingDecision.Unhandled(StrategyName, "输入为空或无可用工具");
        }

        var trimmed = userText.Trim();
        if (trimmed.Length < 2 || trimmed.Length > 300)
        {
            return RoutingDecision.Unhandled(StrategyName, "长度超出意图向量适宜区间");
        }

        var availableFuncs = availableTools.OfType<AIFunction>().ToList();

        // 1. 如果已配置真实向量服务，执行高精度余弦相似度计算 (True Vector Semantic Router)
        if (_embeddingService != null)
        {
            try
            {
                var userVector = await _embeddingService.EncodeAsync(trimmed, ct);
                if (userVector != null && userVector.Length > 0)
                {
                    float maxToolSim = await ComputeMaxSimilarityAsync(userVector, ToolInquiryAnchors, ct);
                    if (maxToolSim >= _threshold)
                    {
                        var toolsFunc = availableFuncs.FirstOrDefault(f => f.Name.Contains("GetAvailableTools", StringComparison.OrdinalIgnoreCase));
                        if (toolsFunc != null)
                        {
                            return RoutingDecision.Match(toolsFunc.Name, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), StrategyName, (int)(maxToolSim * 100), $"语义向量命中工具清单咨询 (相似度: {maxToolSim:F2})");
                        }
                    }

                    float maxStatusSim = await ComputeMaxSimilarityAsync(userVector, StatusAnchors, ct);
                    if (maxStatusSim >= _threshold)
                    {
                        var statusFunc = availableFuncs.FirstOrDefault(f => f.Name.Contains("GetIndexStatus", StringComparison.OrdinalIgnoreCase));
                        if (statusFunc != null)
                        {
                            return RoutingDecision.Match(statusFunc.Name, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), StrategyName, (int)(maxStatusSim * 100), $"语义向量命中状态查询 (相似度: {maxStatusSim:F2})");
                        }
                    }
                }
            }
            catch
            {
                // 向量计算服务降级，平滑转入轻量语义重叠度量
            }
        }

        // 2. 自适应语义重叠度量 (Semantic Bigram Jaccard Metric，摆脱死板子串包含)
        float toolOverlap = ComputeSemanticOverlap(trimmed, ToolInquiryAnchors);
        if (toolOverlap >= 0.45f)
        {
            var toolsFunc = availableFuncs.FirstOrDefault(f => f.Name.Contains("GetAvailableTools", StringComparison.OrdinalIgnoreCase));
            if (toolsFunc != null)
            {
                return RoutingDecision.Match(toolsFunc.Name, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), StrategyName, (int)(toolOverlap * 100), $"自适应语义重叠命中工具清单咨询 (得分: {toolOverlap:F2})");
            }
        }

        float statusOverlap = ComputeSemanticOverlap(trimmed, StatusAnchors);
        if (statusOverlap >= 0.45f)
        {
            var statusFunc = availableFuncs.FirstOrDefault(f => f.Name.Contains("GetIndexStatus", StringComparison.OrdinalIgnoreCase));
            if (statusFunc != null)
            {
                return RoutingDecision.Match(statusFunc.Name, new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), StrategyName, (int)(statusOverlap * 100), $"自适应语义重叠命中状态查询 (得分: {statusOverlap:F2})");
            }
        }

        return RoutingDecision.Unhandled(StrategyName);
    }

    private async Task<float> ComputeMaxSimilarityAsync(float[] userVec, string[] anchors, CancellationToken ct)
    {
        if (_embeddingService == null) return 0f;
        float maxSim = 0f;
        foreach (var anchor in anchors)
        {
            var anchorVec = await _embeddingService.EncodeAsync(anchor, ct);
            if (anchorVec != null && anchorVec.Length == userVec.Length)
            {
                float sim = CosineSimilarity(userVec, anchorVec);
                if (sim > maxSim) maxSim = sim;
            }
        }
        return maxSim;
    }

    public static float CosineSimilarity(float[] v1, float[] v2)
    {
        if (v1.Length != v2.Length) return 0f;
        float dot = 0f, norm1 = 0f, norm2 = 0f;
        for (int i = 0; i < v1.Length; i++)
        {
            dot += v1[i] * v2[i];
            norm1 += v1[i] * v1[i];
            norm2 += v2[i] * v2[i];
        }
        float denom = (float)(Math.Sqrt(norm1) * Math.Sqrt(norm2));
        return denom > 0f ? dot / denom : 0f;
    }

    public static float ComputeSemanticOverlap(string text, string[] anchors)
    {
        if (string.IsNullOrWhiteSpace(text) || anchors == null || anchors.Length == 0) return 0f;
        var cleanText = CleanForSemantic(text);
        float bestScore = 0f;

        foreach (var anchor in anchors)
        {
            var cleanAnchor = CleanForSemantic(anchor);
            if (cleanText == cleanAnchor) return 1.0f;

            if (cleanText.Contains(cleanAnchor) || cleanAnchor.Contains(cleanText))
            {
                float lenRatio = (float)Math.Min(cleanText.Length, cleanAnchor.Length) / Math.Max(cleanText.Length, cleanAnchor.Length);
                bestScore = Math.Max(bestScore, 0.7f + lenRatio * 0.3f);
                continue;
            }

            // 1. 字符级 Dice 系数（对汉语字符袋具有强大的同义泛化鲁棒性）
            var textChars = new HashSet<char>(cleanText);
            var anchorChars = new HashSet<char>(cleanAnchor);
            int charIntersect = textChars.Intersect(anchorChars).Count();
            float charDice = (textChars.Count + anchorChars.Count) > 0
                ? 2.0f * charIntersect / (textChars.Count + anchorChars.Count)
                : 0f;

            // 2. 连续词元 Bigram Jaccard 相似度
            var textGrams = GetBigrams(cleanText);
            var anchorGrams = GetBigrams(cleanAnchor);
            float gramJaccard = 0f;
            if (textGrams.Count > 0 && anchorGrams.Count > 0)
            {
                int intersection = textGrams.Intersect(anchorGrams).Count();
                int union = textGrams.Union(anchorGrams).Count();
                gramJaccard = union > 0 ? (float)intersection / union : 0f;
            }

            float combined = Math.Max(charDice, gramJaccard);
            bestScore = Math.Max(bestScore, combined);
        }

        return bestScore;
    }

    private static string CleanForSemantic(string s) =>
        new string(s.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private static HashSet<string> GetBigrams(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (s.Length < 2)
        {
            if (s.Length == 1) set.Add(s);
            return set;
        }
        for (int i = 0; i < s.Length - 1; i++)
        {
            set.Add(s.Substring(i, 2));
        }
        return set;
    }
}

/// <summary>
/// 复合意图路由器（Composite & Chain Pattern）
/// 依次调用已注册的意图路由策略，直到产生首个确定性决策。
/// </summary>
public class CompositeIntentRouter
{
    private readonly List<IIntentRoutingStrategy> _strategies;

    public static CompositeIntentRouter Default { get; } = new(new IIntentRoutingStrategy[]
    {
        new ExactPrefixRoutingStrategy(),
        new SemanticEmbeddingRoutingStrategy()
    });

    public CompositeIntentRouter(IEnumerable<IIntentRoutingStrategy> strategies)
    {
        _strategies = strategies.OrderByDescending(s => s.Priority).ToList();
    }

    public async Task<RoutingDecision> RouteAsync(
        string userText,
        IList<AITool>? availableTools,
        CancellationToken ct = default)
    {
        foreach (var strategy in _strategies)
        {
            var decision = await strategy.RouteAsync(userText, availableTools, ct);
            if (decision.Handled)
            {
                return decision;
            }
        }

        return RoutingDecision.Unhandled("Composite", "所有路由策略均未拦截，透传大模型推理");
    }
}
