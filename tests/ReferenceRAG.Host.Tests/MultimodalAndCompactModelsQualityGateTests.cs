using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Core.Features.HardwareAcceleration;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 多模态 Agent 视觉输入与 2026 前沿端侧小模型注册表自动化质量门禁
/// </summary>
public class MultimodalAndCompactModelsQualityGateTests
{
    private static readonly string Test1x1PngDataUri =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    [Fact]
    public void PredefinedModels_ContainsAllEight2026CompactAndVisionModels()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "referencerag_gate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configMgr = new ConfigManager(customConfigPath: Path.Combine(tempDir, "appsettings.json"));
            using var modelMgr = new ModelManager(tempDir, configMgr);
            var chatModels = modelMgr.GetChatModels();

            var expectedModels = new Dictionary<string, (long MinSize, long MaxSize, int MinContext)>
            {
                ["FunctionGemma-270M-IT"] = (250L * 1024 * 1024, 320L * 1024 * 1024, 32768),
                ["LFM2.5-350M-Instruct-GGUF"] = (200L * 1024 * 1024, 260L * 1024 * 1024, 32768),
                ["Qwen3.5-0.8B-Instruct-GGUF"] = (500L * 1024 * 1024, 650L * 1024 * 1024, 262144),
                ["LFM2.5-1.2B-Instruct-GGUF"] = (650L * 1024 * 1024, 800L * 1024 * 1024, 32768),
                ["Bonsai-2-2B-GGUF"] = (400L * 1024 * 1024, 550L * 1024 * 1024, 262144),
                ["Qwen3.5-2B-Instruct-GGUF"] = (1000L * 1024 * 1024, 1500L * 1024 * 1024, 262144),
                ["Qwen3.5-4B-Instruct-GGUF"] = (2500L * 1024 * 1024, 3000L * 1024 * 1024, 262144),
                ["Gemma-4-E2B-IT-GGUF"] = (3000L * 1024 * 1024, 3500L * 1024 * 1024, 128000)
            };

