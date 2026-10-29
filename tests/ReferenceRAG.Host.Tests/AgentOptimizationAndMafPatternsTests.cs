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

    [Theory]
    [InlineData("介绍当前工具")]
    [InlineData("你有什么工具")]
    [InlineData("有哪些可用工具")]
    [InlineData("你能做什么")]
    [InlineData("支持哪些工具")]
    public void IntentAdaptiveToolSelectionStrategy_ToolInquiry_ShouldKeepAllTools(string toolInquiryText)
    {
        var strategy = new IntentAdaptiveToolSelectionStrategy();

        // Act: 工具/能力咨询意图，严禁工具剥离丢失
        var selected = strategy.SelectTools(toolInquiryText, _sampleTools);

        // Assert: 必须完整保留所有可用工具，防止模型产生“无内置工具”的幻觉
        Assert.Equal(_sampleTools.Count, selected.Count);
    }

    [Fact]
    public async Task ExactPrefixRoutingStrategy_ToolInquiry_ShouldRouteToGetAvailableTools()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "Tools List", "GetAvailableTools", "获取当前智能助手内置已挂载的所有工具清单")
        };
        var router = new ExactPrefixRoutingStrategy();

        var decision = await router.RouteAsync("介绍当前工具", tools);

        Assert.True(decision.Handled);
        Assert.Equal("GetAvailableTools", decision.FunctionName);
        Assert.Equal(98, decision.ConfidenceScore);
    }

    [Fact]
    public void ChatToolCallingHelper_TryDetectDirectToolCall_ToolInquiry_ShouldDetectGetAvailableTools()
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "Tools List", "GetAvailableTools", "获取当前智能助手内置已挂载的所有工具清单")
        };

        bool matched = ChatToolCallingHelper.TryDetectDirectToolCall("请介绍当前工具", tools, out var funcName, out var args);

        Assert.True(matched);
        Assert.Equal("GetAvailableTools", funcName);
    }

    [Fact]
    public void SemanticEmbeddingRoutingStrategy_ComputeSemanticOverlap_ShouldRecognizeParaphrasedInquiries()
    {
        string[] anchors = ["介绍当前工具", "你有什么工具", "你能做什么", "有哪些可用工具"];

        // 语义发散测试：即便非完全包含的口语表达，重叠度与相似度也能超过门禁阈值
        float score1 = SemanticEmbeddingRoutingStrategy.ComputeSemanticOverlap("你能帮我做什么", anchors);
        float score2 = SemanticEmbeddingRoutingStrategy.ComputeSemanticOverlap("当前工具支持什么", anchors);
        float score3 = SemanticEmbeddingRoutingStrategy.ComputeSemanticOverlap("有哪些工具可以使用", anchors);

        Assert.True(score1 >= 0.40f, $"你能帮我做什么 得分偏低: {score1}");
        Assert.True(score2 >= 0.40f, $"当前工具支持什么 得分偏低: {score2}");
        Assert.True(score3 >= 0.40f, $"有哪些工具可以使用 得分偏低: {score3}");
    }

    [Fact]
    public void SemanticEmbeddingRoutingStrategy_CosineSimilarity_IdenticalVectors_ShouldBeOne()
    {
        float[] v1 = [0.1f, 0.5f, 0.8f, -0.2f];
        float[] v2 = [0.1f, 0.5f, 0.8f, -0.2f];

        float sim = SemanticEmbeddingRoutingStrategy.CosineSimilarity(v1, v2);

        Assert.InRange(sim, 0.999f, 1.001f);
    }

    [Fact]
    public void ChatToolCallingHelper_BuildChatMlPrompt_ShouldIncludeCapabilityManifest()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是 ReferenceRAG 智能助手。"),
            new(ChatRole.User, "介绍当前工具")
        };

        string prompt = ChatToolCallingHelper.BuildChatMlPrompt(messages, _sampleTools, 4096, 2048, "Qwen3.5-2B");

        // 必须包含中文内置能力画像与自知准则，彻底根治小模型“无内置工具”的认知失调与幻觉
        Assert.Contains("## 内置专业能力画像 (Capability Manifest)", prompt);
        Assert.Contains("【能力自知准则】", prompt);
        Assert.Contains("SearchKnowledge", prompt);
        Assert.Contains("# Tools", prompt);
    }

    [Theory]
    [InlineData("你都有啥本事")]
    [InlineData("你能帮我做什么")]
    [InlineData("展示一下你的技能")]
    public void IntentAdaptiveToolSelectionStrategy_ParaphrasedInquiry_ShouldKeepAllTools(string paraphrased)
    {
        var strategy = new IntentAdaptiveToolSelectionStrategy();

        var selected = strategy.SelectTools(paraphrased, _sampleTools);

        Assert.Equal(_sampleTools.Count, selected.Count);
    }

    [Fact]
    public void ChatModelConfig_EnableIntentRouting_DefaultShouldBeTrue()
    {
        var config = new ReferenceRAG.Core.Models.ChatModelConfig();
        Assert.True(config.EnableIntentRouting);
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("介绍一下成都")]
    [InlineData("今天天气怎么样")]
    public void AllToolsSelectionStrategy_WhenRoutingDisabled_ShouldAlwaysReturnAllTools(string query)
    {
        var strategy = new AllToolsSelectionStrategy();
        var decision = RoutingDecision.Unhandled("Disabled", "意图识别已在设置中关闭");

        var selected = strategy.SelectTools(query, _sampleTools, decision);

        // 无论何种闲聊或未分类请求，只要关闭意图识别，必须全量挂载工具交给 LLM 自主决断
        Assert.Equal(_sampleTools.Count, selected.Count);
    }

    [Theory]
    [InlineData("你好")]
    [InlineData("早安")]
    [InlineData("哈喽")]
    [InlineData("谢谢")]
    public void IntentAdaptiveToolSelectionStrategy_WhenRoutingEnabled_FiltersToolsForChitchat(string chitchat)
    {
        var strategy = new IntentAdaptiveToolSelectionStrategy();

        var selected = strategy.SelectTools(chitchat, _sampleTools);

        // 开启意图识别时，纯问候闲聊自动精简/不挂载工具，保障模型纯净响应与首字加速
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

    #region 4. 自回归重复退化防御与流式熔断测试 (Repetition Circuit Breaker & Sampling Penalties)

    [Fact]
    public void RepetitionCircuitBreaker_ConsecutivePeriodicChunk_BreaksGracefully()
    {
        var breaker = new RepetitionCircuitBreaker(
            maxHistoryLength: 1024,
            minChunkLength: 16,
            minSentenceLength: 18,
            minRepetitions: 3);

        string repeatingUnit = "【系统服务健康检查正常，集群所有计算节点状态完好】";
        var tokens = new List<string>
        {
            "正在初始化系统状态巡检...\n",
            repeatingUnit,
            repeatingUnit,
            repeatingUnit // 第 3 次连续出现相同长子串
        };

        bool broken = false;
        string? brokenReason = null;
        foreach (var t in tokens)
        {
            if (!breaker.ProcessToken(t, out _))
            {
                broken = true;
                brokenReason = breaker.BreakReason;
                break;
            }
        }

        Assert.True(broken, "连续 3 次周期性长子串必须触发熔断");
        Assert.True(breaker.IsBroken);
        Assert.NotNull(brokenReason);
        Assert.Contains("连续周期性文本块", brokenReason);
    }

    [Fact]
    public void RepetitionCircuitBreaker_PatternedMultiSectionLoop_BreaksAtThirdCycle()
    {
        var breaker = new RepetitionCircuitBreaker(
            maxHistoryLength: 2048,
            minChunkLength: 16,
            minSentenceLength: 18,
            minRepetitions: 3);

        // 模拟自回归模型在生成结构化章节时陷入相同长句台词与段落的死循环
        string section1 =
            "1. 模块一：分布式鉴权服务接入\n" +
            "   - 说明：主系统与网关服务通过统一拦截器进行令牌双向校验，保证各租户数据隔离与权限合法性。\n\n";

        string coreRepeatedSentence = "请务必确保双向握手协议与超时重试策略准确无误，避免网关层在高并发时出现级联雪崩。";

        string section2 =
            "2. 模块二：分布式鉴权服务接入\n" +
            $"   - 架构师强调：\"{coreRepeatedSentence}\"\n" +
            "   - 开发者确认配置完成。\n\n";

        string section3 =
            "3. 模块三：分布式鉴权服务接入\n" +
            $"   - 架构师强调：\"{coreRepeatedSentence}\"\n" +
            "   - 开发者确认配置完成。\n\n";

        string section4 =
            "4. 模块四：分布式鉴权服务接入\n" +
            $"   - 架构师强调：\"{coreRepeatedSentence}\"\n";

        var streamChunks = new[] { section1, section2, section3, section4 };

        bool triggered = false;
        foreach (var chunk in streamChunks)
        {
            if (!breaker.ProcessToken(chunk, out _))
            {
                triggered = true;
                break;
            }
        }

        Assert.True(triggered, "同类长句在窗口内累积出现达阈值必须立即熔断");
        Assert.True(breaker.IsBroken);
        Assert.Contains("核心语句在窗口内重复出现", breaker.BreakReason);
    }

    [Fact]
    public void RepetitionCircuitBreaker_NormalConversationalText_DoesNotBreak()
    {
        var breaker = new RepetitionCircuitBreaker(
            maxHistoryLength: 2048,
            minChunkLength: 16,
            minSentenceLength: 18,
            minRepetitions: 3);

        // 正常长文本：包含合理重复词汇（如标点、连接词、自然说明、不同段落）
        var naturalStream = new[]
        {
            "ReferenceRAG 是一个支持本地离线向量检索与大模型问答的知识库系统。\n",
            "它的核心特性包括：\n",
            "1. 支持端侧 ONNX 与 GGUF 模型的硬件加速推理；\n",
            "2. 提供基于 BM25 与向量语义的混合召回能力；\n",
            "3. 内置适配器模式与有限状态机，保障多轮 Agent 工具调用的确定性与可观测性。\n",
            "通过以上架构，系统能够在无外网连接的离线环境下安全高效运行。\n",
            "如果您需要进一步了解微服务网关部署或索引重构，请随时告知。"
        };

        foreach (var token in naturalStream)
        {
            bool ok = breaker.ProcessToken(token, out var safeChunk);
            Assert.True(ok, "正常文本流绝不能被熔断误伤");
            Assert.Equal(token, safeChunk);
        }

        Assert.False(breaker.IsBroken);
        Assert.Null(breaker.BreakReason);
    }

    [Fact]
    public void LocalLlamaChatClient_ConfiguresSamplingPenalties_AndPassesProperties()
    {
        var client = new LocalLlamaChatClient(
            modelPath: "dummy.gguf",
            modelName: "Qwen-Test",
            gpuLayers: 0,
            contextSize: 2048,
            batchSize: 512,
            uBatchSize: 256,
            mmprojPath: null,
            logger: null,
            temperature: 0.6f,
            topP: 0.85f,
            repeatPenalty: 1.15f,
            frequencyPenalty: 0.25f,
            presencePenalty: 0.15f,
            penaltyCount: 256,
            enableCircuitBreaker: true);

        Assert.Equal(0.6f, client.Temperature);
        Assert.Equal(0.85f, client.TopP);
        Assert.Equal(1.15f, client.RepeatPenalty);
        Assert.Equal(0.25f, client.FrequencyPenalty);
        Assert.Equal(0.15f, client.PresencePenalty);
        Assert.Equal(256, client.PenaltyCount);
        Assert.True(client.EnableRepetitionCircuitBreaker);
    }

    [Fact]
    public void ObsidianRagConfig_ChatModelConfig_DefaultsAndSerialization_AreConsistent()
    {
        var cfg = new ReferenceRAG.Core.Models.ChatModelConfig();

        // 验证默认黄金防退化采样参数
        Assert.Equal(1.1f, cfg.RepeatPenalty);
        Assert.Equal(0.2f, cfg.FrequencyPenalty);
        Assert.Equal(0.1f, cfg.PresencePenalty);
        Assert.Equal(128, cfg.PenaltyCount);
        Assert.True(cfg.EnableRepetitionCircuitBreaker);

        // 验证 JSON 往返序列化
        var json = JsonSerializer.Serialize(cfg);
        var deserialized = JsonSerializer.Deserialize<ReferenceRAG.Core.Models.ChatModelConfig>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(1.1f, deserialized.RepeatPenalty);
        Assert.Equal(0.2f, deserialized.FrequencyPenalty);
        Assert.Equal(0.1f, deserialized.PresencePenalty);
        Assert.Equal(128, deserialized.PenaltyCount);
        Assert.True(deserialized.EnableRepetitionCircuitBreaker);
    }

    #endregion
}
