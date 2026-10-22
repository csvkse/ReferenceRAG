using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 系统资源监控指标契约与非阻塞采样质量门禁测试
/// 防范：
/// 1. 前后端契约割裂导致前端显示 0 B / 0 B 与 0分钟
/// 2. 阻塞式 Task.Delay(500) 导致的 API 响应延迟回归
/// </summary>
public class SystemMetricsContractGateTests
{
    private static (SystemController controller, MetricsCollector collector) CreateController()
    {
        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(x => x.StreamAllFilesAsync(default))
            .ReturnsAsync(EmptyAsyncEnumerable<FileRecord>());

        var collector = new MetricsCollector(mockVectorStore.Object);
        var alertService = new AlertService(collector);
        var mockLifetime = new Mock<IHostApplicationLifetime>();

        var controller = new SystemController(
            collector,
            alertService,
            mockVectorStore.Object,
            NullLogger<SystemController>.Instance,
            mockLifetime.Object);

        return (controller, collector);
    }

    private static async IAsyncEnumerable<T> EmptyAsyncEnumerable<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    [Fact]
    public async Task SystemController_GetSystemMetrics_ReturnsAllRequiredFields()
    {
        var (controller, collector) = CreateController();

        var result = await controller.GetSystemMetrics();
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var metrics = Assert.IsType<SystemMetrics>(okResult.Value);

        // 验证内存指标
        Assert.True(metrics.MemoryUsedBytes > 0, "MemoryUsedBytes 应大于 0");
        Assert.True(metrics.MemoryTotalBytes > 0, "MemoryTotalBytes 应大于 0");
        Assert.True(metrics.MemoryTotalBytes >= metrics.MemoryUsedBytes, "MemoryTotalBytes 应大于等于 MemoryUsedBytes");
        Assert.True(metrics.MemoryUsageMB > 0, "MemoryUsageMB 兼容字段应大于 0");

        // 验证磁盘指标
        Assert.True(metrics.DiskTotalBytes > 0, "DiskTotalBytes 应大于 0");
        Assert.True(metrics.DiskUsedBytes >= 0, "DiskUsedBytes 应大于等于 0");
        Assert.True(metrics.DiskFreeBytes >= 0, "DiskFreeBytes 应大于等于 0");
        Assert.True(metrics.DiskTotalBytes >= metrics.DiskUsedBytes, "DiskTotalBytes 应大于等于 DiskUsedBytes");

        // 验证 CPU 与时间指标
        Assert.InRange(metrics.CpuUsagePercent, 0.0, 100.0);
        Assert.True(metrics.UptimeSeconds >= 0, "UptimeSeconds 应大于等于 0");
        Assert.True(metrics.Uptime.TotalSeconds >= 0, "Uptime 兼容字段应大于等于 0");
    }

    [Fact]
    public async Task SystemMetrics_JsonSerialization_MatchesFrontendContract()
    {
        var (controller, collector) = CreateController();

        var metrics = await collector.CollectSystemMetricsAsync();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(metrics, options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 严格断言前端 System.js 绑定的 6 个核心属性必须在 JSON 中存在且为合规数值
        Assert.True(root.TryGetProperty("cpuUsagePercent", out var cpuProp), "缺少 cpuUsagePercent 属性");
        Assert.True(cpuProp.GetDouble() >= 0.0);

        Assert.True(root.TryGetProperty("memoryUsedBytes", out var memUsedProp), "缺少 memoryUsedBytes 属性");
        Assert.True(memUsedProp.GetInt64() > 0);

        Assert.True(root.TryGetProperty("memoryTotalBytes", out var memTotalProp), "缺少 memoryTotalBytes 属性");
        Assert.True(memTotalProp.GetInt64() > 0);

        Assert.True(root.TryGetProperty("diskUsedBytes", out var diskUsedProp), "缺少 diskUsedBytes 属性");
        Assert.True(diskUsedProp.GetInt64() >= 0);

        Assert.True(root.TryGetProperty("diskTotalBytes", out var diskTotalProp), "缺少 diskTotalBytes 属性");
        Assert.True(diskTotalProp.GetInt64() > 0);

        Assert.True(root.TryGetProperty("uptimeSeconds", out var uptimeProp), "缺少 uptimeSeconds 属性");
        Assert.True(uptimeProp.GetInt64() >= 0);
    }

    [Fact]
    public async Task SystemMetrics_ZeroBlockingDelayQualityGate()
    {
        var (controller, _) = CreateController();

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
        {
            var res = await controller.GetSystemMetrics();
            Assert.NotNull(res.Result);
        }
        sw.Stop();

        // 质量门禁：5次连续请求耗时必须小于 200ms（旧版包含 Task.Delay(500)，5次请求耗时将达 2500ms）
        Assert.True(sw.ElapsedMilliseconds < 200, $"连续系统指标采样必须在 200ms 内完成，实际耗时: {sw.ElapsedMilliseconds}ms");
    }
}