            foreach (var kv in expectedModels)
            {
                var match = chatModels.FirstOrDefault(m => string.Equals(m.Name, kv.Key, StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(match);
                Assert.Equal("chat", match.ModelType);
                Assert.False(string.IsNullOrWhiteSpace(match.DisplayName));
                Assert.False(string.IsNullOrWhiteSpace(match.Description));
                Assert.False(string.IsNullOrWhiteSpace(match.DownloadUrl));
                Assert.InRange(match.ModelSizeBytes, kv.Value.MinSize, kv.Value.MaxSize);
                Assert.True(match.MaxSequenceLength >= kv.Value.MinContext, $"{kv.Key} max context length is {match.MaxSequenceLength}, expected >= {kv.Value.MinContext}");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CompactModels_SizeOrderingAndPicks_AreConsistent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "referencerag_gate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configMgr = new ConfigManager(customConfigPath: Path.Combine(tempDir, "appsettings.json"));
            using var modelMgr = new ModelManager(tempDir, configMgr);
            var chatModels = modelMgr.GetChatModels();

            var m350m = chatModels.First(m => m.Name == "LFM2.5-350M-Instruct-GGUF");
            var m270m = chatModels.First(m => m.Name == "FunctionGemma-270M-IT");
            var mBonsai = chatModels.First(m => m.Name == "Bonsai-2-2B-GGUF");
            var m08b = chatModels.First(m => m.Name == "Qwen3.5-0.8B-Instruct-GGUF");
            var m12b = chatModels.First(m => m.Name == "LFM2.5-1.2B-Instruct-GGUF");
            var m2b = chatModels.First(m => m.Name == "Qwen3.5-2B-Instruct-GGUF");
            var m4b = chatModels.First(m => m.Name == "Qwen3.5-4B-Instruct-GGUF");
            var mGemma = chatModels.First(m => m.Name == "Gemma-4-E2B-IT-GGUF");

            // 极致体积首选: LFM2.5 350M 体积最小
            Assert.True(m350m.ModelSizeBytes < m270m.ModelSizeBytes);
            Assert.True(m270m.ModelSizeBytes < mBonsai.ModelSizeBytes);
            Assert.True(mBonsai.ModelSizeBytes < m08b.ModelSizeBytes);
            Assert.True(m08b.ModelSizeBytes < m12b.ModelSizeBytes);
            Assert.True(m12b.ModelSizeBytes < m2b.ModelSizeBytes);
            Assert.True(m2b.ModelSizeBytes < m4b.ModelSizeBytes);
            Assert.True(m4b.ModelSizeBytes < mGemma.ModelSizeBytes);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void MultimodalMessageAdapter_AdaptsPlainText_ReturnsSimpleChatMessage()
    {
        var adapter = new MultimodalMessageAdapter();
        var msg = adapter.AdaptUserMessage("用户纯文本提问", null);

        Assert.Equal(ChatRole.User, msg.Role);
        Assert.Equal("用户纯文本提问", msg.Text);
        Assert.DoesNotContain(msg.Contents, c => c is DataContent || c is UriContent);
    }

    [Fact]
    public void MultimodalMessageAdapter_AdaptsBase64DataUri_ReturnsDataContent()
    {
        var adapter = new MultimodalMessageAdapter();
        var msg = adapter.AdaptUserMessage("分析该图片", [Test1x1PngDataUri]);

        Assert.Equal(ChatRole.User, msg.Role);
        Assert.Contains("分析该图片", msg.Text);
        
        var dataContent = msg.Contents.OfType<DataContent>().FirstOrDefault();
        Assert.NotNull(dataContent);
        Assert.Equal("image/png", dataContent.MediaType);
        Assert.False(dataContent.Data.IsEmpty);
    }

    [Fact]
    public void MultimodalMessageAdapter_AdaptsHttpUrl_ReturnsUriContent()
    {
        var adapter = new MultimodalMessageAdapter();
        var msg = adapter.AdaptUserMessage("请解读远程图片", ["https://example.com/sample.jpg"]);

        Assert.Equal(ChatRole.User, msg.Role);
        var uriContent = msg.Contents.OfType<UriContent>().FirstOrDefault();
        Assert.NotNull(uriContent);
        Assert.Equal("https://example.com/sample.jpg", uriContent.Uri.AbsoluteUri);
    }

    [Fact]
    public void MultimodalMessageAdapter_ValidatesImageCountAndSize()
    {
        var adapter = new MultimodalMessageAdapter();

        // 正常情况：2 张合法图片
        var (ok1, err1) = adapter.ValidateImages([Test1x1PngDataUri, Test1x1PngDataUri]);
        Assert.True(ok1);
        Assert.Null(err1);

        // 超过单次 4 张上限
        var tooMany = Enumerable.Repeat(Test1x1PngDataUri, 5).ToList();
        var (ok2, err2) = adapter.ValidateImages(tooMany);
        Assert.False(ok2);
        Assert.Contains("不能超过 4 张", err2);

        // 超大图片限制
        var fakeHugeBase64 = "data:image/jpeg;base64," + new string('A', 15 * 1024 * 1024); // ~11MB
        var (ok3, err3) = adapter.ValidateImages([fakeHugeBase64]);
        Assert.False(ok3);
        Assert.Contains("大小超过上限", err3);
    }

    [Fact]
    public void ChatToolCallingHelper_GracefulDegradation_AnnotatesPromptForTextModels()
    {
        var adapter = new MultimodalMessageAdapter();
        var userMsg = adapter.AdaptUserMessage("请帮我提取图片中的文字", [Test1x1PngDataUri]);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "你是一个智能助手"),
            userMsg
        };

        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(messages);

        // 验证降级提示词与用户输入均完整保留，且未发生运行时异常
        Assert.Contains("<|im_start|>user", prompt);
        Assert.Contains("[附带图像 #1 (image/png)", prompt);
        Assert.Contains("未挂载视觉投影器", prompt);
        Assert.Contains("请帮我提取图片中的文字", prompt);
        Assert.Contains("<|im_end|>", prompt);
    }

    [Fact]
    public void ChatStreamRequest_JsonContract_SupportsImagesArray()
    {
        var json = """
        {
            "sessionId": "test-session-123",
            "message": "带图片的请求",
            "images": [
                "data:image/png;base64,ABC"
            ]
        }
        """;

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var request = JsonSerializer.Deserialize<ChatStreamRequest>(json, options);

        Assert.NotNull(request);
        Assert.Equal("test-session-123", request.SessionId);
        Assert.Equal("带图片的请求", request.Message);
        Assert.NotNull(request.Images);
        Assert.Single(request.Images);
        Assert.Equal("data:image/png;base64,ABC", request.Images[0]);
    }

    [Fact]
    public void ChatPerformancePlanner_PlansSensibleProfilesForCompactAndVisionModels()
    {
        var planner = new ChatPerformancePlanner();

        var modelsToTest = new[]
        {
            "FunctionGemma-270M-IT",
            "LFM2.5-350M-Instruct-GGUF",
            "Qwen3.5-0.8B-Instruct-GGUF",
            "LFM2.5-1.2B-Instruct-GGUF",
            "Bonsai-2-2B-GGUF",
            "Qwen3.5-2B-Instruct-GGUF",
            "Qwen3.5-4B-Instruct-GGUF",
            "Gemma-4-E2B-IT-GGUF"
        };

        foreach (var model in modelsToTest)
        {
            var profile = planner.PlanProfile(null, model, useGpu: false);
            Assert.NotNull(profile);
            Assert.True(profile.ContextSize >= 1024);
            Assert.True(profile.BatchSize >= 256);
            Assert.False(string.IsNullOrWhiteSpace(profile.Rationale));
        }
    }

    [Fact]
    public async Task MafChatService_StreamAsync_WithTooManyImages_YieldsValidationError()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:ApiKey"] = "fake-key"
            })
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .BuildServiceProvider();

        var service = new MafChatService(
            configuration,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IHttpClientFactory>(),
            services.GetRequiredService<ILogger<MafChatService>>());

        var sessionId = service.CreateSession();
        var tooMany = Enumerable.Repeat(Test1x1PngDataUri, 5).ToList();

        var events = new List<SseEvent>();
        await foreach (var evt in service.StreamAsync(sessionId, "带5张图片的提问", tooMany))
        {
            events.Add(evt);
        }

        Assert.NotEmpty(events);
        var err = events.FirstOrDefault(e => e.Type == "error");
        Assert.NotNull(err);
        Assert.Contains("不能超过 4 张", err.Message);
        Assert.Equal("done", events.Last().Type);
    }
}
