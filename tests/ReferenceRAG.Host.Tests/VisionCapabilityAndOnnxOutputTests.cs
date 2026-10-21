using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class VisionCapabilityAndOnnxOutputTests : IDisposable
{
    private readonly string _tempTestDir;

    public VisionCapabilityAndOnnxOutputTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "VisionCapabilityTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempTestDir))
            {
                Directory.Delete(_tempTestDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void GgufVisionMatchingStrategy_WhenPairedMmprojExists_ReportsPairedReady()
    {
        var modelDir = Path.Combine(_tempTestDir, "qwen_model");
        Directory.CreateDirectory(modelDir);
        var modelFile = Path.Combine(modelDir, "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf");
        var mmprojFile = Path.Combine(modelDir, "mmproj-Qwen2.5-VL-7B-Instruct-f16.gguf");
        File.WriteAllText(modelFile, "dummy");
        File.WriteAllText(mmprojFile, "dummy");

        var strategy = new GgufVisionMatchingStrategy();
        var result = strategy.Match("Qwen2.5-VL-7B-Instruct", modelFile);

        Assert.True(result.SupportsVision);
        Assert.Equal(VisionModelStatus.PairedReady, result.Status);
        Assert.NotNull(result.MatchedProjectorPath);
        Assert.Contains("mmproj-Qwen2.5-VL-7B-Instruct-f16.gguf", result.MatchedProjectorPath);
    }

    [Fact]
    public void GgufVisionMatchingStrategy_WhenMmprojMissingForVisionModel_ReportsUnpaired()
    {
        var modelDir = Path.Combine(_tempTestDir, "qwen_unpaired");
        Directory.CreateDirectory(modelDir);
        var modelFile = Path.Combine(modelDir, "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf");
        File.WriteAllText(modelFile, "dummy");

        var strategy = new GgufVisionMatchingStrategy();
        var result = strategy.Match("Qwen2.5-VL-7B-Instruct", modelFile);

        Assert.False(result.SupportsVision);
        Assert.Equal(VisionModelStatus.Unpaired, result.Status);
        Assert.Contains("mmproj", result.GuidanceMessage);
    }

    [Fact]
    public void OnnxVisionMatchingStrategy_WhenVisionGraphExists_ReportsPairedReady()
    {
        var onnxDir = Path.Combine(_tempTestDir, "phi3v_onnx");
        Directory.CreateDirectory(onnxDir);
        File.WriteAllText(Path.Combine(onnxDir, "model.onnx"), "dummy");
        File.WriteAllText(Path.Combine(onnxDir, "vision_encoder.onnx"), "dummy");

        var strategy = new OnnxVisionMatchingStrategy();
        var result = strategy.Match("Phi-3.5-vision-instruct", onnxDir);

        Assert.True(result.SupportsVision);
        Assert.Equal(VisionModelStatus.PairedReady, result.Status);
    }

    [Fact]
    public void OnnxVisionMatchingStrategy_WhenGenAiConfigDeclaresVision_ReportsPairedReady()
    {
        var onnxDir = Path.Combine(_tempTestDir, "genai_vlm");
        Directory.CreateDirectory(onnxDir);
        File.WriteAllText(Path.Combine(onnxDir, "genai_config.json"), "{\"model\": {\"type\": \"phi3v\"}}");

        var strategy = new OnnxVisionMatchingStrategy();
        var result = strategy.Match("Phi-3.5-vision", onnxDir);

        Assert.True(result.SupportsVision);
        Assert.Equal(VisionModelStatus.PairedReady, result.Status);
    }

    [Fact]
    public void ExternalApiVisionMatchingStrategy_CorrectlyDistinguishesVisionFromText()
    {
        var strategy = new ExternalApiVisionMatchingStrategy();

        var gpt4o = strategy.Match("gpt-4o", null);
        Assert.True(gpt4o.SupportsVision);
        Assert.Equal(VisionModelStatus.PairedReady, gpt4o.Status);

        var gemini = strategy.Match("gemini-1.5-pro", null);
        Assert.True(gemini.SupportsVision);
        Assert.Equal(VisionModelStatus.PairedReady, gemini.Status);

        var deepseek = strategy.Match("deepseek-chat", null);
        Assert.False(deepseek.SupportsVision);
        Assert.Equal(VisionModelStatus.TextOnly, deepseek.Status);
    }

    [Fact]
    public void CompositeVisionCapabilityMatcher_RoutesAccuratelyByBackend()
    {
        var matcher = new CompositeVisionCapabilityMatcher();

        var apiResult = matcher.Match("OpenAiApi", "gpt-4o-mini", null);
        Assert.True(apiResult.SupportsVision);

        var onnxResult = matcher.Match("LocalOnnxGenAi", "Phi-3.5-mini-instruct-onnx", null);
        Assert.False(onnxResult.SupportsVision);
        Assert.Equal(VisionModelStatus.TextOnly, onnxResult.Status);
    }

    [Fact]
    public async Task LocalOnnxChatClient_GreetingQuery_ReturnsNaturalLanguageAndNoHardcodedStub()
    {
        using var client = new LocalOnnxChatClient(
            modelPath: _tempTestDir,
            modelName: "Phi-3.5-mini-instruct-onnx",
            logger: NullLogger.Instance);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "你好")
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        var text = response.Text;
        Assert.NotNull(text);

        // 验证不再输出硬编码死文本
        Assert.DoesNotContain("已通过本地 ONNX 引擎完成推理解码。\n\n", text);
        Assert.DoesNotContain("[ONNX Phi-3.5-mini-instruct-onnx] 已通过本地 ONNX 引擎完成推理解码。", text);

        // 验证输出自然语言回复与模型名称感知
        Assert.Contains("Phi-3.5-mini-instruct-onnx", text);
        Assert.True(text.Contains("您好") || text.Contains("你好"), "打招呼应返回礼貌问候");
    }

    [Fact]
    public async Task LocalOnnxChatClient_KnowledgeSearchIntent_EmitsToolCall()
    {
        using var client = new LocalOnnxChatClient(
            modelPath: _tempTestDir,
            modelName: "Phi-3.5-mini-instruct-onnx",
            logger: NullLogger.Instance);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "搜索知识库：微调指南")
        };

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create((string query) => Task.FromResult("content"), "SearchKnowledge", "检索知识库")
        };

        var options = new ChatOptions { Tools = tools };

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            chunks.Add(update);
        }

        var funcCall = chunks.SelectMany(c => c.Contents.OfType<FunctionCallContent>()).FirstOrDefault();
        Assert.NotNull(funcCall);
        Assert.Equal("SearchKnowledge", funcCall.Name);
    }

    [Fact]
    public async Task LocalOnnxChatClient_ToolResultSummarization_SynthesizesNaturalSummary()
    {
        using var client = new LocalOnnxChatClient(
            modelPath: _tempTestDir,
            modelName: "Phi-3.5-mini-instruct-onnx",
            logger: NullLogger.Instance);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "查询知识库索引状态"),
            new(ChatRole.Assistant, [new FunctionCallContent("call_1", "GetIndexStatus", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("call_1", "当前向量索引包含 88 个分块，BM25 包含 12 篇文档。")])
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        var text = response.Text;
        Assert.NotNull(text);
        Assert.Contains("88 个分块", text);
        Assert.Contains("12 篇文档", text);
    }

    [Fact]
    public void LocalOnnxChatClient_GetCapabilities_IncludesVisionStatus()
    {
        using var client = new LocalOnnxChatClient(
            modelPath: _tempTestDir,
            modelName: "Phi-3.5-mini-instruct-onnx",
            logger: NullLogger.Instance);

        var caps = client.GetCapabilities();

        Assert.NotNull(caps);
        Assert.False(caps.SupportsVision);
        Assert.Equal("TextOnly", caps.VisionStatus);
        Assert.Equal("LocalOnnx", caps.BackendType);
    }

    [Fact]
    public async Task NativeOgaDirectMlAdapter_WithRealPhi35Model_LoadsAndGeneratesTokens()
    {
        var realDir = @"E:\LinuxWork\Obsidian\resource\data\models\Chat\Phi-3.5-mini-instruct-onnx\gpu\gpu-int4-awq-block-128";
        if (!Directory.Exists(realDir)) return;

        var adapter = new NativeOgaDirectMlAdapter(NullLogger.Instance);
        var inited = await adapter.InitializeAsync(realDir, null, System.Threading.CancellationToken.None);
        Assert.True(inited, "DirectML OGA initialization should succeed");
        Assert.Equal(OnnxEngineState.Ready, adapter.State);

        var prompt = "<|system|>\n你是 ReferenceRAG 智能助手。<|end|>\n<|user|>\n你会什么？<|end|>\n<|assistant|>\n";
        
        using var testModel = new Microsoft.ML.OnnxRuntimeGenAI.Model(realDir);
        using var testTokenizer = new Microsoft.ML.OnnxRuntimeGenAI.Tokenizer(testModel);
        using var seqs = testTokenizer.Encode(prompt);
        var promptLen = seqs[0].Length;
        Assert.True(promptLen > 0, $"Prompt token count: {promptLen}");

        var tokens = new List<string>();
        await foreach (var token in adapter.GenerateTokensAsync(prompt, 50, System.Threading.CancellationToken.None))
        {
            tokens.Add(token);
        }

        var fullText = string.Join("", tokens);
        Assert.NotEmpty(tokens);
        Assert.False(string.IsNullOrWhiteSpace(fullText));
        // Verify real generative output (not template fallback)
        Assert.DoesNotContain("已收到您的问题：“你会什么？”", fullText);
        Assert.True(fullText.Length > 5, $"Generated output: '{fullText}'");
        adapter.Unload();
    }

    [Fact]
    public async Task LocalOnnxChatClient_WithParentDirectory_AutoResolvesAndLoadsRealOgaEngine()
    {
        var parentDir = @"E:\LinuxWork\Obsidian\resource\data\models\Chat\Phi-3.5-mini-instruct-onnx";
        if (!Directory.Exists(parentDir)) return;

        using var client = new LocalOnnxChatClient(
            modelPath: parentDir, // Note: passing parent directory that contains gpu/gpu-int4-awq-block-128
            modelName: "Phi-3.5-mini-instruct-onnx",
            logger: NullLogger.Instance);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "你好，请简单介绍一下自己。")
        };

        var response = await client.GetResponseAsync(messages);
        Assert.NotNull(response);
        Assert.False(client.IsSimulationMode, "Should resolve OGA directory and run in native accelerated mode!");
        Assert.True(client.IsModelLoaded, "Model should be loaded natively");
        Assert.DoesNotContain("已收到您的问题", response.Text);
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }
}
