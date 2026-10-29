namespace ReferenceRAG.Business.Features.Chat.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.AI;

/// <summary>
/// 动态工具选择策略接口（策略模式 Strategy Pattern）
/// 借鉴 MAF (Microsoft Agent Framework) 的动态工具过滤思想，
/// 根据用户意图、模型能力与会话上下文动态下发工具集，消除小参数模型的上下文膨胀与工具幻觉。
/// </summary>
public interface IToolSelectionStrategy
{
    string StrategyName { get; }

    /// <summary>
    /// 根据用户输入、意图先验与模型画像选择本轮对话应注入的工具子集
    /// </summary>
    IList<AITool> SelectTools(
        string userMessage,
        IList<AITool> allTools,
        RoutingDecision? routingDecision = null,
        ChatModelCapabilities? capabilities = null);
}

/// <summary>
/// 全量工具策略：下发所有已注册工具（适用于 GPT-4o、DeepSeek-V3 等大参数强逻辑模型）
/// </summary>
public class AllToolsSelectionStrategy : IToolSelectionStrategy
{
    public string StrategyName => "AllTools";

    public IList<AITool> SelectTools(
        string userMessage,
        IList<AITool> allTools,
        RoutingDecision? routingDecision = null,
        ChatModelCapabilities? capabilities = null)
    {
        return allTools ?? Array.Empty<AITool>();
    }
}

/// <summary>
/// 意图自适应工具选择策略（策略模式：意图感知分流）
/// 核心优势：
/// 1. 若前置意图路由已直接决断（Handled=true），工具由后端直接调度，跳过大模型 Phase 1 生成，无需向模型注入工具；
/// 2. 问候闲聊类文本彻底屏蔽工具定义，节省 600~1200 prompt tokens 并提升首字延迟（TTFT）；
/// 3. 明确的检索或状态意图仅定向注入对应专业工具，防止小模型因多工具 Schema 干扰产生混淆或幻觉。
/// </summary>
public class IntentAdaptiveToolSelectionStrategy : IToolSelectionStrategy
{
    public string StrategyName => "IntentAdaptive";

    public IList<AITool> SelectTools(
        string userMessage,
        IList<AITool> allTools,
        RoutingDecision? routingDecision = null,
        ChatModelCapabilities? capabilities = null)
    {
        if (allTools == null || allTools.Count == 0)
        {
            return Array.Empty<AITool>();
        }

        // 1. 若意图路由器已确定性直接命中工具（如搜索前缀、GetIndexStatus），
        //    工具调用将在服务端直接执行，大模型只需负责 Phase 2 总结，无需下发任何工具 Schema
        if (routingDecision is { Handled: true })
        {
            return Array.Empty<AITool>();
        }

        var trimmed = userMessage?.Trim() ?? string.Empty;

        // 2. 工具清单与能力介绍意图（如“介绍当前工具”、“可用工具”、“你能做什么”）：
        //    必须完整下发所有已挂载工具（含 GetAvailableTools），确保模型对自身能力具有完整感知，严禁工具剥离丢失
        if (IsToolInquiry(trimmed))
        {
            return allTools;
        }

        // 3. 常见闲聊问候检测：如果用户只是打招呼或表达谢意，完全剔除工具定义，加速推理并杜绝小模型误调工具
        if (IsChitchatGreeting(trimmed))
        {
            return Array.Empty<AITool>();
        }

        // 4. 系统索引与目录状态意图（优先于通用知识检索，防止“知识库状态”被误当做笔记检索）
        if (IsSystemStatusQuery(trimmed))
        {
            var filtered = allTools
                .Where(t => t is AIFunction f && (
                    f.Name.Equals("GetIndexStatus", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Equals("GetFileStructure", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (filtered.Count > 0)
            {
                return filtered;
            }
        }

        // 5. 知识检索与笔记阅读意图：注入所有知识库检索与阅读工具（严格对齐真实函数名 ReadFileContent，防止工具丢失）
        if (IsKnowledgeQuery(trimmed))
        {
            var filtered = allTools
                .Where(t => t is AIFunction f && (
                    f.Name.Equals("SearchKnowledge", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Equals("BM25Search", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Equals("SearchNoteByTitle", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Equals("ReadFileContent", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Equals("ReadNote", StringComparison.OrdinalIgnoreCase) ||
                    f.Name.Equals("DrillDownChunk", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (filtered.Count > 0)
            {
                return filtered;
            }
        }

        // 6. 默认保底：返回全部可用工具
        return allTools;
    }

    private static bool IsToolInquiry(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] toolKeywords =
        [
            "当前工具", "可用工具", "有哪些工具", "你有什么工具", "介绍工具", "介绍当前工具",
            "工具列表", "支持哪些工具", "挂载哪些工具", "挂载了哪些工具", "挂载工具", "系统工具",
            "内置工具", "查看工具", "能调用什么工具", "能使用什么工具", "当前有什么工具",
            "你能做什么", "你有什么功能", "你能干什么", "能力", "介绍一下你自己", "自我介绍",
            "展示技能", "你有啥本事", "绝活"
        ];

        if (toolKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return true;

        return SemanticEmbeddingRoutingStrategy.ComputeSemanticOverlap(text, toolKeywords) >= 0.40f;
    }

    private static bool IsChitchatGreeting(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 25) return false;

        // 若具备工具/能力查询语义重叠，严禁误判为纯闲聊
        if (IsToolInquiry(text)) return false;

        string[] greetings =
        [
            "你好", "您好", "hi", "hello", "嗨", "早上好", "中午好", "下午好", "晚上好",
            "早安", "晚安", "哈喽", "hello there",
            "在吗", "在不在", "谢谢", "多谢", "thx", "thanks", "再见", "拜拜", "who are you",
            "你是谁"
        ];

        // 清除常见标点与空白符后进行词缀匹配
        var clean = new string(text.Where(c => !char.IsPunctuation(c) && !char.IsWhiteSpace(c) && c != '呀' && c != '吧' && c != '啊' && c != '哦' && c != '呢').ToArray()).ToLowerInvariant();
        return greetings.Any(g => clean.Equals(g, StringComparison.OrdinalIgnoreCase) || clean.StartsWith(g, StringComparison.OrdinalIgnoreCase) || clean.EndsWith(g, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKnowledgeQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] searchKeywords =
        [
            "查一下", "找一下", "检索", "搜索", "笔记", "文档", "知识库",
            "什么是", "怎么做", "如何", "为什么", "架构", "设计", "原理", "配置", "实现"
        ];

        return searchKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSystemStatusQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        string[] statusKeywords =
        [
            "索引状态", "知识库状态", "有多少文件", "有多少笔记", "文件结构", "目录树", "层级结构"
        ];

        return statusKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
