using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ReferenceRAG.Infrastructure.Features.Cognitive;

namespace ReferenceRAG.Business.Features.Chat.Services;

/// <summary>
/// 基于 TypeSafe AI System One (Jev) 的极速认知意图路由策略（优先级 80）
/// 介于 ExactPrefix (100) 与 SemanticEmbedding (50) 之间，提供 100ms 级的语义概率先验分流。
/// </summary>
public sealed class JevSystemOneRoutingStrategy : IIntentRoutingStrategy
{
    private readonly IJevDecisionEngine? _decisionEngine;

    public string StrategyName => "JevSystemOne";
    public int Priority => 80;

    public JevSystemOneRoutingStrategy(IJevDecisionEngine? decisionEngine = null)
    {
        _decisionEngine = decisionEngine;
    }

    public async Task<RoutingDecision> RouteAsync(
        string userText,
        IList<AITool>? availableTools,
        CancellationToken ct = default)
    {
        if (_decisionEngine == null || !_decisionEngine.IsEnabled || string.IsNullOrWhiteSpace(userText))
        {
            return RoutingDecision.Unhandled(StrategyName, "Jev 决策引擎未就绪或未启用");
        }

        var availableFuncs = availableTools?.OfType<AIFunction>().ToList() ?? new List<AIFunction>();
        var result = await _decisionEngine.ClassifyIntentAsync(userText, ct);

        // 1. 判定为需要检索知识库 (rag_search)
        if (result.RequiresRetrieval || result.PrimaryIntent.Equals("rag_search", StringComparison.OrdinalIgnoreCase))
        {
            var searchFunc = availableFuncs.FirstOrDefault(f =>
                f.Name.Contains("SearchKnowledgeBase", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("RagSearch", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains("HybridSearch", StringComparison.OrdinalIgnoreCase) ||
                f.Name.Equals("Search", StringComparison.OrdinalIgnoreCase));

            if (searchFunc != null)
            {
                var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["query"] = userText.Trim()
                };

                return RoutingDecision.Match(
                    searchFunc.Name,
                    args,
                    StrategyName,
                    (int)Math.Clamp(result.Confidence * 100, 70, 99),
                    result.Rationale);
            }

            return RoutingDecision.Unhandled(StrategyName, $"Jev 判定需要检索 ({result.Rationale})，但未找到匹配的 Search AIFunction 工具");
        }

        // 2. 判定为纯闲聊 (chit_chat) 且明确无需检索
        if (result.PrimaryIntent.Equals("chit_chat", StringComparison.OrdinalIgnoreCase) && !result.RequiresRetrieval)
        {
            return RoutingDecision.Unhandled(
                StrategyName,
                $"Jev 判定为闲聊模式 ({result.Rationale})，安全短路跳过知识库召回，直接进入主模型对话生成");
        }

        // 3. 其余情况透传复合策略后续环节
        return RoutingDecision.Unhandled(StrategyName, $"Jev 判定意图为 {result.PrimaryIntent}，透传大模型自由组织思考");
    }
}
