using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Core.Features.ModelManagement.StateMachine;
using ReferenceRAG.Core.Features.ModelManagement.Telemetry;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class PerformanceControllerTests
{
    [Fact]
    public void PerformanceController_GetModelStatus_ReturnsValidTelemetry()
    {
        var mockEmbedding = new Mock<IEmbeddingService>();
        var mockChunker = new Mock<IMarkdownChunker>();
        var mockTokenizer = new Mock<ITokenizer>();
        var mockTelemetryService = new Mock<IModelTelemetryService>();

        var expectedTelemetry = new ModelRuntimeTelemetry
        {
            StateMachine = new ModelLifecycleSnapshot { State = ModelState.Ready, IsReady = true, StateDescription = "就绪" },
            Hardware = new ModelHardwareTelemetry { IsGpuMode = true, ExecutionDevice = "NVIDIA CUDA 12" },
            Configuration = new ModelConfigTelemetry { ModelName = "bge-m3.gguf", Quantization = "Q4_K_M", BatchSize = 32 }
        };

        mockTelemetryService.Setup(s => s.GetCurrentEmbeddingTelemetry()).Returns(expectedTelemetry);

        var configManager = new ConfigManager();
        var controller = new PerformanceController(
            mockEmbedding.Object,
            mockChunker.Object,
            mockTokenizer.Object,
            configManager,
            mockTelemetryService.Object,
            NullLogger<PerformanceController>.Instance);

        var actionResult = controller.GetModelStatus();
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var result = Assert.IsType<ModelRuntimeTelemetry>(okResult.Value);

        Assert.Equal(ModelState.Ready, result.StateMachine.State);
        Assert.True(result.Hardware.IsGpuMode);
        Assert.Equal("Q4_K_M", result.Configuration.Quantization);
    }

    [Fact]
    public void PerformanceController_GetRerankStatus_ReturnsValidTelemetry()
    {
        var mockEmbedding = new Mock<IEmbeddingService>();
        var mockChunker = new Mock<IMarkdownChunker>();
        var mockTokenizer = new Mock<ITokenizer>();
        var mockTelemetryService = new Mock<IModelTelemetryService>();

        var expectedTelemetry = new ModelRuntimeTelemetry
        {
            StateMachine = new ModelLifecycleSnapshot { State = ModelState.Ready, IsReady = true, StateDescription = "就绪" },
            Hardware = new ModelHardwareTelemetry { IsGpuMode = true, ExecutionDevice = "NVIDIA CUDA 12" },
            Configuration = new ModelConfigTelemetry { ModelName = "bge-reranker-v2-m3-Q4_K_M.gguf", Quantization = "Q4_K_M", BatchSize = 16 }
        };

        mockTelemetryService.Setup(s => s.GetCurrentRerankTelemetry()).Returns(expectedTelemetry);

        var configManager = new ConfigManager();
        var controller = new PerformanceController(
            mockEmbedding.Object,
            mockChunker.Object,
            mockTokenizer.Object,
            configManager,
            mockTelemetryService.Object,
            NullLogger<PerformanceController>.Instance);

        var actionResult = controller.GetRerankStatus();
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var result = Assert.IsType<ModelRuntimeTelemetry>(okResult.Value);

        Assert.Equal(ModelState.Ready, result.StateMachine.State);
        Assert.True(result.Hardware.IsGpuMode);
        Assert.Equal("Q4_K_M", result.Configuration.Quantization);
    }
}
