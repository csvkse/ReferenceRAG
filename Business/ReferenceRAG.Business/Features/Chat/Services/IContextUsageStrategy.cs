namespace ReferenceRAG.Business.Features.Chat.Services;

using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using ReferenceRAG.Core.Helpers;

/// <summary>
/// 会话上下文用量度量数据
/// </summary>
public record ContextUsageInfo(int UsedTokens, int MaxContextTokens, double Percent);

/// <summary>
/// 上下文用量估算策略接口（策略模式 Strategy Pattern）
/// </summary>
public interface IContextUsageStrategy
{
    string StrategyName { get; }

    ContextUsageInfo Calculate(
        IEnumerable<ChatMessage>? messages,
        string? systemPrompt,
        IList<AITool>? tools,
        int maxContextTokens);
}

/// <summary>
/// 快速字符级 Token 估算策略（默认策略，毫秒级无损耗）
/// </summary>
public class FastTokenEstimationContextUsageStrategy : IContextUsageStrategy
{
    public string StrategyName => "FastTokenEstimation";

    public ContextUsageInfo Calculate(
        IEnumerable<ChatMessage>? messages,
        string? systemPrompt,
        IList<AITool>? tools,
        int maxContextTokens)
    {
        int maxTokens = maxContextTokens > 0 ? maxContextTokens : 8192;
        int usedTokens = 0;

        // 1. 系统提示词开销
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            usedTokens += TokenEstimator.EstimateTokens(systemPrompt);
        }

        // 2. 工具定义协议开销
        if (tools != null && tools.Count > 0)
        {
            var toolsPrompt = ChatToolCallingHelper.FormatToolsPrompt(tools);
            usedTokens += TokenEstimator.EstimateTokens(toolsPrompt);
        }

        // 3. 历史会话消息开销（包含 ChatML 轮次标记开销）
        if (messages != null)
        {
            foreach (var msg in messages)
            {
                usedTokens += 10; // ChatML 协议包装开销 <|im_start|>role\n ... <|im_end|>\n
                if (!string.IsNullOrWhiteSpace(msg.Text))
                {
                    usedTokens += TokenEstimator.EstimateTokens(msg.Text);
                }
            }
        }

        double percent = Math.Min(100.0, Math.Round((double)usedTokens / maxTokens * 100.0, 1));
        return new ContextUsageInfo(usedTokens, maxTokens, percent);
    }
}
