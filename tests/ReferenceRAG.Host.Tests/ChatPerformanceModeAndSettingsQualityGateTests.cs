using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Core.Features.HardwareAcceleration;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 质量门禁：聊天模型自适应规划与固定参数模式（双模）及设置端点质量门禁
/// </summary>
[Collection("CurrentDirectoryCollection")]
public sealed class ChatPerformanceModeAndSettingsQualityGateTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _originalDir;

    public ChatPerformanceModeAndSettingsQualityGateTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"chat-perf-mode-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_originalDir))
                Directory.SetCurrentDirectory(_originalDir);
            else
                Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        }
        catch { }
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    private static HardwareContext CreateGpuHardware(ulong totalVramBytes)
    {
        return new HardwareContext
        {
            HasNvidiaGpu = true,
            HasDirectX12 = true,
            PrimaryGpu = new GpuDeviceInfo
            {
                Name = "NVIDIA GeForce RTX 4070",
                Vendor = GpuVendor.Nvidia,
                TotalVramBytes = totalVramBytes,
                IsPrimary = true
            }
        };
    }

    [Fact]
    public async Task Reproduce_LocalLlama_NoKvSlot()
    {
        var modelPath = @"E:\LinuxWork\Obsidian\resource\data\models\Chat\Qwen2.5-1.5B-Instruct-GGUF\qwen2.5-1.5b-instruct-q4_0.gguf";
        if (!File.Exists(modelPath)) return;

        var rawClient = new ReferenceRAG.Business.Features.Chat.Services.LocalLlamaChatClient(
            modelPath: modelPath,
            modelName: "Qwen2.5-1.5B-Instruct-GGUF",
            gpuLayers: -1,
            contextSize: 32768,
            batchSize: 2048,
            uBatchSize: 512,
            logger: NullLogger.Instance);

        var client = rawClient.AsBuilder().UseFunctionInvocation().Build();

        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "你有哪些工具？"),
            new ChatMessage(ChatRole.Assistant, @"我有以下工具:
• SearchKnowledge: 语义搜索知识库，返回相关笔记片段及来源元数据（文件路径、行号、refId）。
• DrillDownChunk: 按 refId 展开某个检索结果的完整上下文。
• BM25Search: BM25 关键词精确搜索。
• SearchNoteByTitle: 按笔记标题关键词查找知识图谱节点。
• GetFileStructure: 获取一批文件的章节目录结构（标题 + 行号范围），不含正文。
• ReadFileContent: 按行范围精准读取文件内容。
• GetIndexStatus: 获取知识库当前索引状态：向量分块总数、BM25 文档总数化。"),
            new ChatMessage(ChatRole.User, "搜索: 你好")
        };

        // 模拟超长知识库检索结果（RAG 典型大文本注入，如多个千字分块或完整文件内容）
        var largeSearchResult = new System.Text.StringBuilder();
        largeSearchResult.AppendLine("## 语义检索结果（共 10 条）\n");
        for (int i = 0; i < 10; i++)
        {
            largeSearchResult.AppendLine($"[{i + 1}] 《超长知识文档_{i}》");
            largeSearchResult.AppendLine(new string('测', 3000));
        }

        var options = new ChatOptions
        {
            Tools = new List<AITool>
            {
                AIFunctionFactory.Create(([System.ComponentModel.Description("查询词")] string query, int topK = 10) => largeSearchResult.ToString(), "SearchKnowledge"),
                AIFunctionFactory.Create((string refId, string query) => "result", "DrillDownChunk"),
                AIFunctionFactory.Create((string query, int topK = 10) => "result", "BM25Search"),
                AIFunctionFactory.Create((string query) => "result", "SearchNoteByTitle"),
                AIFunctionFactory.Create((string directoryPath) => "result", "GetFileStructure"),
                AIFunctionFactory.Create((string filePath, int startLine, int endLine) => "result", "ReadFileContent"),
                AIFunctionFactory.Create(() => "result", "GetIndexStatus")
            }
        };

        var sb = new System.Text.StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(messages, options))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                sb.Append(update.Text);
            }
        }

        Assert.True(sb.Length > 0);
    }

    [Fact]
    public void ChatToolCallingHelper_CapsOversizedToolResponseToContextBudget()
    {
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.User, "查询大量资料"),
            new ChatMessage(ChatRole.Assistant, "<tool_call>{\"name\":\"SearchKnowledge\",\"arguments\":{\"query\":\"test\"}}</tool_call>"),
            new ChatMessage(ChatRole.Tool, new string('A', 50000))
        };

        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(messages, null, maxContextTokens: 4096, maxOutputTokens: 1024);

        Assert.True(prompt.Length < 10000);
        Assert.Contains("<tool_response>", prompt);
        Assert.Contains("已自动截断", prompt);
        Assert.EndsWith("<|im_start|>assistant\n", prompt);
    }

    [Fact]
    public void ChatToolCallingHelper_SlidingWindowPrunesEarlyHistoryWhenExceedingBudget()
    {
        var messages = new List<ChatMessage>();
        for (int i = 0; i < 30; i++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"历史问题 {i}: " + new string('Q', 300)));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"历史回答 {i}: " + new string('A', 300)));
        }
        messages.Add(new ChatMessage(ChatRole.User, "最新的核心提问"));

        var prompt = ChatToolCallingHelper.BuildChatMlPrompt(messages, null, maxContextTokens: 4096, maxOutputTokens: 1024);

        Assert.Contains("最新的核心提问", prompt);
        Assert.Contains("已自动压缩并省略较早的若干轮历史对话", prompt);
        Assert.True(prompt.Length < 10000);
    }

    [Fact]
    public void ChatToolCallingHelper_SyntaxSelfHealing_RepairsBrokenMarkdownAndTags()
    {
        // 模拟断裂的代码块与未闭合的工具标签
        var brokenText = "这里是前半句内容。```csharp\npublic void Test() {\n    int a = 1;";
        var healed = ChatToolCallingHelper.RepairTruncatedMarkdownAndXml(brokenText);
        Assert.EndsWith("```\n", healed);

        var brokenTool = "<tool_response>{\"call_id\": \"123\", \"content\": \"partial";
        var healedTool = ChatToolCallingHelper.RepairTruncatedMarkdownAndXml(brokenTool);
        Assert.Contains("</tool_response>", healedTool);

        // 测试自然句尾标点断点
        var sample = "第一句完整。第二句也完整！第三句说了一半但是很长很长很长很长很长";
        int cut = ChatToolCallingHelper.FindNaturalBreakpoint(sample, 25);
        Assert.Equal("第一句完整。第二句也完整！".Length, cut);
    }

    [Fact]
    public void ChatPerformancePlanner_AdaptiveBalancedStrategy_Yields8KOrBetter()
    {
        var planner = new ChatPerformancePlanner();
        var hw = CreateGpuHardware(8UL * 1024 * 1024 * 1024);

        var request = new ChatPerformancePlanningRequest
        {
            ModelName = "Qwen2.5-7B-Instruct-GGUF",
            HardwareContext = hw,
            UseGpu = true,
            PerformanceMode = "Adaptive",
            AdaptiveStrategy = "Balanced"
        };

        var profile = planner.PlanProfile(request);

        Assert.Equal("Adaptive", profile.Mode);
        Assert.Equal("Balanced", profile.Strategy);
        Assert.Equal(8192, profile.ContextSize);
        Assert.Equal(2048, profile.BatchSize);
        Assert.Equal(512, profile.UBatchSize);
        Assert.Equal(-1, profile.GpuLayers);
        Assert.Null(profile.Warning);
    }

    [Fact]
    public void ChatPerformancePlanner_AdaptiveHighThroughputStrategy_YieldsHighThroughputWindow()
    {
        var planner = new ChatPerformancePlanner();
        var hw = CreateGpuHardware(8UL * 1024 * 1024 * 1024);

        var request = new ChatPerformancePlanningRequest
        {
            ModelName = "Qwen2.5-7B-Instruct-GGUF",
            HardwareContext = hw,
            UseGpu = true,
            PerformanceMode = "Adaptive",
            AdaptiveStrategy = "HighThroughput"
        };

        var profile = planner.PlanProfile(request);

        Assert.Equal("Adaptive", profile.Mode);
        Assert.Equal("HighThroughput", profile.Strategy);
        Assert.Equal(4096, profile.ContextSize);
        Assert.Equal(2048, profile.BatchSize);
        Assert.Equal(512, profile.UBatchSize);
        Assert.Equal(-1, profile.GpuLayers);
        Assert.Contains("高吞吐", profile.Rationale);
    }

    [Fact]
    public void ChatPerformancePlanner_AdaptiveLongContextStrategy_ExpandsWindow()
    {
        var planner = new ChatPerformancePlanner();
        // 16GB 显卡 + 7B 模型
        var hw = CreateGpuHardware(16UL * 1024 * 1024 * 1024);

        var request = new ChatPerformancePlanningRequest
        {
            ModelName = "Qwen2.5-7B-Instruct-GGUF",
            HardwareContext = hw,
            UseGpu = true,
            PerformanceMode = "Adaptive",
            AdaptiveStrategy = "LongContext"
        };

        var profile = planner.PlanProfile(request);

        Assert.Equal("Adaptive", profile.Mode);
        Assert.Equal("LongContext", profile.Strategy);
        Assert.True(profile.ContextSize >= 16384, $"Expected ContextSize >= 16384, got {profile.ContextSize}");
        Assert.Equal(-1, profile.GpuLayers);
    }

    [Fact]
    public void ChatPerformancePlanner_AdaptiveLowMemoryStrategy_YieldsLightweightWindow()
    {
        var planner = new ChatPerformancePlanner();
        var hw = CreateGpuHardware(6UL * 1024 * 1024 * 1024);

        var request = new ChatPerformancePlanningRequest
        {
            ModelName = "Qwen2.5-7B-Instruct-GGUF",
            HardwareContext = hw,
            UseGpu = true,
            PerformanceMode = "Adaptive",
            AdaptiveStrategy = "LowMemory"
        };

        var profile = planner.PlanProfile(request);

        Assert.Equal("Adaptive", profile.Mode);
        Assert.Equal("LowMemory", profile.Strategy);
        Assert.True(profile.ContextSize <= 4096, $"Expected ContextSize <= 4096, got {profile.ContextSize}");
        Assert.True(profile.BatchSize <= 1024, $"Expected BatchSize <= 1024, got {profile.BatchSize}");
        Assert.True(profile.UBatchSize <= 256, $"Expected UBatchSize <= 256, got {profile.UBatchSize}");
    }

    [Fact]
    public void ChatPerformancePlanner_FixedMode_WithinBudget_HonorsUserParameters()
    {
        var planner = new ChatPerformancePlanner();
        var hw = CreateGpuHardware(12UL * 1024 * 1024 * 1024);

        var request = new ChatPerformancePlanningRequest
        {
            ModelName = "Qwen2.5-7B-Instruct-GGUF",
            HardwareContext = hw,
            UseGpu = true,
            PerformanceMode = "Fixed",
            UserContextSize = 12288,
            UserBatchSize = 1024,
            UserUBatchSize = 256,
            UserGpuLayers = 32
        };

        var profile = planner.PlanProfile(request);

        Assert.Equal("Fixed", profile.Mode);
        Assert.Equal(12288, profile.ContextSize);
        Assert.Equal(1024, profile.BatchSize);
        Assert.Equal(256, profile.UBatchSize);
        Assert.Equal(32, profile.GpuLayers);
        Assert.Null(profile.Warning);
    }

    [Fact]
    public void ChatPerformancePlanner_FixedMode_ExcessiveContext_GeneratesWarningAndClampsSafely()
    {
        var planner = new ChatPerformancePlanner();
        // 4GB 显存显卡，用户在固定模式强行设置 65536 (64K) 上下文
        var hw = CreateGpuHardware(4UL * 1024 * 1024 * 1024);

        var request = new ChatPerformancePlanningRequest
        {
            ModelName = "Qwen2.5-7B-Instruct-GGUF",
            HardwareContext = hw,
            UseGpu = true,
            PerformanceMode = "Fixed",
            UserContextSize = 65536,
            UserBatchSize = 4096,
            UserUBatchSize = 1024,
            UserGpuLayers = -1
        };

        var profile = planner.PlanProfile(request);

        Assert.Equal("Fixed", profile.Mode);
        Assert.NotNull(profile.Warning);
        Assert.Contains("显存压力告警", profile.Warning);
        // 上限软箝位至 65536
        Assert.True(profile.ContextSize <= 65536);
        Assert.True(profile.EstimatedVramBytes > 0);
    }

    [Fact]
    public void ChatPerformancePlanner_CompactModels_UnlockFullCapability()
    {
        var planner = new ChatPerformancePlanner();
        var hw = CreateGpuHardware(8UL * 1024 * 1024 * 1024);

        var compactModels = new[]
        {
            "FunctionGemma-270M-IT-GGUF",
            "LFM2.5-350M-GGUF",
            "Qwen3.5-0.8B-GGUF",
            "Bonsai-2-2B-GGUF",
            "Qwen3.5-2B-GGUF"
        };

        foreach (var model in compactModels)
        {
            var plan = planner.PlanProfile(new ChatPerformancePlanningRequest
            {
                ModelName = model,
                HardwareContext = hw,
                UseGpu = true,
                PerformanceMode = "Adaptive",
                AdaptiveStrategy = "Balanced"
            });

            Assert.True(plan.ContextSize >= 8192, $"Model {model} should support at least 8192 context");
            Assert.Equal(-1, plan.GpuLayers);
            Assert.Null(plan.Warning);
        }
    }

    [Fact]
    public void SettingsController_PreviewChatPerformance_ReturnsAccurateProfile()
    {
        var configManager = new ConfigManager();
        var mockModelManager = new Mock<IModelManager>();
        mockModelManager.Setup(m => m.GetChatModels()).Returns(new System.Collections.Generic.List<ModelInfo>());

        var controller = new SettingsController(
            configManager,
            mockModelManager.Object,
            NullLogger<SettingsController>.Instance);

        var request = new ChatPerformancePlanningRequest
        {
            ModelName = "Qwen2.5-7B-Instruct-GGUF",
            PerformanceMode = "Adaptive",
            AdaptiveStrategy = "Balanced",
            HardwareContext = CreateGpuHardware(8UL * 1024 * 1024 * 1024),
            UseGpu = true
        };

        var result = controller.PreviewChatPerformance(request);
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var profile = Assert.IsType<ChatPerformanceProfile>(okResult.Value);

        Assert.Equal(8192, profile.ContextSize);
        Assert.Equal(2048, profile.BatchSize);
        Assert.Equal(512, profile.UBatchSize);
        Assert.Equal(-1, profile.GpuLayers);
        Assert.True(profile.EstimatedVramBytes > 0);
        Assert.Equal("Adaptive", profile.Mode);
    }

    [Fact]
    public void ConfigManager_RoundTrip_PreservesPerformanceModeAndAdaptiveStrategy()
    {
        var configManager = new ConfigManager();
        var initial = new ObsidianRagConfig
        {
            Chat = new ChatModelConfig
            {
                ActiveBackend = "LocalGguf",
                LocalGguf = new LocalGgufChatProfile
                {
                    ModelName = "Qwen2.5-7B-Instruct-GGUF",
                    PerformanceMode = "Fixed",
                    AdaptiveStrategy = "HighThroughput",
                    ContextSize = 16384,
                    BatchSize = 2048,
                    UBatchSize = 512,
                    GpuLayers = 40
                }
            }
        };

        configManager.Save(initial);

        var loaded = configManager.Load();
        Assert.NotNull(loaded.Chat?.LocalGguf);
        Assert.Equal("Fixed", loaded.Chat.LocalGguf.PerformanceMode);
        Assert.Equal("HighThroughput", loaded.Chat.LocalGguf.AdaptiveStrategy);
        Assert.Equal(16384, loaded.Chat.LocalGguf.ContextSize);
        Assert.Equal(2048, loaded.Chat.LocalGguf.BatchSize);
        Assert.Equal(512, loaded.Chat.LocalGguf.UBatchSize);
        Assert.Equal(40, loaded.Chat.LocalGguf.GpuLayers);
    }

    [Fact]
    public void LocalGgufChatProfile_LegacyCompatibility_DefaultsAccurately()
    {
        // 场景 1：历史配置文件未包含 PerformanceMode 字段，但 ContextSize > 0 (如 4096)
        var jsonWithCustomContext = @"{
            ""ModelName"": ""Qwen2.5-7B"",
            ""ContextSize"": 4096,
            ""BatchSize"": 1024
        }";
        var profile1 = JsonSerializer.Deserialize<LocalGgufChatProfile>(jsonWithCustomContext, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        // 检查向下兼容性
        Assert.Equal("Fixed", profile1.PerformanceMode);
        Assert.Equal("Balanced", profile1.AdaptiveStrategy);
        Assert.Equal(4096, profile1.ContextSize);

        // 场景 2：历史配置文件未包含 PerformanceMode 字段，且 ContextSize == 0 (未配置)
        var jsonWithZeroContext = @"{
            ""ModelName"": ""Qwen2.5-7B"",
            ""ContextSize"": 0
        }";
        var profile2 = JsonSerializer.Deserialize<LocalGgufChatProfile>(jsonWithZeroContext, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Adaptive", profile2.PerformanceMode);
        Assert.Equal("Balanced", profile2.AdaptiveStrategy);
    }

    [Fact]
    public void ChatModel_StartupDefaultNotLoaded_RuntimeStatusReflectsUnloaded()
    {
        // 门禁：Chat 模型与嵌入、重排模型一致，启动时默认不加载权重，处于未加载状态
        var client = new LocalLlamaChatClient(
            modelPath: "fake_path.gguf",
            modelName: "Qwen2.5-7B-Instruct-GGUF",
            gpuLayers: -1,
            contextSize: 8192,
            batchSize: 2048,
            uBatchSize: 512,
            logger: NullLogger.Instance);

        Assert.False(client.IsInitialized);
        Assert.False(client.IsModelLoaded);

        // 模拟 ModelsController.GetRuntimeStatus
        var configManager = new ConfigManager();
        var modelManagerMock = new Mock<IModelManager>();
        modelManagerMock.Setup(m => m.GetCurrentChatModel()).Returns(new ModelInfo
        {
            Name = "Qwen2.5-7B-Instruct-GGUF",
            DisplayName = "通义千问 2.5 7B",
            LocalPath = "fake_path.gguf",
            Dimension = 4096,
            MaxSequenceLength = 8192,
            InstalledVariants = new List<ModelVariantInfo>
            {
                new ModelVariantInfo { FileName = "qwen2.5-7b.gguf", Format = "gguf", IsActive = true, Quantization = "Q4_K_M" }
            }
        });

        var embeddingServiceMock = new Mock<IEmbeddingService>();
        var vectorStoreMock = new Mock<IVectorStore>();

        var controller = new ModelsController(
            configManager,
            modelManagerMock.Object,
            embeddingServiceMock.Object,
            vectorStoreMock.Object,
            NullLogger<ModelsController>.Instance,
            rerankService: null,
            telemetryService: null,
            chatService: null);

        var statusResult = controller.GetRuntimeStatus();
        var okResult = Assert.IsType<OkObjectResult>(statusResult.Result);
        var statusResponse = Assert.IsType<ModelsRuntimeStatusResponse>(okResult.Value);

        Assert.NotNull(statusResponse.Chat);
        Assert.Equal(0, statusResponse.Chat.State);
        Assert.Equal("Unloaded", statusResponse.Chat.StateName);
        Assert.False(statusResponse.Chat.IsReady);
        Assert.Contains("未加载", statusResponse.Chat.StateDescription);
    }

    [Fact]
    public void ChatModel_Unload_ReleasesResources_And_ResetsState()
    {
        var client = new LocalLlamaChatClient(
            modelPath: "fake_path.gguf",
            modelName: "Qwen2.5-7B-Instruct-GGUF",
            gpuLayers: -1,
            contextSize: 8192,
            batchSize: 2048,
            uBatchSize: 512,
            logger: NullLogger.Instance);

        // 调用显式卸载，应能安全幂等执行并保持未加载状态
        client.Unload();
        Assert.False(client.IsInitialized);
        Assert.False(client.IsModelLoaded);
    }

    [Fact]
    public void Settings_Frontend_TwoTierTabs_And_UnifiedNaming_Integrity()
    {
        // 门禁：验证设置页面 Settings.js 的二级分类重构与名称统一
        var settingsJsPath = Path.Combine(AppContext.BaseDirectory, "../../../../../Business/ReferenceRAG.Business/wwwroot/app/views/Settings.js");
        if (!File.Exists(settingsJsPath))
        {
            settingsJsPath = Path.Combine(Directory.GetCurrentDirectory(), "Business/ReferenceRAG.Business/wwwroot/app/views/Settings.js");
        }
        if (!File.Exists(settingsJsPath))
        {
            settingsJsPath = @"E:\WorkProject\CSharp\PrivateProject\Web\ReferenceRAG\Business\ReferenceRAG.Business\wwwroot\app\views\Settings.js";
        }

        Assert.True(File.Exists(settingsJsPath), $"未找到 Settings.js 文件: {settingsJsPath}");
        var jsContent = File.ReadAllText(settingsJsPath);

        // 1. 验证二级分类状态与分组定义
        Assert.Contains("activeGroup", jsContent);
        Assert.Contains("activeTab", jsContent);
        Assert.Contains("groupTabsMap", jsContent);
        Assert.Contains("models", jsContent);
        Assert.Contains("retrieval", jsContent);
        Assert.Contains("system", jsContent);

        // 2. 验证一级分类三组胶囊导航
        Assert.Contains("模型与推理", jsContent);
        Assert.Contains("检索与分段", jsContent);
        Assert.Contains("系统与存储", jsContent);

        // 3. 验证聊天模型与嵌入重排名称统一
        Assert.Contains("推理模式", jsContent);
        Assert.Contains("本地 GGUF", jsContent);
        Assert.Contains("OpenAI 兼容 API", jsContent);
        Assert.DoesNotContain("后端方案", jsContent);
        Assert.DoesNotContain("本地 GGUF 大模型 (Local GGUF)", jsContent);
        Assert.DoesNotContain("OpenAI 兼容 API (Online/API)", jsContent);
    }

    [Fact]
    public void ChatPerformancePlanner_AmdApuIntegratedGpu_OnLinux_ShouldEnableVulkanAndFullOffload()
    {
        // 门禁：Linux 环境 AMD Radeon APU/核显（零独立显存），自适应规划应自动分配虚拟共享显存并全量卸载至 Vulkan
        var mockDetector = new Mock<IHardwareDetector>();
        var amdApu = new GpuDeviceInfo
        {
            Name = "AMD Radeon GPU / APU",
            Vendor = GpuVendor.Amd,
            TotalVramBytes = 0UL, // Linux APU 共享系统内存，无独立物理显存
            IsPrimary = true
        };
        var context = new HardwareContext
        {
            GpuDevices = new List<GpuDeviceInfo> { amdApu },
            PrimaryGpu = amdApu,
            HasNvidiaGpu = false,
            HasDirectX12 = false, // Linux 无 DirectX12
            HasPortableCuda = false,
            IsCuda12Available = false
        };

        var planner = new ChatPerformancePlanner(mockDetector.Object);
        var profile = planner.PlanProfile(new ChatPerformancePlanningRequest
        {
            ModelName = "Qwen3.5-0.8B-Instruct-GGUF",
            HardwareContext = context,
            UseGpu = true,
            PerformanceMode = "Adaptive",
            AdaptiveStrategy = "Balanced"
        });

        Assert.True(profile.IsGpuAccelerated, "AMD APU 应判定为支持 GPU 加速");
        Assert.Equal(-1, profile.GpuLayers);
        Assert.Contains("集成核显 Vulkan 加速", profile.Rationale);
        Assert.Null(profile.Warning);
    }

    [Fact]
    public void ChatPerformancePlanner_IntelIntegratedGpu_OnLinux_ShouldEnableVulkanAndFullOffload()
    {
        // 门禁：Linux 环境 Intel 核显（零独立显存），自适应规划应自动启用 Vulkan 全量加速
        var mockDetector = new Mock<IHardwareDetector>();
        var intelGpu = new GpuDeviceInfo
        {
            Name = "Intel(R) UHD Graphics 770",
            Vendor = GpuVendor.Intel,
            TotalVramBytes = 0UL,
            IsPrimary = true
        };
        var context = new HardwareContext
        {
            GpuDevices = new List<GpuDeviceInfo> { intelGpu },
            PrimaryGpu = intelGpu,
            HasNvidiaGpu = false,
            HasDirectX12 = false,
            HasPortableCuda = false,
            IsCuda12Available = false
        };

        var planner = new ChatPerformancePlanner(mockDetector.Object);
        var profile = planner.PlanProfile(new ChatPerformancePlanningRequest
        {
            ModelName = "Qwen2.5-1.5B-Instruct-GGUF",
            HardwareContext = context,
            UseGpu = true,
            PerformanceMode = "Adaptive",
            AdaptiveStrategy = "Balanced"
        });

        Assert.True(profile.IsGpuAccelerated);
        Assert.Equal(-1, profile.GpuLayers);
        Assert.Contains("集成核显 Vulkan 加速", profile.Rationale);
    }

    [Fact]
    public void ChatPerformancePlanner_FixedMode_AmdIntegratedGpu_RespectsManualGpuLayers()
    {
        // 门禁：固定参数模式下，AMD 核显应支持用户指定的 GpuLayers，而不是被强退为 0
        var mockDetector = new Mock<IHardwareDetector>();
        var amdApu = new GpuDeviceInfo
        {
            Name = "AMD Radeon GPU / APU",
            Vendor = GpuVendor.Amd,
            TotalVramBytes = 0UL,
            IsPrimary = true
        };
        var context = new HardwareContext
        {
            GpuDevices = new List<GpuDeviceInfo> { amdApu },
            PrimaryGpu = amdApu,
            HasNvidiaGpu = false,
            HasDirectX12 = false
        };

        var planner = new ChatPerformancePlanner(mockDetector.Object);
        var profile = planner.PlanProfile(new ChatPerformancePlanningRequest
        {
            ModelName = "Qwen3.5-0.8B-Instruct-GGUF",
            HardwareContext = context,
            UseGpu = true,
            PerformanceMode = "Fixed",
            UserGpuLayers = -1,
            UserContextSize = 4096
        });

        Assert.True(profile.IsGpuAccelerated);
        Assert.Equal(-1, profile.GpuLayers);
        Assert.Equal(4096, profile.ContextSize);
    }

    [Fact]
    public void ModelsController_GetRuntimeStatus_OnAmdIntegratedGpu_ReportsAmdVulkanGpu()
    {
        // 门禁：当聊天模型启用 GPU 时，AMD 核显环境下端点应准确回显 "AMD Vulkan GPU"
        var configManager = new ConfigManager();
        var cfg = configManager.Load();
        cfg.Chat = new ChatModelConfig
        {
            ActiveBackend = "LocalGguf",
            LocalGguf = new LocalGgufChatProfile
            {
                ModelName = "Qwen3.5-0.8B-Instruct-GGUF",
                GpuLayers = -1
            }
        };
        configManager.Save(cfg);

        var modelManagerMock = new Mock<IModelManager>();
        modelManagerMock.Setup(m => m.GetCurrentChatModel()).Returns(new ModelInfo
        {
            Name = "Qwen3.5-0.8B-Instruct-GGUF",
            DisplayName = "Qwen3.5 0.8B Instruct",
            ModelType = "chat",
            InstalledVariants = new List<ModelVariantInfo>
            {
                new ModelVariantInfo { FileName = "Qwen3.5-0.8B.Q4_K_M.gguf", Format = "gguf", IsActive = true, Quantization = "Q4_K_M" }
            }
        });

        var amdApu = new GpuDeviceInfo
        {
            Name = "AMD Radeon GPU / APU",
            Vendor = GpuVendor.Amd,
            TotalVramBytes = 0UL,
            IsPrimary = true
        };
        var mockDetector = new Mock<IHardwareDetector>();
        mockDetector.Setup(d => d.GetContext()).Returns(new HardwareContext
        {
            PrimaryGpu = amdApu,
            GpuDevices = new List<GpuDeviceInfo> { amdApu },
            HasNvidiaGpu = false,
            HasDirectX12 = false
        });

        var controller = new ModelsController(
            configManager,
            modelManagerMock.Object,
            new Mock<IEmbeddingService>().Object,
            new Mock<IVectorStore>().Object,
            NullLogger<ModelsController>.Instance,
            rerankService: null,
            telemetryService: null,
            chatService: null,
            hardwareDetector: mockDetector.Object);

        var statusResult = controller.GetRuntimeStatus();
        var okResult = Assert.IsType<OkObjectResult>(statusResult.Result);
        var statusResponse = Assert.IsType<ModelsRuntimeStatusResponse>(okResult.Value);

        Assert.NotNull(statusResponse.Chat);
        Assert.True(statusResponse.Chat.IsGpuMode);
        Assert.Equal("AMD Vulkan GPU (AMD Radeon GPU / APU)", statusResponse.Chat.ExecutionDevice);
        Assert.Equal("全量卸载到 GPU", statusResponse.Chat.OffloadDescription);
    }
}
