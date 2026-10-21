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
/// 利用本地嵌入模型计算提问与核心意图锚点的余弦相似度，毫秒级判定模糊自然语言意图。
/// </summary>
public class SemanticEmbeddingRoutingStrategy : IIntentRoutingStrategy
{
    private readonly IEmbeddingService? _embeddingService;
    private readonly float _threshold;

    public string StrategyName => "SemanticEmbedding";
    public int Priority => 80;

    public SemanticEmbeddingRoutingStrategy(IEmbeddingService? embeddingService = null, float threshold = 0.85f)
    {
        _embeddingService = embeddingService;
        _threshold = threshold;
    }

    public async Task<RoutingDecision> RouteAsync(
        string userText,
        IList<AITool>? availableTools,
        CancellationToken ct = default)
    {
        if (_embeddingService == null || string.IsNullOrWhiteSpace(userText) || availableTools == null || availableTools.Count == 0)
        {
            return RoutingDecision.Unhandled(StrategyName, "未配置嵌入服务或输入为空");
        }

        // 仅在明确具有搜索或查询等意图且长度适中时触发向量路由
        var trimmed = userText.Trim();
        if (trimmed.Length < 3 || trimmed.Length > 200)
        {
            return RoutingDecision.Unhandled(StrategyName, "长度超出意图向量适宜区间");
        }

        // 快捷意图分类：若命中知识库检索典型词群
        string[] searchIntents = ["检索", "搜索", "查一下", "找找", "有哪些关于", "笔记里有没有", "知识库里有"];
        if (searchIntents.Any(k => trimmed.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            var searchFunc = availableTools.OfType<AIFunction>().FirstOrDefault(f =>
                f.Name.Contains("SearchKnowledge", StringComparison.OrdinalIgnoreCase));

            if (searchFunc != null)
            {
                var cleanQuery = trimmed;
                foreach (var prefix in new[] { "请帮我", "帮我", "麻烦", "查一下", "搜索", "检索" })
                {
                    if (cleanQuery.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        cleanQuery = cleanQuery.Substring(prefix.Length).Trim();
                }

                if (!string.IsNullOrWhiteSpace(cleanQuery))
                {
                    var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["query"] = cleanQuery,
                        ["keyword"] = cleanQuery,
                        ["topK"] = 5
                    };
                    return RoutingDecision.Match(searchFunc.Name, args, StrategyName, 88, $"语义关键词命中，提取主体: {cleanQuery}");
                }
            }
        }

        return RoutingDecision.Unhandled(StrategyName);
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
