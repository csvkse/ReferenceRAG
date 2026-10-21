namespace ReferenceRAG.Host.Tests;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ReferenceRAG.Business.Features.Chat.Services;
using Xunit;

public class ThreeTierSlmOptimizationTests
{
    private readonly List<AITool> _sampleTools;

    public ThreeTierSlmOptimizationTests()
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
                "获取当前知识库与向量索引状态"),
            AIFunctionFactory.Create(
                () => "MockFileStructure: root/doc1.md",
                "GetFileStructure",
                "获取已索引文档的树形层级结构")
        };
    }

    #region 1. 策略模式测试 (Strategy Pattern: Intent Routing)

    [Fact]
    public async Task ExactPrefixRoutingStrategy_WithSearchPrefix_ShouldRouteToSearchKnowledge()
    {
        // Arrange
        var strategy = new ExactPrefixRoutingStrategy();
        string input = "搜索知识库中: 微调与对齐技术\n包含LoRA实现";

        // Act
        var decision = await strategy.RouteAsync(input, _sampleTools);

        // Assert
        Assert.True(decision.Handled);
        Assert.Equal("SearchKnowledge", decision.FunctionName);
        Assert.NotNull(decision.Arguments);
        Assert.Equal("微调与对齐技术 包含LoRA实现", decision.Arguments["query"]?.ToString());
        Assert.Equal("ExactPrefix", decision.StrategyName);
        Assert.True(decision.ConfidenceScore >= 90);
    }

    [Fact]
    public async Task ExactPrefixRoutingStrategy_WithIndexStatus_ShouldRouteDirectly()
    {
        // Arrange
        var strategy = new ExactPrefixRoutingStrategy();
        string input = "查看当前知识库状态";

        // Act
        var decision = await strategy.RouteAsync(input, _sampleTools);

        // Assert
        Assert.True(decision.Handled);
        Assert.Equal("GetIndexStatus", decision.FunctionName);
    }

    [Fact]
    public async Task ExactPrefixRoutingStrategy_WithXmlToolCallTag_ShouldParseAndRoute()
    {
        // Arrange
        var strategy = new ExactPrefixRoutingStrategy();
        string input = "<tool_call>\n{\"name\": \"SearchKnowledge\", \"arguments\": {\"query\": \"CUDA13 兼容性\"}}\n</tool_call>";

        // Act
        var decision = await strategy.RouteAsync(input, _sampleTools);

        // Assert
        Assert.True(decision.Handled);
        Assert.Equal("SearchKnowledge", decision.FunctionName);
        Assert.Equal("CUDA13 兼容性", decision.Arguments?["query"]?.ToString());
    }

    [Fact]
    public async Task ExactPrefixRoutingStrategy_WithGenericChat_ShouldReturnUnhandled()
    {
        // Arrange
        var strategy = new ExactPrefixRoutingStrategy();
        string input = "你好，请问你是谁？可以帮我写一首关于春天的诗吗？";

        // Act
        var decision = await strategy.RouteAsync(input, _sampleTools);

        // Assert
        Assert.False(decision.Handled);
        Assert.Null(decision.FunctionName);
    }

    [Fact]
    public async Task CompositeIntentRouter_ShouldExecuteInPriorityOrder()
    {
        // Arrange
        var router = CompositeIntentRouter.Default;
        string directInput = "搜索: 端侧小模型量化";

        // Act
        var decision = await router.RouteAsync(directInput, _sampleTools);

        // Assert
        Assert.True(decision.Handled);
        Assert.Equal("SearchKnowledge", decision.FunctionName);
        Assert.Equal("端侧小模型量化", decision.Arguments?["query"]?.ToString());
    }

    #endregion

    #region 2. 适配器模式测试 (Adapter Pattern: Constraint Generation)

    [Fact]
    public void LlamaGbnfConstraintAdapter_ShouldGenerateUnionGrammarPreventingDeadlock()
    {
        // Arrange
        var adapter = new LlamaGbnfConstraintAdapter();
        Assert.Equal(ToolConstraintBackend.LocalGguf, adapter.Backend);

        // Act
        var gbnf = adapter.AdaptConstraint(_sampleTools, "ToolCallOrText") as string;

        // Assert
        Assert.NotNull(gbnf);
        // 关键断言：必须生成联合文法 (Union Grammar)，避免只有 tool-call 导致闲聊死锁
        Assert.Contains("root ::= tool-call | text-response", gbnf);
        Assert.Contains("<tool_call>", gbnf);
        Assert.Contains("</tool_call>", gbnf);
        Assert.Contains("\"SearchKnowledge\"", gbnf);
        Assert.Contains("\"GetIndexStatus\"", gbnf);
        Assert.Contains("text-response ::= [^<]+", gbnf);
    }

    [Fact]
    public void OnnxSchemaConstraintAdapter_ShouldGenerateValidJsonSchemaWithAnyOf()
    {
        // Arrange
        var adapter = new OnnxSchemaConstraintAdapter();
        Assert.Equal(ToolConstraintBackend.LocalOnnx, adapter.Backend);

        // Act
        var schemaJson = adapter.AdaptConstraint(_sampleTools) as string;

        // Assert
        Assert.NotNull(schemaJson);
        using var doc = JsonDocument.Parse(schemaJson);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("$schema", out _));
        Assert.True(root.TryGetProperty("anyOf", out var anyOf));
        Assert.Equal(_sampleTools.Count, anyOf.GetArrayLength());
    }

    [Fact]
    public void ExternalApiStrictConstraintAdapter_ShouldInjectStrictToolsProperty()
    {
        // Arrange
        var adapter = new ExternalApiStrictConstraintAdapter();
        Assert.Equal(ToolConstraintBackend.ExternalApi, adapter.Backend);

        // Act
        var result = adapter.AdaptConstraint(_sampleTools) as ChatOptions;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(_sampleTools.Count, result.Tools?.Count);
        Assert.NotNull(result.AdditionalProperties);
        Assert.True(result.AdditionalProperties.TryGetValue("strict_tools", out var strictVal));
        Assert.Equal(true, strictVal);
    }

    #endregion

    #region 3. 状态机模式测试 (State Machine Pattern: Full Lifecycle & Self-Healing)

    [Fact]
    public void ToolCallingExecutionStateMachine_HappyPath_TransitionsCorrectly()
    {
        // Arrange
        var ctx = new ChatTurnContext
        {
            SessionId = "test-session-1",
            UserMessage = "搜索知识库: GGUF 性能"
        };
        var fsm = new ToolCallingExecutionStateMachine(ctx);

        // Act & Assert
        Assert.Equal(ChatTurnState.Idle, fsm.CurrentState);

        fsm.TransitionTo(ChatTurnState.Routing, "开始复合前置意图路由");
        Assert.Equal(ChatTurnState.Routing, fsm.CurrentState);

        fsm.TransitionTo(ChatTurnState.DirectToolExecuting, "确定性命中搜索前缀");
        Assert.Equal(ChatTurnState.DirectToolExecuting, fsm.CurrentState);

        fsm.TransitionTo(ChatTurnState.ToolExecuting, "执行 SearchKnowledge 工具");
        ctx.ExecutedTools.Add("SearchKnowledge");
        Assert.Equal(ChatTurnState.ToolExecuting, fsm.CurrentState);

        fsm.TransitionTo(ChatTurnState.Phase2Synthesizing, "工具返回结果，启动负向防说教总结");
        Assert.Equal(ChatTurnState.Phase2Synthesizing, fsm.CurrentState);

        fsm.TransitionTo(ChatTurnState.OutputSanitizing, "清洗输出与代码块规整");
        ctx.RawOutput = "基于知识库检索，GGUF 量化在 Q4_K_M 下显存收益最高。";
        ctx.SanitizedOutput = ctx.RawOutput;
        Assert.Equal(ChatTurnState.OutputSanitizing, fsm.CurrentState);

        fsm.TransitionTo(ChatTurnState.Completed, "流程正常收尾");
        Assert.Equal(ChatTurnState.Completed, fsm.CurrentState);

        // 验证状态轨迹记录
        Assert.Contains(ChatTurnState.Routing, ctx.StateTransitions);
        Assert.Contains(ChatTurnState.DirectToolExecuting, ctx.StateTransitions);
        Assert.Contains(ChatTurnState.ToolExecuting, ctx.StateTransitions);
        Assert.Contains(ChatTurnState.Phase2Synthesizing, ctx.StateTransitions);
        Assert.Contains(ChatTurnState.OutputSanitizing, ctx.StateTransitions);
        Assert.Contains(ChatTurnState.Completed, ctx.StateTransitions);
    }

    [Fact]
    public void ToolCallingExecutionStateMachine_InvalidTransition_ThrowsException()
    {
        // Arrange
        var ctx = new ChatTurnContext { UserMessage = "Test Invalid" };
        var fsm = new ToolCallingExecutionStateMachine(ctx);

        // Act & Assert: 从 Idle 直接跳转到 Phase2Synthesizing 是非法越级
        Assert.Throws<InvalidOperationException>(() =>
            fsm.TransitionTo(ChatTurnState.Phase2Synthesizing, "非法跳转"));
    }

    [Fact]
    public void ToolCallingExecutionStateMachine_WhenOutputEmpty_TriggersSelfHealingSuccessfully()
    {
        // Arrange
        var ctx = new ChatTurnContext
        {
            SessionId = "heal-session",
            UserMessage = "搜索知识库状态"
        };
        ctx.ExecutedTools.Add("GetIndexStatus");

        var fsm = new ToolCallingExecutionStateMachine(ctx);
        fsm.TransitionTo(ChatTurnState.Routing);
        fsm.TransitionTo(ChatTurnState.DirectToolExecuting);
        fsm.TransitionTo(ChatTurnState.ToolExecuting);
        fsm.TransitionTo(ChatTurnState.Phase2Synthesizing);

        // 模拟小模型推理截断，吐出空白文本或崩溃
        ctx.RawOutput = "";

        // Act: 触发状态机自愈
        string healedText = fsm.TriggerSelfHealing("模型输出为空，触发防白屏自愈");

        // Assert
        Assert.Equal(ChatTurnState.Completed, fsm.CurrentState);
        Assert.NotNull(healedText);
        Assert.Contains("🔧 **已执行工具**", healedText);
        Assert.Contains("`GetIndexStatus`", healedText);
        Assert.Contains("工具执行完毕", healedText);
        Assert.Equal(healedText, ctx.SanitizedOutput);
    }

    #endregion
}
