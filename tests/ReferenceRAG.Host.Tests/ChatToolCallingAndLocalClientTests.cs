using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class ChatToolCallingAndLocalClientTests
{
    [Fact]
    public void FormatToolsPrompt_GeneratesValidXmlWithJsonSchema()
    {
        var tool = AIFunctionFactory.Create(
            [Description("根据关键词搜索笔记内容")] (string query, int topK = 10) => "result",
            "SearchNotes");

        var prompt = ChatToolCallingHelper.FormatToolsPrompt([tool]);

        Assert.Contains("<tools>", prompt);
        Assert.Contains("</tools>", prompt);
        Assert.Contains("\"name\":\"SearchNotes\"", prompt);
        Assert.Contains("根据关键词搜索笔记内容", prompt);
        Assert.Contains("\"query\"", prompt);
    }

    [Fact]
    public void BuildChatMlPrompt_InjectsToolsAndRolesCorrectly()
    {
        var tool = AIFunctionFactory.Create(
            [Description("读取文件")] (string path) => "content",
            "ReadFile");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "请帮我读取 readme.md 文件")
        };

        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(messages, [tool]);

        Assert.Contains("<|im_start|>system", prompt);
        Assert.Contains("<tools>", prompt);
        Assert.Contains("ReadFile", prompt);
        Assert.Contains("<|im_start|>user\n请帮我读取 readme.md 文件\n<|im_end|>", prompt);
        Assert.EndsWith("<|im_start|>assistant\n", prompt);
    }

    [Fact]
    public void BuildChatMlPrompt_FormatsToolResponseCorrectly()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "查询知识库"),
            new(ChatRole.Assistant, "<tool_call>{\"name\":\"SearchNotes\",\"arguments\":{\"query\":\"test\"}}</tool_call>"),
            new(ChatRole.Tool, "搜索结果: 找到2条笔记")
        };

        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(messages);

        Assert.Contains("<tool_response>", prompt);
        Assert.Contains("搜索结果: 找到2条笔记", prompt);
        Assert.Contains("</tool_response>", prompt);
    }

    [Fact]
    public void TryParseToolCall_ValidXmlFormat_ParsesSuccessfully()
    {
        var text = "<tool_call>\n{\"name\": \"SearchKnowledgeAsync\", \"arguments\": {\"query\": \"向量数据库\", \"topK\": 5}}\n</tool_call>";

        var result = ChatToolCallingHelper.TryParseToolCall(text);

        Assert.True(result.Success);
        Assert.Equal("SearchKnowledgeAsync", result.Name);
        Assert.NotNull(result.Arguments);
        Assert.True(result.Arguments.ContainsKey("query"));
        Assert.Equal("向量数据库", result.Arguments["query"]?.ToString());
    }

    [Fact]
    public void TryParseToolCall_MarkdownEnclosed_ParsesSuccessfully()
    {
        var text = "```xml\n<tool_call>{\"name\": \"GetFileStructure\", \"arguments\": {}}</tool_call>\n```";

        var result = ChatToolCallingHelper.TryParseToolCall(text);

        Assert.True(result.Success);
        Assert.Equal("GetFileStructure", result.Name);
    }

    [Fact]
    public void TryParseToolCall_NonToolCallText_ReturnsFalse()
    {
        var text = "这是一段普通的助手回答内容，没有任何工具调用标签。";

        var result = ChatToolCallingHelper.TryParseToolCall(text);

        Assert.False(result.Success);
        Assert.Null(result.Name);
    }

    [Fact]
    public async Task LocalOnnxChatClient_SimulationMode_EmitsTextAndInterception()
    {
        using var client = new LocalOnnxChatClient(
            modelPath: "non_existent_model_dir",
            modelName: "Phi-3.5-mini-instruct-onnx");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "你好，请介绍一下你自己")
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Messages);
        Assert.Contains("Phi-3.5-mini-instruct-onnx", response.Text);

        var streamingChunks = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages))
        {
            streamingChunks.Add(update);
        }

        Assert.NotEmpty(streamingChunks);
        var fullStreamText = string.Join("", streamingChunks.Select(c => c.Text));
        Assert.Contains("Phi-3.5-mini-instruct-onnx", fullStreamText);
    }

    [Fact]
    public async Task ModelManager_SwitchChatModelAsync_AutoDetectsOnnxAndGgufBackends()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "rag_chat_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
            var configManager = new ConfigManager(customConfigPath: configPath);
            var modelManager = new ModelManager(tempDir, configManager);

            // 注册模拟的已下载 ONNX 与 GGUF 模型
            var availableChatModels = await modelManager.GetAvailableModelsAsync("chat");
            var onnxModel = availableChatModels.FirstOrDefault(m => m.Name.Contains("onnx", StringComparison.OrdinalIgnoreCase));
            var ggufModel = availableChatModels.FirstOrDefault(m => m.Name.Contains("GGUF", StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(onnxModel);
            Assert.NotNull(ggufModel);

            // 标记已下载并模拟路径
            onnxModel.IsDownloaded = true;
            onnxModel.LocalPath = Path.Combine(tempDir, "Phi-3.5-mini-instruct-onnx");

            ggufModel.IsDownloaded = true;
            ggufModel.LocalPath = Path.Combine(tempDir, "qwen2.5-7b-instruct.gguf");

            // 1. 切换到 ONNX 模型 -> 预期自动切换到 LocalOnnxGenAi 后端
            var switchedOnnx = await modelManager.SwitchChatModelAsync(onnxModel.Name);
            Assert.True(switchedOnnx);

            var configAfterOnnx = configManager.Load();
            Assert.Equal("LocalOnnxGenAi", configAfterOnnx.Chat.ActiveBackend);
            Assert.Equal(onnxModel.Name, configAfterOnnx.Chat.LocalOnnxGenAi.ModelName);

            // 2. 切换到 GGUF 模型 -> 预期自动切换到 LocalGguf 后端
            var switchedGguf = await modelManager.SwitchChatModelAsync(ggufModel.Name);
            Assert.True(switchedGguf);

            var configAfterGguf = configManager.Load();
            Assert.Equal("LocalGguf", configAfterGguf.Chat.ActiveBackend);
            Assert.Equal(ggufModel.Name, configAfterGguf.Chat.LocalGguf.ModelName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void ModelManager_ChatModels_HaveCleanDisplayNamesWithoutChineseParentheses()
    {
        var chatModels = ModelManager.PredefinedModels.Where(m => m.ModelType == "chat").ToList();
        Assert.NotEmpty(chatModels);

        foreach (var m in chatModels)
        {
            // 验证 DisplayName 严禁包含中文括号介绍（如 (极速小钢炮)）
            Assert.DoesNotMatch(@"\([^)]*[\u4e00-\u9fa5]+[^)]*\)", m.DisplayName);
            // 验证 DisplayName 严禁包含中文字符，保持官方纯净英文名以供社区检索
            Assert.DoesNotMatch(@"[\u4e00-\u9fa5]", m.DisplayName);
            // 验证中文介绍已完好保留在 Description 字段中
            Assert.Matches(@"[\u4e00-\u9fa5]", m.Description);
        }
    }

    [Fact]
    public void FormatToolsPrompt_NoNegativeSuppression_AndHasPositiveFewShot()
    {
        var tool = AIFunctionFactory.Create(() => "ok", "GetIndexStatus", "获取知识库状态");
        var prompt = ChatToolCallingHelper.FormatToolsPrompt([tool]);

        // GATE-TC-01 门禁：严禁包含负向抑制指令（避免微型模型被禁言导致工具调用瘫痪）
        Assert.DoesNotContain("NEVER output XML tags like <tool_call>", prompt);
        Assert.DoesNotContain("NEVER return raw JSON", prompt);

        // 正向协议与 1-Shot 示例
        Assert.Contains("<tools>", prompt);
        Assert.Contains("GetIndexStatus", prompt);
        Assert.Contains("<tool_call>", prompt);
        Assert.Contains("Example:", prompt);
    }

    [Fact]
    public void TryDetectDirectToolCall_ExactToolName_EmitsFunctionCall()
    {
        var tool = AIFunctionFactory.Create(() => "ok", "GetIndexStatus", "获取知识库状态");
        
        // 门禁：用户输入精确指令 "GetIndexStatus" 时 100% 触发确定性意图分发
        var detected = ChatToolCallingHelper.TryDetectDirectToolCall("GetIndexStatus", [tool], out var name, out var args);

        Assert.True(detected);
        Assert.Equal("GetIndexStatus", name);
        Assert.NotNull(args);
    }

    [Fact]
    public void TryDetectDirectToolCall_SearchDirectives_ExtractsQueryAccurately()
    {
        var searchTool = AIFunctionFactory.Create((string query, int topK = 10) => "ok", "SearchKnowledge", "语义检索");
        var tools = new List<AITool> { searchTool };

        // 门禁：针对 "搜索: 你好"
        var d1 = ChatToolCallingHelper.TryDetectDirectToolCall("搜索: 你好", tools, out var n1, out var a1);
        Assert.True(d1);
        Assert.Equal("SearchKnowledge", n1);
        Assert.NotNull(a1);
        Assert.Equal("你好", a1["query"]?.ToString());

        // 门禁：针对 "SearchKnowledge 搜索: 你好"
        var d2 = ChatToolCallingHelper.TryDetectDirectToolCall("SearchKnowledge 搜索: 你好", tools, out var n2, out var a2);
        Assert.True(d2);
        Assert.Equal("SearchKnowledge", n2);
        Assert.NotNull(a2);
        Assert.Equal("你好", a2["query"]?.ToString());

        // 门禁：针对 "查一下: 向量数据库"
        var d3 = ChatToolCallingHelper.TryDetectDirectToolCall("查一下: 向量数据库", tools, out var n3, out var a3);
        Assert.True(d3);
        Assert.Equal("SearchKnowledge", n3);
        Assert.NotNull(a3);
        Assert.Equal("向量数据库", a3["query"]?.ToString());

        var bm25Tool = AIFunctionFactory.Create((string? keyword = null, int topK = 10, string? query = null) => "ok", "BM25Search", "关键词检索");
        var toolsWithBm25 = new List<AITool> { searchTool, bm25Tool };

        // 门禁：针对 D4-02 "BM25Search 搜索: 数据库连接超时"
        var dBm25_1 = ChatToolCallingHelper.TryDetectDirectToolCall("BM25Search 搜索: 数据库连接超时", toolsWithBm25, out var nBm25_1, out var aBm25_1);
        Assert.True(dBm25_1);
        Assert.Equal("BM25Search", nBm25_1);
        Assert.NotNull(aBm25_1);
        Assert.Equal("数据库连接超时", aBm25_1["keyword"]?.ToString());
        Assert.Equal("数据库连接超时", aBm25_1["query"]?.ToString());

        // 门禁：针对 D4-01 "BM25: 错误码"
        var dBm25_2 = ChatToolCallingHelper.TryDetectDirectToolCall("BM25: 错误码", toolsWithBm25, out var nBm25_2, out var aBm25_2);
        Assert.True(dBm25_2);
        Assert.Equal("BM25Search", nBm25_2);
        Assert.NotNull(aBm25_2);
        Assert.Equal("错误码", aBm25_2["keyword"]?.ToString());
        Assert.Equal("错误码", aBm25_2["query"]?.ToString());
    }

    [Fact]
    public void TryParseToolCall_LenientParsing_SupportsBareJsonAndMarkdown()
    {
        // 1. 裸 JSON 对象容错
        var bareJson = "{\"name\": \"GetIndexStatus\", \"arguments\": {}}";
        var r1 = ChatToolCallingHelper.TryParseToolCall(bareJson);
        Assert.True(r1.Success);
        Assert.Equal("GetIndexStatus", r1.Name);

        // 2. Markdown 代码块包裹裸 JSON
        var mdJson = "```json\n{\"name\": \"SearchKnowledge\", \"arguments\": {\"query\": \"test\"}}\n```";
        var r2 = ChatToolCallingHelper.TryParseToolCall(mdJson);
        Assert.True(r2.Success);
        Assert.Equal("SearchKnowledge", r2.Name);
        Assert.Equal("test", r2.Arguments!["query"]?.ToString());
    }

    [Fact]
    public void SanitizeAssistantResponse_CleansHallucinatedNlinkTokens()
    {
        var raw = "这是正常的回答内容 <nlinkcount> 知识库状态正常 <nlink123> 结束";
        var cleaned = ChatToolCallingHelper.SanitizeAssistantResponse(raw);

        Assert.DoesNotContain("<nlinkcount>", cleaned);
        Assert.DoesNotContain("<nlink123>", cleaned);
        Assert.Contains("这是正常的回答内容", cleaned);
        Assert.Contains("知识库状态正常", cleaned);
    }

    [Fact]
    public void SanitizeAssistantResponse_HealsRawJsonStatusToNaturalLanguage()
    {
        var rawJson = "{\n  \"status\": {\n    \"BM25文档总数\": 5392,\n    \"向量分块总数\": 280\n  }\n}";
        var healed = ChatToolCallingHelper.SanitizeAssistantResponse(rawJson);

        Assert.Contains("知识库当前已成功构建索引", healed);
        Assert.Contains("280", healed);
        Assert.Contains("5392", healed);
        Assert.DoesNotContain("{", healed);
    }

    [Fact]
    public void SanitizeAssistantResponse_HealsTruncatedRawJsonGracefully()
    {
        var truncatedJson = "{\n  \"status\": {\n    \"BM25文档总数\": 5392,\n    \"向量分块总数\": 280";
        var healed = ChatToolCallingHelper.SanitizeAssistantResponse(truncatedJson);

        Assert.Contains("知识库当前已成功构建索引", healed);
        Assert.Contains("280", healed);
        Assert.Contains("5392", healed);
    }

    [Fact]
    public void TryParseToolCall_UnclosedToolCallTag_ParsesSuccessfully()
    {
        // 针对端侧小模型输出 <tool_call> 后被 <|im_end|> 截断无闭合标签的回归防范
        var raw = "<tool_call>\n{\"name\": \"SearchKnowledge\", \"arguments\": {\"query\": \"你好\"}}";
        var result = ChatToolCallingHelper.TryParseToolCall(raw);

        Assert.True(result.Success);
        Assert.Equal("SearchKnowledge", result.Name);
        Assert.NotNull(result.Arguments);
        Assert.Equal("你好", result.Arguments["query"]?.ToString());
    }

    [Fact]
    public void TryDetectDirectToolCall_KnowledgeBasePrefixVariations_Matches()
    {
        var tool = AIFunctionFactory.Create(
            [Description("搜索知识库")] (string query, int topK = 5) => "result",
            "SearchKnowledge");

        var variations = new[]
        {
            "搜索知识库中: 你好",
            "搜索知识库中：你好",
            "搜索知识库: 你好",
            "检索知识库: 你好",
            "在知识库中搜索: 你好",
            "从知识库检索: 你好",
            "知识库搜索: 你好",
            "知识库检索: 你好"
        };

        foreach (var cmd in variations)
        {
            var matched = ChatToolCallingHelper.TryDetectDirectToolCall(cmd, [tool], out var fn, out var args);
            Assert.True(matched, $"指令应被成功命中: {cmd}");
            Assert.Equal("SearchKnowledge", fn);
            Assert.Equal("你好", args!["query"]?.ToString());
        }
    }

    [Fact]
    public void TryDetectDirectToolCall_MultilineInput_ExtractsCleanQuery()
    {
        // 模拟用户截图 2 的真实输入指令
        var multiline = "搜索知识库中: 你好\n相关内容\n梳理有哪些文档";
        var tool = AIFunctionFactory.Create(
            [Description("搜索知识库")] (string query, int topK = 5) => "result",
            "SearchKnowledge");

        var matched = ChatToolCallingHelper.TryDetectDirectToolCall(multiline, [tool], out var fn, out var args);
        Assert.True(matched);
        Assert.Equal("SearchKnowledge", fn);
        Assert.Contains("你好", args!["query"]?.ToString());
        Assert.DoesNotContain("\n", args!["query"]?.ToString());
    }

    [Fact]
    public void SanitizeAssistantResponse_CleansDanglingToolCall_PreservesSummary()
    {
        var raw = "<tool_call>\n{\"name\": \"SearchKnowledge\", \"arguments\": {\"query\": \"你好\"}}\n</tool_call>";
        var cleaned = ChatToolCallingHelper.SanitizeAssistantResponse(raw);

        Assert.DoesNotContain("<tool_call>", cleaned);
        Assert.DoesNotContain("</tool_call>", cleaned);
        Assert.Contains("SearchKnowledge", cleaned);
    }

    [Fact]
    public async Task ContextAwareFallbackAdapter_TransitionsStateAndStreamsTokens()
    {
        using var adapter = new ContextAwareFallbackAdapter("Test-Model");
        Assert.Equal(OnnxEngineState.Uninitialized, adapter.State);
        Assert.False(adapter.IsNativeAccelerated);

        var initialized = await adapter.InitializeAsync("dummy_path", null, CancellationToken.None);
        Assert.True(initialized);
        Assert.Equal(OnnxEngineState.Ready, adapter.State);

        var tokens = new List<string>();
        await foreach (var token in adapter.GenerateTokensAsync("你好", 256, CancellationToken.None))
        {
            tokens.Add(token);
        }

        Assert.NotEmpty(tokens);
        var full = string.Join("", tokens);
        Assert.Contains("Test-Model", full);
        Assert.Equal(OnnxEngineState.Ready, adapter.State);

        adapter.Unload();
        Assert.Equal(OnnxEngineState.Uninitialized, adapter.State);
    }

    [Fact]
    public async Task NativeOgaDirectMlAdapter_NonExistentPath_TransitionsToDegraded()
    {
        using var adapter = new NativeOgaDirectMlAdapter();
        Assert.Equal(OnnxEngineState.Uninitialized, adapter.State);
        Assert.True(adapter.IsNativeAccelerated);

        var initialized = await adapter.InitializeAsync("C:\\non_existent_oga_dir_xyz", null, CancellationToken.None);
        Assert.False(initialized);
        Assert.Equal(OnnxEngineState.Degraded, adapter.State);

        var tokens = new List<string>();
        await foreach (var token in adapter.GenerateTokensAsync("test", 128, CancellationToken.None))
        {
            tokens.Add(token);
        }
        Assert.Empty(tokens);
    }

    [Fact]
    public async Task LocalOnnxChatClient_CustomAdapterInjection_ResolvesViaGetService()
    {
        var fallbackAdapter = new ContextAwareFallbackAdapter("Custom-Injected-Model");
        using var client = new LocalOnnxChatClient(
            modelPath: "any_path",
            modelName: "Custom-Injected-Model",
            engineAdapter: fallbackAdapter);

        var messages = new List<ChatMessage> { new(ChatRole.User, "你好") };
        var resp = await client.GetResponseAsync(messages);

        Assert.NotNull(resp);
        Assert.Equal(fallbackAdapter, client.EngineAdapter);
        Assert.Equal(fallbackAdapter, client.GetService(typeof(IOnnxGenAiEngineAdapter)));
        Assert.Equal(fallbackAdapter, client.GetService(typeof(ContextAwareFallbackAdapter)));

        var caps = client.GetCapabilities();
        Assert.Contains("Context-Aware Fallback", caps.Description);
    }
}
