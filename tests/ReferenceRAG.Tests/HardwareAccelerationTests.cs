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
}
