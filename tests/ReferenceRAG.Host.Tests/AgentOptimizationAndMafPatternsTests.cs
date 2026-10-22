namespace ReferenceRAG.Host.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Service.Services;
using Xunit;

public class AgentOptimizationAndMafPatternsTests
{
    private readonly List<AITool> _sampleTools;

    public AgentOptimizationAndMafPatternsTests()
    {
        _sampleTools = new List<AITool>
        {
            AIFunctionFactory.Create(
                (string query, int topK) => $"MockSearchResult for {query}",
                "SearchKnowledge",
                "在本地知识库中进行语义及混合搜索"),
            AIFunctionFactory.Create(
                (string path) => $"Content of {path}",
                "ReadNote",
                "读取指定知识库笔记文件"),
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

    #region 1. 适配器模式测试 (Adapter Pattern: SSE Reasoning Stream & Non-Streaming)

    [Fact]
    public async Task ToolCallSanitizingStream_ShouldAdaptReasoningContentToThinkBlock()
    {
        // 模拟来自 DeepSeek-R1 / Qwen 官方兼容 API 的原始 SSE 事件流
        string rawSseStream =
            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"delta\":{\"reasoning_content\":\"第一步：分析用户需求。\"}}]}\n\n" +
            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"delta\":{\"reasoning_content\":\"第二步：制定检索计划。\"}}]}\n\n" +
            "data: {\"id\":\"chatcmpl-1\",\"choices\":[{\"delta\":{\"content\":\"成都是一座兼具底蕴与活力的城市。\"}}]}\n\n" +
            "data: [DONE]\n\n";

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(rawSseStream));
        using var sanitizingStream = new ToolCallSanitizingStream(memoryStream);
        using var reader = new StreamReader(sanitizingStream, Encoding.UTF8);

        string output = await reader.ReadToEndAsync();

        // 断言：第一块 reasoning_content 必须被前缀适配注入 <think>\n
        Assert.Contains("\"content\":\"<think>\\n第一步：分析用户需求。\"", output);
        // 断言：后续 reasoning_content 必须直接映射为 content
        Assert.Contains("\"content\":\"第二步：制定检索计划。\"", output);
        // 断言：从 reasoning 转换到正文 content 时，必须优雅注入 \n</think>\n\n 结束标
        Assert.Contains("\"content\":\"\\n</think>\\n\\n成都是一座兼具底蕴与活力的城市。\"", output);
    }

    [Fact]
    public async Task ToolCallSanitizingStream_WhenModelStopsWithoutAnswerContent_ShouldCloseThinkBeforeDone()
    {
        // 模拟模型在思考阶段直接结束（如仅输出思考即达到上限）
        string rawSseStream =
            "data: {\"id\":\"chatcmpl-2\",\"choices\":[{\"delta\":{\"reasoning_content\":\"纯粹的思考步骤。\"}}]}\n\n" +
            "data: [DONE]\n\n";

        using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(rawSseStream));
        using var sanitizingStream = new ToolCallSanitizingStream(memoryStream);
        using var reader = new StreamReader(sanitizingStream, Encoding.UTF8);

        string output = await reader.ReadToEndAsync();

        Assert.Contains("<think>", output);
        Assert.Contains("</think>", output);
    }

    [Fact]
    public void OpenAiToolCallSanitizingHandler_AdaptNonStreamingReasoning_ShouldPrependThinkTag()
    {
        // 模拟非流式 JSON 响应
        string rawJson = "{\"id\":\"chatcmpl-3\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"这是最终回答\",\"reasoning_content\":\"这是内部思考过程\"}}]}";

        string adapted = OpenAiToolCallSanitizingHandler.AdaptNonStreamingReasoning(rawJson);

        Assert.Contains("<think>\\n这是内部思考过程\\n</think>\\n\\n这是最终回答", adapted);
    }

    #endregion

    #region 2. 策略模式测试 (Strategy Pattern: Intent Adaptive Tool Selection)

    [Fact]
    public void IntentAdaptiveToolSelectionStrategy_ChitchatGreeting_ShouldSuppressAllTools()
    {
        var strategy = new IntentAdaptiveToolSelectionStrategy();

        // 闲聊问候
        var selected = strategy.SelectTools("你好呀！早上好！", _sampleTools);

        // 断言：闲聊时完全剔除工具定义，节省 Token 并杜绝误调
        Assert.Empty(selected);
    }

