using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class ChatMultimodalAndMultiTurnToolTests
{
    [Fact]
    public void LocalLlamaChatClient_ImplementsCapabilityProvider_ReportsVisionUnsupportedForPureTextGguf()
    {
        var client = new LocalLlamaChatClient(
            modelPath: "fake/path/Qwen3.5-2B-GPT-5.1-HighIQ-INSTRUCT.i1-Q4_0.gguf",
            modelName: "Qwen3.5-2B-Instruct",
            gpuLayers: 0,
            contextSize: 8192,
            logger: NullLogger.Instance);

        var caps = client.GetCapabilities();

        Assert.False(caps.SupportsVision, "未配对 mmproj 的 GGUF 模型应声明不支持视觉多模态");
        Assert.True(caps.SupportsToolCalling, "应声明支持函数工具调用");
        Assert.Equal("Qwen3.5-2B-Instruct", caps.ModelName);
        Assert.Equal("LocalGguf", caps.BackendType);
        Assert.Equal(8192, caps.MaxContextTokens);
        Assert.Contains("视觉多模态架构", caps.VisionUnsupportedReason);
        Assert.Contains("mmproj", caps.VisionUnsupportedReason);

        // 测试真正的纯文本模型 (如 DeepSeek-R1-Distill)
        var pureClient = new LocalLlamaChatClient(
            modelPath: "fake/path/DeepSeek-R1-Distill-Qwen-1.5B-Q4_K_M.gguf",
            modelName: "DeepSeek-R1-Distill-Qwen-1.5B",
            gpuLayers: 0,
            contextSize: 8192,
            logger: NullLogger.Instance);

        var pureCaps = pureClient.GetCapabilities();
        Assert.False(pureCaps.SupportsVision);
        Assert.Contains("纯语言模型", pureCaps.VisionUnsupportedReason);
    }

    [Fact]
    public void LocalOnnxChatClient_ImplementsCapabilityProvider_ReportsVisionUnsupported()
    {
        var client = new LocalOnnxChatClient(
            modelPath: "fake/onnx/path",
            modelName: "Qwen2.5-1.5B-ONNX",
            acceleration: null,
            logger: NullLogger.Instance);

        var caps = client.GetCapabilities();

        Assert.False(caps.SupportsVision);
        Assert.True(caps.SupportsToolCalling);
        Assert.Equal("LocalOnnx", caps.BackendType);
        Assert.Contains("纯语言模型", caps.VisionUnsupportedReason);
    }

    [Fact]
    public void StripDecorativeBadges_RemovesExecutionBadges_LeavesCleanAssistantText()
    {
        var historicalText = "> 🔧 **已执行工具**: `GetIndexStatus`\n\n知识库当前已成功构建索引：包含 42 篇笔记。";
        var cleaned = ChatToolCallingHelper.StripDecorativeBadges(historicalText);

        Assert.DoesNotContain("> 🔧 **已执行工具**", cleaned);
        Assert.Equal("知识库当前已成功构建索引：包含 42 篇笔记。", cleaned);
    }

    [Fact]
    public void StripDecorativeBadges_RemovesMultipleBadgesAndCallingTags()
    {
        var rawText = "> 🔧 **已执行工具**: `SearchKnowledge`\n> 🔧 **已调用工具**: `BM25Search`\n\n这是检索后的详细答案。";
        var cleaned = ChatToolCallingHelper.StripDecorativeBadges(rawText);

        Assert.DoesNotContain("> 🔧", cleaned);
        Assert.Equal("这是检索后的详细答案。", cleaned);
    }

    [Fact]
    public void BuildChatMlPrompt_StripsDecorativeBadgesFromHistoricalAssistantMessages()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "知识库状态"),
            new(ChatRole.Assistant, "> 🔧 **已执行工具**: `GetIndexStatus`\n\n知识库状态正常，包含 10 个文件。"),
            new(ChatRole.User, "那这些索引包含哪些文件？")
        };

        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(history, null, 4096, 1024);

        Assert.Contains("<|im_start|>assistant\n知识库状态正常，包含 10 个文件。\n<|im_end|>", prompt);
        Assert.DoesNotContain("> 🔧 **已执行工具**", prompt);
    }

    [Theory]
    [InlineData("搜索知识库 神经网络模型", "SearchKnowledge", "神经网络模型")]
    [InlineData("再查一下关于配置的说明", "SearchKnowledge", "配置的说明")]
    [InlineData("再搜索一下关于三端优化的内容", "SearchKnowledge", "三端优化的内容")]
    [InlineData("再检索一下关于RAG的实现", "SearchKnowledge", "RAG的实现")]
    [InlineData("帮我查一下 本地模型部署", "SearchKnowledge", "本地模型部署")]
    [InlineData("从知识库检索 混合搜索策略", "SearchKnowledge", "混合搜索策略")]
    public void TryDetectDirectToolCall_MatchesNonColonNaturalLanguageAndFollowUpQueries(
        string userText, string expectedFuncName, string expectedQuery)
    {
        var dummyTools = CreateMockTools();

        var matched = ChatToolCallingHelper.TryDetectDirectToolCall(
            userText,
            dummyTools,
            out var funcName,
            out var args);

        Assert.True(matched, $"输入 '{userText}' 应被确定性意图分发器成功捕获");
        Assert.Equal(expectedFuncName, funcName);
        Assert.NotNull(args);
        Assert.Equal(expectedQuery, args!["query"]?.ToString());
    }

    [Theory]
    [InlineData("包含哪些文件", "GetFileStructure")]
    [InlineData("知识库有哪些文件", "GetFileStructure")]
    [InlineData("查看文件列表", "GetFileStructure")]
    [InlineData("查询状态", "GetIndexStatus")]
    [InlineData("有多少笔记", "GetIndexStatus")]
    public void TryDetectDirectToolCall_MatchesFileStructureAndIndexStatusNaturalLanguage(
        string userText, string expectedFuncName)
    {
        var dummyTools = CreateMockTools();

        var matched = ChatToolCallingHelper.TryDetectDirectToolCall(
            userText,
            dummyTools,
            out var funcName,
            out var args);

        Assert.True(matched, $"输入 '{userText}' 应被确定性意图分发器捕获为 '{expectedFuncName}'");
        Assert.Equal(expectedFuncName, funcName);
    }

    [Fact]
    public async Task ExactPrefixRoutingStrategy_MatchesFollowUpNaturalLanguage()
    {
        var strategy = new ExactPrefixRoutingStrategy();
        var dummyTools = CreateMockTools();

        var decision = await strategy.RouteAsync("再查一下关于向量索引的配置", dummyTools);

        Assert.True(decision.Handled);
        Assert.Equal("SearchKnowledge", decision.FunctionName);
        Assert.Equal("向量索引的配置", decision.Arguments?["query"]?.ToString());
        Assert.True(decision.ConfidenceScore >= 90);
    }

    private static IList<AITool> CreateMockTools()
    {
        var searchFunc = AIFunctionFactory.Create(
            (string query, int topK) => $"MockSearchResult: {query}",
            "SearchKnowledge",
            "在知识库中检索相关文档");

        var bm25Func = AIFunctionFactory.Create(
            (string keyword, int topK) => $"MockBM25Result: {keyword}",
            "BM25Search",
            "使用 BM25 算法检索知识库");

        var statusFunc = AIFunctionFactory.Create(
            () => "MockIndexStatus: Total=10",
            "GetIndexStatus",
            "获取知识库索引状态");

        var structFunc = AIFunctionFactory.Create(
            () => "MockFileStructure: doc1.md, doc2.md",
            "GetFileStructure",
            "获取文件目录结构");

        return [searchFunc, bm25Func, statusFunc, structFunc];
    }

    [Fact]
    public void BuildChatMlPrompt_WithVisionSupport_EmbedsMediaMarkerAndDoesNotInjectFakeDegradationText()
    {
        var dummyBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var message = new ChatMessage(ChatRole.User, [
            new TextContent("请查看这张图片内容并进行分析"),
            new DataContent(dummyBytes, "image/jpeg")
        ]);

        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(
            [message],
            null,
            4096,
            1024,
            modelName: "Qwen3.5-4B-Instruct",
            supportsVision: true);

        // 验证具备视觉能力时按 llama.cpp MTMD 规范注入 <__media__>
        Assert.Contains("<__media__>", prompt);
        Assert.Contains("请查看这张图片内容并进行分析", prompt);

        // 验证绝对不得注入假降级提示词，杜绝诱导模型产生“无法访问外部图像”的幻觉解释
        Assert.DoesNotContain("本地文本引擎未挂载视觉投影器", prompt);
        Assert.DoesNotContain("已优先基于文本与知识库为您解答", prompt);
    }

    [Fact]
    public void BuildChatMlPrompt_WithoutVisionSupport_AnnotatesDegradationMetadataForTextModels()
    {
        var dummyBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var message = new ChatMessage(ChatRole.User, [
            new TextContent("你好"),
            new DataContent(dummyBytes, "image/png")
        ]);

        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(
            [message],
            null,
            4096,
            1024,
            modelName: "DeepSeek-R1-Distill",
            supportsVision: false);

        // 验证纯文本引擎下不注入 <__media__> 避免引起纯文本模型分词异常
        Assert.DoesNotContain("<__media__>", prompt);
        // 验证生成降级占位与解释
        Assert.Contains("[附带图像 #1 (image/png)", prompt);
        Assert.Contains("本地文本引擎未挂载视觉投影器", prompt);
    }

    [Fact]
    public async Task LocalLlamaChatClient_SimulationMode_WithImages_EmitsVisionAwareTokens()
    {
        var client = new LocalLlamaChatClient(
            modelPath: "non_existent_vision_model.gguf",
            modelName: "Qwen2.5-VL-7B-Instruct",
            logger: NullLogger.Instance);

        var dummyBytes = new byte[] { 1, 2, 3, 4 };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [
                new TextContent("这张图里是什么？"),
                new DataContent(dummyBytes, "image/jpeg")
            ])
        };

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages))
        {
            chunks.Add(update);
        }

        var fullText = string.Concat(chunks.Select(c => c.Text));
        Assert.Contains("已成功接收并解析附带图片", fullText);
        Assert.Contains("1 张图片，多模态投影器就绪", fullText);
        Assert.DoesNotContain("本地文本引擎未挂载视觉投影器", fullText);
    }

    [Fact]
    public async Task RealModel_Qwen35_4B_Gguf_WithProjector_CanInitializeAndInferAsync()
    {
        var modelDir = @"E:\LinuxWork\Obsidian\resource\data\models\Chat\Qwen3.5-4B-Instruct-GGUF";
        var modelPath = Path.Combine(modelDir, "Qwen3.5-4B-Revised-q4_k_m.gguf");
        var mmprojPath = Path.Combine(modelDir, "mmproj-f16.gguf");

        if (!File.Exists(modelPath) || !File.Exists(mmprojPath)) return;

        using var client = new LocalLlamaChatClient(
            modelPath: modelPath,
            modelName: "Qwen3.5-4B-Instruct-GGUF",
            gpuLayers: 0,
            contextSize: 2048,
            mmprojPath: mmprojPath,
            logger: NullLogger.Instance);

        var caps = client.GetCapabilities();
        Assert.True(caps.SupportsVision, "Model should report vision supported");
        Assert.Equal("PairedReady", caps.VisionStatus);

        var validPngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, [
                new TextContent("请用中文简要说明图片内容"),
                new DataContent(validPngBytes, "image/png")
            ])
        };

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages))
        {
            chunks.Add(update);
            Console.WriteLine($"[TEST CHUNK] Text='{update.Text}' ContentsCount={update.Contents.Count} Type={update.Contents.FirstOrDefault()?.GetType().Name}");
        }

        Console.WriteLine($"[TEST SUMMARY] Total chunks: {chunks.Count}");
        Assert.NotEmpty(chunks);
        var fullText = string.Concat(chunks.Select(c => c.Text));
        Console.WriteLine($"[TEST SUMMARY] fullText='{fullText}'");
        Assert.False(string.IsNullOrWhiteSpace(fullText), "Output should not be empty");
    }

    [Fact]
    public async Task RealModel_Qwen35_4B_ReproduceUserScenario_WithUserScreenshotAndToolsAsync()
    {
        var modelDir = @"E:\LinuxWork\Obsidian\resource\data\models\Chat\Qwen3.5-4B-Instruct-GGUF";
        var modelPath = Path.Combine(modelDir, "Qwen3.5-4B-Revised-q4_k_m.gguf");
        var mmprojPath = Path.Combine(modelDir, "mmproj-f16.gguf");
        var imagePath = @"C:\Users\hlr\.gemini\antigravity\brain\beff1303-40d8-440e-a953-14d083b0fccc\.user_uploaded\media_1792569497395_c9943e5a.png";

        if (!File.Exists(modelPath) || !File.Exists(mmprojPath) || !File.Exists(imagePath)) return;

        using var client = new LocalLlamaChatClient(
            modelPath: modelPath,
            modelName: "Qwen3.5-4B-Instruct-GGUF",
            gpuLayers: 0,
            contextSize: 16384,
            batchSize: 2048,
            uBatchSize: 2048,
            mmprojPath: mmprojPath,
            logger: NullLogger.Instance);

        var imageBytes = await File.ReadAllBytesAsync(imagePath);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是 ReferenceRAG 智能助手。"),
            new(ChatRole.User, [
                new TextContent("介绍图片内容"),
                new DataContent(imageBytes, "image/png")
            ])
        };

        var options = new ChatOptions
        {
            Tools = CreateMockTools(),
            Temperature = 0.7f,
            MaxOutputTokens = 4096
        };

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            chunks.Add(update);
            Console.WriteLine($"[USER SCENARIO CHUNK] Text='{update.Text}' ContentsCount={update.Contents.Count} Type={update.Contents.FirstOrDefault()?.GetType().Name}");
        }

        Console.WriteLine($"[USER SCENARIO SUMMARY] Total chunks: {chunks.Count}");
        var fullText = string.Concat(chunks.Select(c => c.Text));
        Console.WriteLine($"[USER SCENARIO SUMMARY] fullText='{fullText}'");
        Assert.NotEmpty(chunks);
        Assert.False(string.IsNullOrWhiteSpace(fullText), "Output should not be empty");
    }
}
