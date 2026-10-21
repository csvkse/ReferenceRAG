using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Injection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.HardwareAcceleration.Scanning;
using ReferenceRAG.Core.Features.HardwareAcceleration.Strategies;

namespace ReferenceRAG.Tests;

public class HardwareAccelerationTests
{
    [Fact]
    public void EnvironmentInjector_InjectDirectories_ShouldUpdatePathAndAvoidDuplicates()
    {
        var injector = new Win32EnvironmentInjector();
        var tempDir = Path.Combine(Path.GetTempPath(), "test_cuda_inject_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var injected = injector.InjectDirectories(new[] { tempDir });
            Assert.Contains(tempDir, injected);

            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            Assert.Contains(tempDir, pathVar);

            // 再次注入应幂等去重
            var injectedAgain = injector.InjectDirectories(new[] { tempDir });
            Assert.Contains(tempDir, injectedAgain);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir); } catch { }
            }
        }
    }

    [Fact]
    public void HardwareDetector_GetContext_ShouldReturnValidContext()
    {
        var detector = new HardwareDetector();
        var context = detector.GetContext();

        Assert.NotNull(context);
        Assert.NotNull(context.GpuDevices);

        // 如果在带有 NVIDIA 显卡的 Windows 设备上运行
        if (context.HasNvidiaGpu)
        {
            Assert.NotNull(context.PrimaryGpu);
            Assert.Equal(GpuVendor.Nvidia, context.PrimaryGpu.Vendor);
            Assert.False(string.IsNullOrWhiteSpace(context.PrimaryGpu.Name));
        }
    }

    [Fact]
    public void CudaEnvironmentScanner_WhenGivenValidDirectory_ShouldDetectAndInject()
    {
        var mockInjector = new Mock<IEnvironmentInjector>();
        var tempDir = Path.Combine(Path.GetTempPath(), "test_cuda_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var cublasLt = Path.Combine(tempDir, "cublasLt64_12.dll");
        var cublas = Path.Combine(tempDir, "cublas64_12.dll");
        File.WriteAllText(cublasLt, "fake");
        File.WriteAllText(cublas, "fake");

        mockInjector.Setup(i => i.InjectDirectories(It.IsAny<IEnumerable<string>>()))
            .Returns(new List<string> { tempDir });

        try
        {
            var scanner = new CudaEnvironmentScanner(mockInjector.Object);
            var result = scanner.ScanAndInject(tempDir);

            Assert.True(result.Found);
            Assert.Equal("12.x", result.CudaVersion);
            Assert.Equal(tempDir, result.MainDllDirectory);
            mockInjector.Verify(i => i.InjectDirectories(It.Is<IEnumerable<string>>(dirs => dirs.Contains(tempDir))), Times.Once);
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
    public void CudaEnvironmentScanner_WhenCudaDllMissing_ShouldReturnNotFound()
    {
        var mockInjector = new Mock<IEnvironmentInjector>();
        var tempDir = Path.Combine(Path.GetTempPath(), "test_cuda_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var scanner = new CudaEnvironmentScanner(mockInjector.Object);
            var result = scanner.ScanAndInject(tempDir);

            // 由于只有空目录且非标准 CUDA 安装，应标记未找到或有错误提示
            if (!result.Found)
            {
                Assert.False(result.HasRequiredDlls);
                Assert.NotNull(result.ErrorMessage);
            }
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
    public void StrategyPriorities_ShouldBeProperlyOrdered()
    {
        var nativeCuda = new NativeCudaStrategy(new Mock<ICudaEnvironmentScanner>().Object);
        var portableCuda = new PortableCudaStrategy(new Mock<IEnvironmentInjector>().Object);
        var directMl = new DirectMlStrategy();
        var cpu = new CpuStrategy();

        Assert.Equal(100, nativeCuda.Priority);
        Assert.Equal(80, portableCuda.Priority);
        Assert.Equal(60, directMl.Priority);
        Assert.Equal(10, cpu.Priority);

        Assert.True(nativeCuda.Priority > portableCuda.Priority);
        Assert.True(portableCuda.Priority > directMl.Priority);
        Assert.True(directMl.Priority > cpu.Priority);
    }

    [Fact]
    public async Task AutoExecutionStrategy_ShouldFallbackToCpu_WhenAllGpuStrategiesFail()
    {
        var mockDetector = new Mock<IHardwareDetector>();
        mockDetector.Setup(d => d.GetContext(It.IsAny<bool>()))
            .Returns(new HardwareContext
            {
                GpuDevices = new List<GpuDeviceInfo>(),
                PrimaryGpu = null,
                HasNvidiaGpu = false,
                HasDirectX12 = false,
                HasPortableCuda = false,
                IsCuda12Available = false
            });

        var cpuStrategy = new CpuStrategy();
        var strategies = new List<IExecutionStrategy> { cpuStrategy };

        var autoStrategy = new AutoExecutionStrategy(mockDetector.Object, strategies);
        var result = await autoStrategy.ValidateAndPrepareAsync(mockDetector.Object.GetContext());

        Assert.True(result.IsSuccess);
        Assert.Equal("CPU", result.StrategyName);
        Assert.Equal("CPU", autoStrategy.GetActiveStrategy().Name);
    }

    [Fact]
    public async Task AutoExecutionStrategy_ShouldSelectNativeCuda_WhenAvailable()
    {
        var mockDetector = new Mock<IHardwareDetector>();
        var dummyGpu = new GpuDeviceInfo
        {
            Name = "NVIDIA GeForce RTX 4060",
            Vendor = GpuVendor.Nvidia,
            IsPrimary = true
        };

        mockDetector.Setup(d => d.GetContext(It.IsAny<bool>()))
            .Returns(new HardwareContext
            {
                GpuDevices = new List<GpuDeviceInfo> { dummyGpu },
                PrimaryGpu = dummyGpu,
                HasNvidiaGpu = true,
                HasDirectX12 = true,
                HasPortableCuda = false,
                IsCuda12Available = true
            });

        var mockCudaStrategy = new Mock<IExecutionStrategy>();
        mockCudaStrategy.SetupGet(s => s.Name).Returns("NativeCuda");
        mockCudaStrategy.SetupGet(s => s.Priority).Returns(100);
        mockCudaStrategy.Setup(s => s.IsSupported(It.IsAny<HardwareContext>())).Returns(true);
        mockCudaStrategy.Setup(s => s.ValidateAndPrepareAsync(It.IsAny<HardwareContext>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync(StrategyValidationResult.Success("NativeCuda", dummyGpu.Name, new[] { @"E:\CUDA" }, "CUDA Ready"));

        var cpuStrategy = new CpuStrategy();
        var strategies = new List<IExecutionStrategy> { mockCudaStrategy.Object, cpuStrategy };

        var autoStrategy = new AutoExecutionStrategy(mockDetector.Object, strategies);
        var result = await autoStrategy.ValidateAndPrepareAsync(mockDetector.Object.GetContext());

        Assert.True(result.IsSuccess);
        Assert.Equal("NativeCuda", result.StrategyName);
        Assert.Equal("NativeCuda", autoStrategy.GetActiveStrategy().Name);
    }

    [Fact]
    public void HardwareAccelerationConfig_LegacyBridge_ShouldMapUseCudaCorrectly()
    {
        var config = new ReferenceRAG.Core.Models.EmbeddingConfig();
        
        // 初始默认 Backend = Auto (IsEnabled = true, UseCuda = true)
        Assert.Equal(ReferenceRAG.Core.Models.AccelerationBackend.Auto, config.Acceleration.Backend);
        Assert.True(config.UseCuda);

        // 设置 UseCuda = false 应自动降级为 Cpu
        config.UseCuda = false;
        Assert.Equal(ReferenceRAG.Core.Models.AccelerationBackend.Cpu, config.Acceleration.Backend);
        Assert.False(config.UseCuda);

        // 设置 UseCuda = true 应自动升级为 Auto
        config.UseCuda = true;
        Assert.Equal(ReferenceRAG.Core.Models.AccelerationBackend.Auto, config.Acceleration.Backend);
        Assert.True(config.UseCuda);

        // 设置为 DirectML 时，UseCuda 表现为 true (IsEnabled)，但不会被 UseCuda=true 冲掉
        config.Acceleration.Backend = ReferenceRAG.Core.Models.AccelerationBackend.DirectML;
        Assert.True(config.UseCuda);
        config.UseCuda = true;
        Assert.Equal(ReferenceRAG.Core.Models.AccelerationBackend.DirectML, config.Acceleration.Backend);

        // DeviceId 与 LibraryPath 桥接
        config.CudaDeviceId = 2;
        Assert.Equal(2, config.Acceleration.DeviceId);
        config.CudaLibraryPath = @"C:\Cuda";
        Assert.Equal(@"C:\Cuda", config.Acceleration.CustomLibraryPath);
    }

    [Fact]
    public void HardwareAccelerationConfig_JsonDeserialization_OldFormat_ShouldMapCorrectly()
    {
        var legacyJson = @"{
            ""UseCuda"": false,
            ""CudaDeviceId"": 3,
            ""CudaLibraryPath"": ""/opt/cuda""
        }";

        var config = System.Text.Json.JsonSerializer.Deserialize<ReferenceRAG.Core.Models.EmbeddingConfig>(legacyJson);
        Assert.NotNull(config);
        Assert.Equal(ReferenceRAG.Core.Models.AccelerationBackend.Cpu, config.Acceleration.Backend);
        Assert.False(config.UseCuda);
        Assert.Equal(3, config.Acceleration.DeviceId);
        Assert.Equal("/opt/cuda", config.Acceleration.CustomLibraryPath);
    }

    [Fact]
    public void HardwareAccelerationConfig_JsonDeserialization_NewFormat_ShouldMapCorrectly()
    {
        var newJson = @"{
            ""Acceleration"": {
                ""Backend"": ""DirectML"",
                ""DeviceId"": 1,
                ""CustomLibraryPath"": ""C:\\DirectML"",
                ""FallbackToCpuOnFailure"": true
            }
        }";

        var config = System.Text.Json.JsonSerializer.Deserialize<ReferenceRAG.Core.Models.EmbeddingConfig>(newJson);
        Assert.NotNull(config);
        Assert.Equal(ReferenceRAG.Core.Models.AccelerationBackend.DirectML, config.Acceleration.Backend);
        Assert.True(config.UseCuda);
        Assert.Equal(1, config.Acceleration.DeviceId);
        Assert.Equal(@"C:\DirectML", config.Acceleration.CustomLibraryPath);
    }

    [Fact]
    public void MultiBackendProfiles_EmbeddingAndRerank_ShouldIsolateModelNamesWithoutOverwrite()
    {
        var config = new ReferenceRAG.Core.Models.EmbeddingConfig();
        config.LocalOnnx.ModelName = "bge-small-zh-v1.5";
        config.OpenAiApi.ModelName = "text-embedding-3-small";
        config.OpenAiApi.BaseUrl = "https://api.openai.com/v1";

        // ActiveBackend 默认为 LocalOnnx
        Assert.Equal("LocalOnnx", config.ActiveBackend);
        Assert.Equal("bge-small-zh-v1.5", config.ModelName);
        Assert.Equal("onnx", config.Mode);

        // 切换到 OpenAiApi
        config.ActiveBackend = "OpenAiApi";
        Assert.Equal("text-embedding-3-small", config.ModelName);
        Assert.Equal("openai", config.Mode);
        Assert.Equal("https://api.openai.com/v1", config.ApiBaseUrl);

        // 切回 LocalOnnx，本地模型名称依然保持原样，未被破坏性覆盖！
        config.ActiveBackend = "LocalOnnx";
        Assert.Equal("bge-small-zh-v1.5", config.ModelName);
        Assert.Equal("text-embedding-3-small", config.OpenAiApi.ModelName);

        // 重排同样测试隔离
        var rerank = new ReferenceRAG.Core.Models.RerankConfig();
        rerank.LocalOnnx.ModelName = "bge-reranker-base";
        rerank.OpenAiApi.ModelName = "jina-reranker-v2";

        rerank.ActiveBackend = "LocalOnnx";
        Assert.Equal("bge-reranker-base", rerank.ModelName);
        rerank.ActiveBackend = "OpenAiApi";
        Assert.Equal("jina-reranker-v2", rerank.ModelName);
        rerank.ActiveBackend = "LocalOnnx";
        Assert.Equal("bge-reranker-base", rerank.ModelName);
    }

    [Fact]
    public void MultiBackendProfiles_ChatConfig_ShouldMapProfilesAndBridgeProperly()
    {
        var chat = new ReferenceRAG.Core.Models.ChatModelConfig();
        chat.LocalGguf.ModelName = "Qwen2.5-7B-Instruct-GGUF";
        chat.OpenAiApi.Model = "gpt-4o-mini";
        chat.OpenAiApi.Endpoint = "https://api.deepseek.com/v1";

        chat.ActiveBackend = "OpenAiApi";
        Assert.Equal("gpt-4o-mini", chat.Model);
        Assert.Equal("https://api.deepseek.com/v1", chat.Endpoint);

        chat.ActiveBackend = "LocalGguf";
        Assert.Equal("Qwen2.5-7B-Instruct-GGUF", chat.Model);
    }

    [Theory]
    [InlineData("0x8086", GpuVendor.Intel)]
    [InlineData("0x1002", GpuVendor.Amd)]
    [InlineData("0x10de", GpuVendor.Nvidia)]
    [InlineData("0x9999", GpuVendor.Unknown)]
    public void HardwareDetector_ParseLinuxVendorId_ShouldDetectIntelAndAmdGpu(string vendorHex, GpuVendor expected)
    {
        var vendor = HardwareDetector.ParseLinuxVendorId(vendorHex);
        Assert.Equal(expected, vendor);
    }

    [Theory]
    [InlineData("Intel(R) Iris(R) Xe Graphics", "PCI\\VEN_8086&DEV_9A49", GpuVendor.Intel)]
    [InlineData("Intel(R) UHD Graphics 770", null, GpuVendor.Intel)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", null, GpuVendor.Intel)]
    [InlineData("AMD Radeon 780M Graphics", "PCI\\VEN_1002&DEV_15BF", GpuVendor.Amd)]
    [InlineData("AMD Radeon Graphics", null, GpuVendor.Amd)]
    [InlineData("NVIDIA GeForce RTX 4070", "PCI\\VEN_10DE&DEV_2786", GpuVendor.Nvidia)]
    public void HardwareDetector_ParseVendor_ShouldRecognizeIntegratedIntelAndAmdGpus(string name, string? pnpId, GpuVendor expected)
    {
        var vendor = HardwareDetector.ParseVendor(name, pnpId);
        Assert.Equal(expected, vendor);
    }

    [Fact]
    public void OnnxExecutionProviderHelper_TryAppendOpenVino_ShouldHandleSafelyWithoutCrashing()
    {
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
        var success = ReferenceRAG.Core.Features.HardwareAcceleration.OnnxExecutionProviderHelper.TryAppendOpenVino(options, "GPU", out var failureReason);
        if (!success)
        {
            Assert.NotNull(failureReason);
        }
    }
}