    [Fact]
    public void IntentAdaptiveToolSelectionStrategy_KnowledgeQuery_ShouldOnlySelectKnowledgeTools()
    {
        var strategy = new IntentAdaptiveToolSelectionStrategy();

        // 知识库查询
        var selected = strategy.SelectTools("帮我查一下知识库中关于 Docker 容器部署的笔记", _sampleTools);

        // 断言：仅下发 SearchKnowledge 与 ReadNote，抑制系统状态工具
        Assert.Equal(2, selected.Count);
        var names = selected.OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.Contains("SearchKnowledge", names);
        Assert.Contains("ReadNote", names);
        Assert.DoesNotContain("GetIndexStatus", names);
    }

    [Fact]
    public void IntentAdaptiveToolSelectionStrategy_SystemStatusQuery_ShouldOnlySelectStatusTools()
    {
        var strategy = new IntentAdaptiveToolSelectionStrategy();

        // 状态查询
        var selected = strategy.SelectTools("当前知识库状态如何？有多少笔记文件？", _sampleTools);

        // 断言：仅下发 GetIndexStatus 与 GetFileStructure
        Assert.Equal(2, selected.Count);
        var names = selected.OfType<AIFunction>().Select(f => f.Name).ToList();
        Assert.Contains("GetIndexStatus", names);
        Assert.Contains("GetFileStructure", names);
        Assert.DoesNotContain("SearchKnowledge", names);
    }

    [Fact]
    public void IntentAdaptiveToolSelectionStrategy_DirectHandledDecision_ShouldSuppressToolsForModel()
    {
        var strategy = new IntentAdaptiveToolSelectionStrategy();
        var directDecision = RoutingDecision.Match("SearchKnowledge", new Dictionary<string, object?>(), "ExactPrefix", 100, "精确匹配");

        // Act: 服务端直接分发时，大模型无需再感知工具定义
        var selected = strategy.SelectTools("搜索: 向量检索", _sampleTools, directDecision);

        Assert.Empty(selected);
    }

    #endregion

    #region 3. 状态机模式全生命周期流转与遥测测试 (State Machine & Lifecycle Telemetry)

    [Fact]
    public void ToolCallingExecutionStateMachine_LifecycleWithDirectDispatch_RecordsFullTelemetry()
    {
        var ctx = new ChatTurnContext
        {
            SessionId = "session-telemetry-1",
            UserMessage = "搜索: 端侧模型部署"
        };
        var fsm = new ToolCallingExecutionStateMachine(ctx);

        // 1. Idle -> Routing
        fsm.TransitionTo(ChatTurnState.Routing, "意图路由分流");
        Thread.Sleep(5); // 模拟耗时

        // 2. Routing -> DirectToolExecuting
        fsm.TransitionTo(ChatTurnState.DirectToolExecuting, "命中直接分发");
        Thread.Sleep(5);

        // 3. DirectToolExecuting -> ToolExecuting
        fsm.TransitionTo(ChatTurnState.ToolExecuting, "执行底层存储查询");
        ctx.ExecutedTools.Add("SearchKnowledge");
        Thread.Sleep(5);

        // 4. ToolExecuting -> Phase2Synthesizing
        fsm.TransitionTo(ChatTurnState.Phase2Synthesizing, "大模型生成总结");
        Thread.Sleep(5);

        // 5. Phase2Synthesizing -> OutputSanitizing
        fsm.TransitionTo(ChatTurnState.OutputSanitizing, "响应清洗与格式化");
        ctx.RawOutput = "检索完成。";
        ctx.SanitizedOutput = ctx.RawOutput;
        Thread.Sleep(5);

        // 6. OutputSanitizing -> Completed
        fsm.TransitionTo(ChatTurnState.Completed, "本轮结束");

        // 断言：各阶段耗时均被精准捕获
        Assert.True(ctx.StateDurationMs.ContainsKey(ChatTurnState.Routing));
        Assert.True(ctx.StateDurationMs.ContainsKey(ChatTurnState.DirectToolExecuting));
        Assert.True(ctx.StateDurationMs.ContainsKey(ChatTurnState.ToolExecuting));
        Assert.True(ctx.StateDurationMs.ContainsKey(ChatTurnState.Phase2Synthesizing));
        Assert.True(ctx.StateDurationMs.ContainsKey(ChatTurnState.OutputSanitizing));

        // 断言：状态流转历史完整留痕 (Idle + 6 次状态流转 = 7 项记录)
        Assert.Equal(7, ctx.StateTransitions.Count);
        Assert.Equal(ChatTurnState.Completed, fsm.CurrentState);
    }

    #endregion
}
