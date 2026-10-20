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
}
