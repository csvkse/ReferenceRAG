using Moq;
using ReferenceRAG.Core.Features.HardwareAcceleration.Detection;
using ReferenceRAG.Core.Features.HardwareAcceleration.Models;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Features.ModelManagement.StateMachine;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry.Strategies;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class ModelTelemetryTests
{
    [Fact]
    public void LlamaTelemetryStrategy_CanHandle_RecognizesGgufAndLlamaBackend()
    {
        var strategy = new LlamaTelemetryStrategy();

        Assert.True(strategy.CanHandle("llama", "bge-m3.gguf"));
        Assert.True(strategy.CanHandle(null, "C:\\models\\bge-large-zh-q4_k_m.gguf"));
        Assert.False(strategy.CanHandle("onnx", "model.onnx"));
        Assert.False(strategy.CanHandle("openai", "http://localhost:11434"));
    }

    [Fact]
    public void LlamaTelemetryStrategy_Extract_CorrectlyParsesQuantizationAndHardware()
    {
        var strategy = new LlamaTelemetryStrategy();
        var options = new EmbeddingOptions
        {
            ModelPath = "C:\\models\\bge-m3-q4_k_m.gguf",
            ModelName = "bge-m3",
            UseCuda = true,
            BatchSize = 16,
            MaxSequenceLength = 4096
        };

        var hardwareContext = new HardwareContext
        {
            HasNvidiaGpu = true,
            IsCuda12Available = true,
            PrimaryGpu = new GpuDeviceInfo
            {
                Name = "NVIDIA GeForce RTX 4060",
                TotalVramBytes = 8589934592, // 8GB
                DriverVersion = "560.94"
            }
        };

        var (hardware, config) = strategy.Extract(null, options, hardwareContext, null);

        Assert.True(hardware.IsGpuMode);
        Assert.Contains("NVIDIA CUDA 12", hardware.ExecutionDevice);
        Assert.Equal(8.0, hardware.TotalVramGb);
        Assert.Equal("Q4_K_M", config.Quantization);
        Assert.Equal("llama", config.BackendType);
        Assert.Equal(16, config.BatchSize);
        Assert.Equal(4096, config.ContextSize);
    }

    [Fact]
    public void OnnxTelemetryStrategy_Extract_DetectsCudaAndCpuModes()
    {
        var strategy = new OnnxTelemetryStrategy();
        var optionsCuda = new EmbeddingOptions
        {
            ModelPath = "C:\\models\\bge-small-zh\\model.onnx",
            ModelName = "bge-small-zh",
            UseCuda = true
        };

        var hardwareWithGpu = new HardwareContext
        {
            HasNvidiaGpu = true,
            PrimaryGpu = new GpuDeviceInfo { Name = "RTX 4060", TotalVramBytes = 8589934592 }
        };

        var (hardwareCuda, configCuda) = strategy.Extract(null, optionsCuda, hardwareWithGpu, null);
        Assert.True(hardwareCuda.IsGpuMode);
        Assert.Contains("CUDA Execution Provider", hardwareCuda.OffloadDescription);

        var optionsCpu = new EmbeddingOptions
        {
            ModelPath = "C:\\models\\bge-small-zh\\model.onnx",
            ModelName = "bge-small-zh",
            UseCuda = false
        };

        var (hardwareCpu, configCpu) = strategy.Extract(null, optionsCpu, hardwareWithGpu, null);
        Assert.False(hardwareCpu.IsGpuMode);
        Assert.Contains("CPU Execution Provider", hardwareCpu.OffloadDescription);
    }

    [Fact]
    public void OpenAiTelemetryStrategy_Extract_ReportsCloudApi()
    {
        var strategy = new OpenAiTelemetryStrategy();
        var options = new EmbeddingOptions
        {
            ModelPath = "http://localhost:11434",
            ModelName = "bge-m3"
        };
        var hardware = new HardwareContext();

        var (hw, cfg) = strategy.Extract(null, options, hardware, null);

        Assert.False(hw.IsGpuMode);
        Assert.Contains("OpenAI", hw.ExecutionDevice);
        Assert.Equal("openai", cfg.BackendType);
    }

    [Fact]
    public void ModelTelemetryService_ReflectsStateMachineLifecycleTransitions()
    {
        // 模拟带状态机的 LazyEmbeddingService
        using var stateMachine = new ModelLifecycleStateMachine<FakeEmbeddingAdapter>("TestService");
        var fakeAdapter = new FakeEmbeddingAdapter();

        // 初始状态为 Unloaded
        Assert.Equal(ModelState.Unloaded, stateMachine.State);

        var mockEmbedding = new Mock<IEmbeddingService>();
        var mockProvider = mockEmbedding.As<IModelRuntimeTelemetryProvider>();

        mockProvider.Setup(p => p.GetLifecycleSnapshot()).Returns(() => new ModelLifecycleSnapshot
        {
            State = stateMachine.State,
            StateDescription = stateMachine.State.ToString(),
            ActiveLeases = stateMachine.ActiveLeases,
            IsReady = stateMachine.IsReady
        });
        mockProvider.Setup(p => p.CurrentAdapter).Returns(fakeAdapter);
        mockProvider.Setup(p => p.CurrentOptions).Returns(new EmbeddingOptions
        {
            ModelPath = "bge-m3-q4_k_m.gguf",
            ModelName = "bge-m3",
            UseCuda = true
        });

        var mockDetector = new Mock<IHardwareDetector>();
        mockDetector.Setup(d => d.GetContext(false)).Returns(new HardwareContext
        {
            HasNvidiaGpu = true,
            PrimaryGpu = new GpuDeviceInfo { Name = "RTX 4060", TotalVramBytes = 8589934592 }
        });

        var mockModelManager = new Mock<IModelManager>();
        mockModelManager.Setup(m => m.GetCurrentModel()).Returns(new ModelInfo
        {
            Name = "bge-m3",
            DisplayName = "BGE M3 GGUF",
            QuantizationType = "Q4_K_M"
        });

        var telemetryService = new ModelTelemetryService(
            mockEmbedding.Object,
            mockDetector.Object,
            mockModelManager.Object);

        // 1. 未加载状态
        var telemetry1 = telemetryService.GetCurrentEmbeddingTelemetry();
        Assert.Equal(ModelState.Unloaded, telemetry1.StateMachine.State);
        Assert.False(telemetry1.StateMachine.IsReady);

        // 2. 加载后进入 Ready 状态
        stateMachine.TwoPhaseReloadAsync(() => Task.FromResult(fakeAdapter)).GetAwaiter().GetResult();
        var telemetry2 = telemetryService.GetCurrentEmbeddingTelemetry();
        Assert.Equal(ModelState.Ready, telemetry2.StateMachine.State);
        Assert.True(telemetry2.StateMachine.IsReady);
        Assert.Equal(0, telemetry2.StateMachine.ActiveLeases);

        // 3. 借用租约进入 Inferring 状态
        using (var lease = stateMachine.AcquireLease())
        {
            var telemetry3 = telemetryService.GetCurrentEmbeddingTelemetry();
            Assert.Equal(ModelState.Inferring, telemetry3.StateMachine.State);
            Assert.Equal(1, telemetry3.StateMachine.ActiveLeases);
        }

        // 4. 租约归还后回到 Ready 状态
        var telemetry4 = telemetryService.GetCurrentEmbeddingTelemetry();
        Assert.Equal(ModelState.Ready, telemetry4.StateMachine.State);
        Assert.Equal(0, telemetry4.StateMachine.ActiveLeases);
    }

    private class FakeEmbeddingAdapter : IEmbeddingBackendAdapter
    {
        public string BackendType => "llama";
        public string ModelPath => "bge-m3-q4_k_m.gguf";
        public string ModelName => "bge-m3";
        public int Dimension => 1024;
        public int MaxSequenceLength => 8192;
        public bool SupportsAsymmetricEncoding => true;
        public bool IsSimulationMode => false;

        public int CountTokens(string text) => text.Length / 4;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<float[]> EncodeAsync(string text, string? prefix = null, CancellationToken cancellationToken = default) => Task.FromResult(new float[1024]);
        public Task<float[][]> EncodeBatchAsync(IReadOnlyList<string> texts, string? prefix = null, CancellationToken cancellationToken = default) => Task.FromResult(new float[texts.Count][]);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
