namespace ReferenceRAG.Host.Tests;

using System;
using System.Collections.Generic;
using Microsoft.Extensions.AI;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Service.Services;
using Xunit;

public class ChatThoughtAndContextUsageTests
{
    private readonly List<AITool> _sampleTools;

    public ChatThoughtAndContextUsageTests()
    {
        _sampleTools = new List<AITool>
        {
            AIFunctionFactory.Create(
                (string query, int topK) => $"MockSearchResult for {query}",
                "SearchKnowledge",
                "在本地知识库中进行语义及混合搜索"),
            AIFunctionFactory.Create(
                () => "MockIndexStatus: Active, 100 docs",
                "GetIndexStatus",
                "获取当前知识库与向量索引状态")
        };
    }

    [Fact]
    public void FastTokenEstimationContextUsageStrategy_WithSystemAndTools_CalculatesBaseTokens()
    {
        // Arrange
        var strategy = new FastTokenEstimationContextUsageStrategy();
        string systemPrompt = "你是 ReferenceRAG 智能助手。";
        int maxTokens = 8192;

        // Act
        var usage = strategy.Calculate([], systemPrompt, _sampleTools, maxTokens);

        // Assert
        Assert.True(usage.UsedTokens > 0, "基础系统提示词与工具协议必须产生初始 Token 用量");
        Assert.Equal(8192, usage.MaxContextTokens);
        Assert.True(usage.Percent > 0.0 && usage.Percent < 10.0, "初始基础协议占总容量比例应处于合理低位");
    }

    [Fact]
    public void FastTokenEstimationContextUsageStrategy_WithMultiTurnConversation_TracksAccumulatedTokens()
    {
        // Arrange
        var strategy = new FastTokenEstimationContextUsageStrategy();
        string systemPrompt = "你是 ReferenceRAG 智能助手。";
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "请帮我介绍一下操虫师这本轻小说的核心世界观与主角经历"),
            new(ChatRole.Assistant, "《操虫师》是一部融合奇幻与虫群机械魔法题材的轻小说，讲述了主角高桥亮太成为宿命者后的经历。"),
            new(ChatRole.User, "再详细介绍一下变形虫王的设定和反派势力的交锋细节")
        };
        int maxTokens = 4096;

        // Act
        var usage = strategy.Calculate(messages, systemPrompt, _sampleTools, maxTokens);

        // Assert
        Assert.True(usage.UsedTokens >= 100, "多轮对话必须正确累加用户与助手的 Token");
        Assert.Equal(4096, usage.MaxContextTokens);
        Assert.True(usage.Percent > 1.0, "累积后的百分比应体现增长");
    }

    [Fact]
    public void FastTokenEstimationContextUsageStrategy_BudgetExceeded_CapsAt100Percent()
    {
        // Arrange
        var strategy = new FastTokenEstimationContextUsageStrategy();
        var hugeText = new string('A', 50000);
        var messages = new List<ChatMessage> { new(ChatRole.User, hugeText) };

        // Act
        var usage = strategy.Calculate(messages, "System", null, 1024);

        // Assert
        Assert.Equal(100.0, usage.Percent);
    }

    [Fact]
    public void StripThoughtBlocks_SingleThought_StripsCleanly()
    {
        var raw = "<think>\n正在分析用户问题，需要先调用工具查询宁姚的经历...\n</think>\n根据小说设定，宁姚是五彩天下第一位飞升仙人境的大剑仙。";
        var stripped = ChatToolCallingHelper.StripThoughtBlocks(raw);

        Assert.DoesNotContain("<think>", stripped);
        Assert.DoesNotContain("</think>", stripped);
        Assert.DoesNotContain("正在分析用户问题", stripped);
        Assert.Contains("根据小说设定，宁姚是五彩天下第一位飞升仙人境的大剑仙。", stripped);
    }

    [Fact]
    public void StripThoughtBlocks_MultipleThoughts_StripsAll()
    {
        // 模拟多轮 Agent 工具调用留下的连续 3 轮思考标签
        var raw = "<think>思考阶段 1：分析搜索关键词</think><think>思考阶段 2：分析检索到的片段</think><think>思考阶段 3：组织最终语言</think>宁姚的全书经历如下：早期在剑气长城相识，后至五彩天下开天辟地。";
        var stripped = ChatToolCallingHelper.StripThoughtBlocks(raw);

        Assert.DoesNotContain("<think>", stripped);
        Assert.DoesNotContain("</think>", stripped);
        Assert.DoesNotContain("思考阶段", stripped);
        Assert.Equal("宁姚的全书经历如下：早期在剑气长城相识，后至五彩天下开天辟地。", stripped);
    }

    [Fact]
    public void StripThoughtBlocks_UnclosedThought_StripsGracefully()
    {
        var raw = "<think>正在思考但流中断尚未闭合";
        var stripped = ChatToolCallingHelper.StripThoughtBlocks(raw);

        Assert.Empty(stripped);
    }

    [Fact]
    public void BuildChatMlPrompt_StripsThoughtsFromAssistantHistory()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "大女主是谁？"),
            new(ChatRole.Assistant, "<think>思考1：判断大女主为宁姚</think>大女主是宁姚。"),
            new(ChatRole.User, "介绍一下她在全书中的经历")
        };

        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(messages, _sampleTools, maxContextTokens: 16384, maxOutputTokens: 4096);

        // 验证 Prompt 必须剥离思维链，彻底杜绝历史 CoT 导致的少样本格式偏置与卡片堆叠
        Assert.DoesNotContain("<think>", prompt);
        Assert.DoesNotContain("</think>", prompt);
        Assert.DoesNotContain("思考1", prompt);
        // 验证正文保留完好
        Assert.Contains("大女主是宁姚。", prompt);
        Assert.Contains("介绍一下她在全书中的经历", prompt);
    }

    [Fact]
    public void BuildChatMlPrompt_DynamicTokenBudget_AllocatesGenerousBudgetUnder16k()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "请详细分析全部剧情")
        };

        // 模拟 16k 上下文窗口
        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(messages, null, maxContextTokens: 16384, maxOutputTokens: 4096);

        // 验证在 16k 空间下，有效生成提示词且未发生暴力腰斩
        Assert.Contains("<|im_start|>user\n请详细分析全部剧情\n<|im_end|>", prompt);
        Assert.EndsWith("<|im_start|>assistant\n", prompt);
    }
}
