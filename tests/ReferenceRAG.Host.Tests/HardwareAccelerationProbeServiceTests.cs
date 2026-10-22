using System;
using System.IO;
using System.Text.Json;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.HardwareAcceleration.Probing;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class HardwareAccelerationProbeServiceTests
{
    [Fact]
    public void ProbeWorker_ExecuteProbe_ReturnsNonNullResult_AndDefaultBackends()
    {
        var result = HardwareAccelerationProbeWorker.ExecuteProbe();

        Assert.NotNull(result);
        Assert.NotNull(result.AvailableBackends);
        Assert.NotEmpty(result.AvailableBackends);
        Assert.Contains(result.AvailableBackends, b => b.Backend == "Cpu" && b.Supported);
        Assert.Contains(result.AvailableBackends, b => b.Backend == "Auto" && b.Supported);
        Assert.False(string.IsNullOrWhiteSpace(result.ActiveBackend));
    }

    [Fact]
    public void ProbeWorker_RunCli_OutputsDelimitedJson_AndReturnsZero()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var exitCode = HardwareAccelerationProbeWorker.RunCli(Array.Empty<string>());
            Assert.Equal(0, exitCode);

            var output = sw.ToString();
            Assert.Contains(HardwareAccelerationProbeWorker.JsonStartDelimiter, output);
            Assert.Contains(HardwareAccelerationProbeWorker.JsonEndDelimiter, output);

            var startIdx = output.IndexOf(HardwareAccelerationProbeWorker.JsonStartDelimiter, StringComparison.Ordinal) + HardwareAccelerationProbeWorker.JsonStartDelimiter.Length;
            var endIdx = output.IndexOf(HardwareAccelerationProbeWorker.JsonEndDelimiter, StringComparison.Ordinal);
            var json = output.Substring(startIdx, endIdx - startIdx).Trim();

            var parsed = JsonSerializer.Deserialize<HardwareAccelerationStatusResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            Assert.NotNull(parsed);
            Assert.Contains(parsed.AvailableBackends, b => b.Backend == "Cpu");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ProbeService_GetStatus_ReturnsValidCapabilities_AndCachesResult()
    {
        var service = new HardwareAccelerationProbeService();

        var first = service.GetStatus();
        Assert.NotNull(first);
        Assert.NotNull(first.ActiveBackend);

        // 第二次调用应命中高速缓存（同一对象引用）
        var second = service.GetStatus();
        Assert.Same(first, second);

        // 验证 CUDA 视图模型
        var cuda = service.GetCudaAvailability();
        Assert.NotNull(cuda);
        Assert.False(string.IsNullOrWhiteSpace(cuda.Message));
    }
}
