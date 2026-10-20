using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.Chat.Services;
using ReferenceRAG.Core.Features.HardwareAcceleration;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 质量门禁：本地大模型自适应性能规划与 KV Cache 生命周期门禁
/// 确保系统既具备自适应释放小模型（1.5B/3B）超长上下文（32K）与高显存硬件潜力的能力，
/// 又具备在消费级 8GB 显卡与 7B/14B 模型上自动平衡箝位（8K/4K）防止 NoKvSlot 崩溃的刚性防线。
/// </summary>
public sealed class LocalChatContextAndKvCacheQualityGateTests
{
    private static HardwareContext CreateTestHardware(ulong totalVramBytes, bool hasNvidia = true)
    {
        return new HardwareContext
        {
            HasNvidiaGpu = hasNvidia,
            HasDirectX12 = true,
            PrimaryGpu = new GpuDeviceInfo
            {
                Name = "NVIDIA GeForce RTX 4060",
                Vendor = GpuVendor.Nvidia,
                TotalVramBytes = totalVramBytes,
                IsPrimary = true
            }
        };
    }

    [Fact]
    public void ChatPerformancePlanner_SmallModelOn8GbGpu_Unlocks32KContext_WithoutPerformanceWaste()
    {
        // 门禁规则 1：1.5B 小模型在 8GB 消费级显卡上，剩余显存充裕 (> 5GB)，
        // 系统必须自适应释放 32768 (32K) 超长上下文窗口，绝不能死板限制在 8K 导致能力浪费！
        var planner = new ChatPerformancePlanner();
        var hw = CreateTestHardware(8UL * 1024 * 1024 * 1024); // 8GB 显存

        var plan = planner.PlanProfile(
            modelPath: null,
            modelName: "Qwen2.5-1.5B-Instruct-GGUF",
            hardwareContext: hw,
            useGpu: true);

        Assert.Equal(32768, plan.ContextSize);
        Assert.Equal(2048, plan.BatchSize);
        Assert.Equal(512, plan.UBatchSize);
        Assert.Equal(-1, plan.GpuLayers);
        Assert.Contains("32K", plan.Rationale);
    }

    [Fact]
    public void ChatPerformancePlanner_Standard7BOn8GbGpu_AdaptsTo8KBalancedContext()
    {
        // 门禁规则 2：7B 主流模型在 8GB 显卡上，权重占用 ~4.7GB，
        // 系统必须自适应配置为 8192 (8K) 黄金平衡上下文与 2048 批大小，杜绝 NoKvSlot。
        var planner = new ChatPerformancePlanner();
        var hw = CreateTestHardware(8UL * 1024 * 1024 * 1024); // 8GB 显存

        var plan = planner.PlanProfile(
            modelPath: null,
            modelName: "Qwen2.5-7B-Instruct-GGUF",
            hardwareContext: hw,
            useGpu: true);

        Assert.Equal(8192, plan.ContextSize);
        Assert.Equal(2048, plan.BatchSize);
        Assert.Equal(512, plan.UBatchSize);
        Assert.Equal(-1, plan.GpuLayers);
        Assert.Contains("8K", plan.Rationale);
    }

    [Fact]
    public void ChatPerformancePlanner_Standard7BOn24GbGpu_Unlocks16KExtendedContext()
    {
        // 门禁规则 3：7B 模型在 24GB 大显存硬件（如 RTX 4090/A100）上，
        // 系统自适应扩容至 16384 (16K) 上下文，充分利用硬件优势。
        var planner = new ChatPerformancePlanner();
        var hw = CreateTestHardware(24UL * 1024 * 1024 * 1024); // 24GB 显存

        var plan = planner.PlanProfile(
            modelPath: null,
            modelName: "Qwen2.5-7B-Instruct-GGUF",
            hardwareContext: hw,
            useGpu: true);

        Assert.Equal(16384, plan.ContextSize);
        Assert.Equal(2048, plan.BatchSize);
        Assert.Equal(-1, plan.GpuLayers);
    }

    [Fact]
    public void ChatPerformancePlanner_CpuOnlyMode_AdaptsToLowerLatencyBatchAndContext()
    {
        // 门禁规则 4：纯 CPU 运行模式下，自适应降低 BatchSize (512) 与上下文，保证首字响应速度 (TTFT)。
        var planner = new ChatPerformancePlanner();
        var hw = new HardwareContext { HasNvidiaGpu = false, HasDirectX12 = false, PrimaryGpu = null };

        var plan = planner.PlanProfile(
            modelPath: null,
            modelName: "Qwen2.5-7B-Instruct-GGUF",
            hardwareContext: hw,
            useGpu: false);

        Assert.Equal(0, plan.GpuLayers);
        Assert.True(plan.ContextSize <= 4096);
        Assert.Equal(512, plan.BatchSize);
        Assert.Equal(256, plan.UBatchSize);
        Assert.Contains("CPU 运行模式", plan.Rationale);
    }

    [Fact]
    public void LocalLlamaChatClient_ClearKvCache_CanBeInvokedSafely()
    {
        // 门禁规则 5：LocalLlamaChatClient 必须具备 ClearKvCache 原生物理槽位重置机制
        var method = typeof(LocalLlamaChatClient).GetMethod("ClearKvCache", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var client = new LocalLlamaChatClient(
            modelPath: "test.gguf",
            modelName: "test-model",
            gpuLayers: 0,
            contextSize: 4096,
            logger: NullLogger<LocalLlamaChatClient>.Instance);

        var ex = Record.Exception(() => method.Invoke(client, null));
        Assert.Null(ex);
    }

    [Fact]
    public void ChatToolCallingHelper_PromptRules_EnforcesImmediateToolCall_WithoutVerbosity()
    {
        // 门禁规则 6：注入小模型的工具提示词中必须包含强硬的“立即输出 <tool_call>，禁止对用户解释工具”约束规则，
        // 防止 Qwen2.5 1.5B/3B 等小模型啰嗦列举工具耗尽上下文与 KV 槽位。
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(() => "result", "search_tool", "Search documentation")
        };

        var prompt = ChatToolCallingHelper.FormatToolsPrompt(tools);

        Assert.Contains("<tools>", prompt);
        Assert.Contains("</tools>", prompt);
        Assert.Contains("<tool_call>", prompt);
        Assert.Contains("IMMEDIATELY using <tool_call> tags", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not explain the tools to the user", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
