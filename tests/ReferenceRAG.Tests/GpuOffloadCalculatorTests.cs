using System.Text;
using Moq;
using Xunit;
using ReferenceRAG.Core.Features.HardwareAcceleration;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.ModelManagement.Helpers;

namespace ReferenceRAG.Tests;

public class GpuOffloadCalculatorTests
{
    [Fact]
    public void GgufMetadataReader_WithSyntheticGgufHeader_ShouldParseBlockCount()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "test_gguf_header_" + Guid.NewGuid().ToString("N") + ".gguf");
        try
        {
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(fs, Encoding.UTF8))
            {
                writer.Write(0x46554747U); // "GGUF" Magic
                writer.Write(3U);          // Version 3
                writer.Write(100UL);       // Tensor count
                writer.Write(2UL);         // Metadata KV count

                // Key 1: "general.architecture" -> "llama"
                var k1 = Encoding.UTF8.GetBytes("general.architecture");
                writer.Write((ulong)k1.Length);
                writer.Write(k1);
                writer.Write(8U);          // ValueType: STRING
                var v1 = Encoding.UTF8.GetBytes("llama");
                writer.Write((ulong)v1.Length);
                writer.Write(v1);

                // Key 2: "llama.block_count" -> 32
                var k2 = Encoding.UTF8.GetBytes("llama.block_count");
                writer.Write((ulong)k2.Length);
                writer.Write(k2);
                writer.Write(4U);          // ValueType: UINT32
                writer.Write(32U);
            }

            var info = GgufMetadataReader.ReadMetadata(tempFile);
            Assert.Equal("llama", info.Architecture);
            Assert.Equal(32, info.BlockCount);
            Assert.Equal(100UL, info.TensorCount);
            Assert.Equal(2UL, info.MetadataCount);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [Fact]
    public void GgufMetadataReader_NonExistentOrEmptyFile_ReturnsEmptyGracefully()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "non_existent_" + Guid.NewGuid().ToString("N") + ".gguf");
        var info = GgufMetadataReader.ReadMetadata(missingPath);

        Assert.Null(info.Architecture);
        Assert.Null(info.BlockCount);
        Assert.Equal(0UL, info.TensorCount);
    }

    [Fact]
    public void CalculateOffload_WhenUseGpuFalse_ReturnsZeroLayers()
    {
        var mockDetector = new Mock<IHardwareDetector>();
        var calc = new GpuOffloadCalculator(mockDetector.Object);

        var plan = calc.CalculateOffload("model.gguf", useGpu: false);
        Assert.Equal(0, plan.GpuLayers);
        Assert.True(plan.IsCpuOnly);
        Assert.Contains("确定性 CPU", plan.Rationale);
    }

    [Fact]
    public void CalculateOffload_WhenNoGpu_ReturnsZeroLayers()
    {
        var mockDetector = new Mock<IHardwareDetector>();
        var emptyContext = new HardwareContext
        {
            GpuDevices = new List<GpuDeviceInfo>(),
            PrimaryGpu = null,
            HasNvidiaGpu = false,
            HasDirectX12 = false,
            HasPortableCuda = false,
            IsCuda12Available = false
        };

        var calc = new GpuOffloadCalculator(mockDetector.Object);
        var plan = calc.CalculateOffload("model.gguf", context: emptyContext, useGpu: true);

        Assert.Equal(0, plan.GpuLayers);
        Assert.True(plan.IsCpuOnly);
        Assert.Contains("未检测到支持的独立显卡", plan.Rationale);
    }

    [Fact]
    public void CalculateOffload_WhenUserSpecifiesExplicitLayers_HonorsExplicitLayers()
    {
        var mockDetector = new Mock<IHardwareDetector>();
        var gpu = new GpuDeviceInfo
        {
            Name = "NVIDIA GeForce RTX 4060",
            Vendor = GpuVendor.Nvidia,
            TotalVramBytes = 8UL * 1024 * 1024 * 1024,
            IsPrimary = true
        };
        var context = new HardwareContext
        {
            GpuDevices = new List<GpuDeviceInfo> { gpu },
            PrimaryGpu = gpu,
            HasNvidiaGpu = true,
            HasDirectX12 = true,
            HasPortableCuda = false,
            IsCuda12Available = true
        };

        var calc = new GpuOffloadCalculator(mockDetector.Object);
        var plan = calc.CalculateOffload("model.gguf", context: context, useGpu: true, explicitLayers: 18);

        Assert.Equal(18, plan.GpuLayers);
        Assert.Contains("用户显式指定", plan.Rationale);
    }

    [Fact]
    public void CalculateOffload_WhenVramAmple_AssignsFullOffload()
    {
        var mockDetector = new Mock<IHardwareDetector>();
        // 模拟 8GB VRAM (RTX 4060)
        var gpu = new GpuDeviceInfo
        {
            Name = "NVIDIA GeForce RTX 4060 Laptop GPU",
            Vendor = GpuVendor.Nvidia,
            TotalVramBytes = 8UL * 1024 * 1024 * 1024,
            IsPrimary = true
        };
        var context = new HardwareContext
        {
            GpuDevices = new List<GpuDeviceInfo> { gpu },
            PrimaryGpu = gpu,
            HasNvidiaGpu = true,
            HasDirectX12 = true,
            HasPortableCuda = false,
            IsCuda12Available = true
        };

        // 创建临时 500MB 文件模拟模型
        var tempModel = Path.Combine(Path.GetTempPath(), "test_small_model_" + Guid.NewGuid().ToString("N") + ".gguf");
        using (var fs = new FileStream(tempModel, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(500L * 1024 * 1024); // 500MB
        }

        try
        {
            var calc = new GpuOffloadCalculator(mockDetector.Object);
            var plan = calc.CalculateOffload(tempModel, context: context, useGpu: true);

            // 8GB 显存充裕，应全量卸载 (GpuLayers = -1)
            Assert.Equal(-1, plan.GpuLayers);
            Assert.True(plan.IsFullOffload);
            Assert.Contains("显存充裕", plan.Rationale);
            Assert.Contains("全部层", plan.Rationale);
        }
        finally
        {
            if (File.Exists(tempModel))
            {
                try { File.Delete(tempModel); } catch { }
            }
        }
    }

    [Fact]
    public void CalculateOffload_WhenVramPartiallySufficient_CalculatesPartialLayers()
    {
        var mockDetector = new Mock<IHardwareDetector>();
        // 模拟 4GB 总显存 (可用约 3.2GB)
        var gpu = new GpuDeviceInfo
        {
            Name = "NVIDIA GeForce GTX 1650",
            Vendor = GpuVendor.Nvidia,
            TotalVramBytes = 4UL * 1024 * 1024 * 1024,
            IsPrimary = true
        };
        var context = new HardwareContext
        {
            GpuDevices = new List<GpuDeviceInfo> { gpu },
            PrimaryGpu = gpu,
            HasNvidiaGpu = true,
            HasDirectX12 = true,
            HasPortableCuda = false,
            IsCuda12Available = true
        };

        // 创建临时 5GB 文件模拟 7B/8B 量化模型
        var tempModel = Path.Combine(Path.GetTempPath(), "test_large_model_" + Guid.NewGuid().ToString("N") + ".gguf");
        using (var fs = new FileStream(tempModel, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(5L * 1024 * 1024 * 1024); // 5GB
        }

        try
        {
            var calc = new GpuOffloadCalculator(mockDetector.Object);
            var plan = calc.CalculateOffload(tempModel, context: context, useGpu: true);

            // 3.2GB 可用显存 / 5.6GB 需求 ≈ 57%
            // 32 总层数 * 57% ≈ 18 层
            Assert.True(plan.GpuLayers > 0);
            Assert.True(plan.GpuLayers < plan.TotalLayers);
            Assert.False(plan.IsFullOffload);
            Assert.False(plan.IsCpuOnly);
            Assert.Contains("部分满足", plan.Rationale);
        }
        finally
        {
            if (File.Exists(tempModel))
            {
                try { File.Delete(tempModel); } catch { }
            }
        }
    }

    [Fact]
    public void CalculateOffload_WhenVramSeverelyInsufficient_FallsBackToCpu()
    {
        var mockDetector = new Mock<IHardwareDetector>();
        // 模拟可用显存仅 1GB
        var gpu = new GpuDeviceInfo
        {
            Name = "Intel UHD Graphics",
            Vendor = GpuVendor.Intel,
            TotalVramBytes = 1000UL * 1024 * 1024, // 1000MB (扣除 768MB 后仅剩 ~232MB)
            IsPrimary = true
        };
        var context = new HardwareContext
        {
            GpuDevices = new List<GpuDeviceInfo> { gpu },
            PrimaryGpu = gpu,
            HasNvidiaGpu = false,
            HasDirectX12 = true,
            HasPortableCuda = false,
            IsCuda12Available = false
        };

        // 4GB 模型
        var tempModel = Path.Combine(Path.GetTempPath(), "test_huge_model_" + Guid.NewGuid().ToString("N") + ".gguf");
        using (var fs = new FileStream(tempModel, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(4L * 1024 * 1024 * 1024);
        }

        try
        {
            var calc = new GpuOffloadCalculator(mockDetector.Object);
            var plan = calc.CalculateOffload(tempModel, context: context, useGpu: true);

            // 显存太小，应回退纯 CPU 防止 OOM 崩溃
            Assert.Equal(0, plan.GpuLayers);
            Assert.True(plan.IsCpuOnly);
            Assert.Contains("可用显存过小", plan.Rationale);
        }
        finally
        {
            if (File.Exists(tempModel))
            {
                try { File.Delete(tempModel); } catch { }
            }
        }
    }

    [Fact]
    public void CalculateOffload_WhenIntelIntegratedGpu_WithZeroVram_AllocatesSharedMemoryAndOffloadsToVulkan()
    {
        var mockDetector = new Mock<IHardwareDetector>();
        var igpu = new GpuDeviceInfo
        {
            Name = "Intel(R) UHD Graphics 770",
            Vendor = GpuVendor.Intel,
            TotalVramBytes = 0UL, // 核显共享系统内存，无独立板载显存
            IsPrimary = true
        };
        var context = new HardwareContext
        {
            GpuDevices = new List<GpuDeviceInfo> { igpu },
            PrimaryGpu = igpu,
            HasNvidiaGpu = false,
            HasDirectX12 = false, // Linux 无 DirectX12
            HasPortableCuda = false,
            IsCuda12Available = false
        };

        // 500MB 的 bge-m3 或类似小型 GGUF 模型
        var tempModel = Path.Combine(Path.GetTempPath(), "test_igpu_model_" + Guid.NewGuid().ToString("N") + ".gguf");
        using (var fs = new FileStream(tempModel, FileMode.Create, FileAccess.Write))
        {
            fs.SetLength(500L * 1024 * 1024);
        }

        try
        {
            var calc = new GpuOffloadCalculator(mockDetector.Object);
            var plan = calc.CalculateOffload(tempModel, context: context, useGpu: true);

            // 核显应分配共享内存并支持全量卸载至核显 Vulkan
            Assert.Equal(-1, plan.GpuLayers);
            Assert.False(plan.IsCpuOnly);
            Assert.Contains("核显 Vulkan 加速", plan.Rationale);
        }
        finally
        {
            if (File.Exists(tempModel))
            {
                try { File.Delete(tempModel); } catch { }
            }
        }
    }
}
