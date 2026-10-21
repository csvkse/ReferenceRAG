using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using System.Text.Json;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Download.Models;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class ModelsControllerDownloadTests
{
    private readonly Mock<IModelManager> _mockModelManager = new();
    private readonly Mock<IEmbeddingService> _mockEmbeddingService = new();
    private readonly Mock<IVectorStore> _mockVectorStore = new();
    private readonly ConfigManager _configManager = new();

    private ModelsController CreateController()
    {
        return new ModelsController(
            _configManager,
            _mockModelManager.Object,
            _mockEmbeddingService.Object,
            _mockVectorStore.Object,
            NullLogger<ModelsController>.Instance);
    }

    [Fact]
    public void CancelDownload_WhenNoActiveTask_ReturnsOkWithCancelledTrue_Idempotent()
    {
        var controller = CreateController();
        var modelName = $"non-existent-model-{Guid.NewGuid():N}";

        var result = controller.CancelDownload(modelName);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(okResult.Value);

        var json = JsonSerializer.Serialize(okResult.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("wasRunning").GetBoolean());
    }

    [Fact]
    public void CancelRerankDownload_WhenNoActiveTask_ReturnsOkWithCancelledTrue()
    {
        var controller = CreateController();
        var modelName = $"non-existent-reranker-{Guid.NewGuid():N}";

        var result = controller.CancelRerankDownload(modelName);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(okResult.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task DownloadModel_WhenModelAlreadyDownloadedAndNoSpecificVariantRequested_ReturnsAlreadyDownloaded()
    {
        var controller = CreateController();
        var modelName = "Qwen3-Embedding-0.6B";

        _mockModelManager.Setup(m => m.GetAvailableModelsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<ModelInfo>
            {
                new ModelInfo
                {
                    Name = modelName,
                    DisplayName = "Qwen3 Embedding 0.6B",
                    IsDownloaded = true
                }
            });

        var result = await controller.DownloadModel(modelName, null);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(okResult.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("isAlreadyDownloaded").GetBoolean());
    }

    [Fact]
    public async Task DownloadModel_WhenModelDownloadedAndExistingVariantRequested_ReturnsAlreadyDownloaded()
    {
        var controller = CreateController();
        var modelName = "Qwen3-Embedding-0.6B";

        _mockModelManager.Setup(m => m.GetAvailableModelsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<ModelInfo>
            {
                new ModelInfo
                {
                    Name = modelName,
                    DisplayName = "Qwen3 Embedding 0.6B",
                    IsDownloaded = true,
                    InstalledVariants = new List<ModelVariantInfo>
                    {
                        new ModelVariantInfo { FileName = "Qwen3-Embedding-0.6B-Q4_0.gguf", RelativePath = "Qwen3-Embedding-0.6B-Q4_0.gguf" }
                    }
                }
            });

        var request = new DownloadModelRequest
        {
            OnnxFilePath = "Qwen3-Embedding-0.6B-Q4_0.gguf"
        };

        var result = await controller.DownloadModel(modelName, request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(okResult.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("isAlreadyDownloaded").GetBoolean());
    }

    [Fact]
    public async Task DownloadModel_WhenModelDownloadedButNewVariantRequested_StartsDownloadTask()
    {
        var controller = CreateController();
        var modelName = "Qwen3-Embedding-0.6B";

        _mockModelManager.Setup(m => m.GetAvailableModelsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<ModelInfo>
            {
                new ModelInfo
                {
                    Name = modelName,
                    DisplayName = "Qwen3 Embedding 0.6B",
                    IsDownloaded = true,
                    InstalledVariants = new List<ModelVariantInfo>
                    {
                        new ModelVariantInfo { FileName = "Qwen3-Embedding-0.6B-Q4_0.gguf", RelativePath = "Qwen3-Embedding-0.6B-Q4_0.gguf" }
                    }
                }
            });

        _mockModelManager.Setup(m => m.DownloadModelAsync(
                modelName,
                It.IsAny<IProgress<DownloadProgress>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync((true, null));

        var request = new DownloadModelRequest
        {
            OnnxFilePath = "Qwen3-Embedding-0.6B-Q8_0.gguf", // 新变体！
            Format = "gguf"
        };

        var result = await controller.DownloadModel(modelName, request);

        // 应启动异步下载任务，返回 Accepted (202) 而非 Ok 200 已下载
        var acceptedResult = Assert.IsType<AcceptedResult>(result);
        Assert.NotNull(acceptedResult.Value);

        // 取消该进行中的任务验证取消逻辑
        var cancelResult = controller.CancelDownload(modelName);
        var cancelOk = Assert.IsType<OkObjectResult>(cancelResult);
        var json = JsonSerializer.Serialize(cancelOk.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task SearchVariants_WhenQueryEmpty_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.SearchVariants("   ");

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public async Task SearchVariants_WhenQueryValid_CallsModelManagerAndReturnsResults()
    {
        var controller = CreateController();
        var expectedResults = new List<ModelVariantSearchResult>
        {
            new ModelVariantSearchResult
            {
                Id = "bartowski/bge-m3-GGUF",
                Author = "bartowski",
                ModelName = "bge-m3-GGUF",
                Downloads = 12000,
                Likes = 45,
                HasGguf = true
            }
        };

        _mockModelManager.Setup(m => m.SearchVariantsAsync("bge-m3", "gguf", 15, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResults);

        var result = await controller.SearchVariants("bge-m3", "gguf", 15);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedList = Assert.IsType<List<ModelVariantSearchResult>>(okResult.Value);
        Assert.Single(returnedList);
        Assert.Equal("bartowski/bge-m3-GGUF", returnedList[0].Id);
    }

    [Fact]
    public async Task GetVariantDetail_WhenRepositoryEmpty_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.GetVariantDetail("");

        var badRequestResult = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.NotNull(badRequestResult.Value);
    }

    [Fact]
    public async Task GetVariantDetail_WhenNotFound_ReturnsNotFound()
    {
        var controller = CreateController();

        _mockModelManager.Setup(m => m.GetVariantDetailAsync("nonexistent/repo", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelVariantDetail?)null);

        var result = await controller.GetVariantDetail("nonexistent/repo");

        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.NotNull(notFoundResult.Value);
    }

    [Fact]
    public async Task GetVariantDetail_WhenFound_ReturnsDetail()
    {
        var controller = CreateController();
        var expectedDetail = new ModelVariantDetail
        {
            Id = "bartowski/bge-m3-GGUF",
            Author = "bartowski",
            HasGguf = true,
            GgufFiles = new List<ModelVariantFileOption>
            {
                new ModelVariantFileOption
                {
                    FileName = "bge-m3-Q4_K_M.gguf",
                    Path = "bge-m3-Q4_K_M.gguf",
                    FileSize = 1_200_000_000,
                    Format = "gguf",
                    Quantization = "Q4_K_M",
                    IsRecommended = true
                }
            }
        };

        _mockModelManager.Setup(m => m.GetVariantDetailAsync("bartowski/bge-m3-GGUF", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDetail);

        var result = await controller.GetVariantDetail("bartowski/bge-m3-GGUF");

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<ModelVariantDetail>(okResult.Value);
        Assert.Equal("bartowski/bge-m3-GGUF", detail.Id);
        Assert.Single(detail.GgufFiles);
        Assert.Equal("Q4_K_M", detail.GgufFiles[0].Quantization);
    }

    [Fact]
    public async Task GetChatProjectorStatus_ReturnsStatusFromModelManager()
    {
        var controller = CreateController();
        var modelName = "Qwen3.5-4B-Instruct-GGUF";
        var expectedStatus = new ProjectorStatusResult
        {
            ModelName = modelName,
            IsMultimodal = true,
            HasLocalMmproj = false,
            HasRemoteMmproj = true,
            RemoteMmprojFileName = "mmproj-F16.gguf",
            RemoteMmprojPath = "mmproj-F16.gguf",
            RemoteMmprojSizeBytes = 672_000_000,
            Status = "missing",
            Message = "检测到仓库存在配套视觉投影器"
        };

        _mockModelManager.Setup(m => m.CheckProjectorStatusAsync(modelName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedStatus);

        var result = await controller.GetChatProjectorStatus(modelName);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<ProjectorStatusResult>(okResult.Value);
        Assert.Equal(modelName, status.ModelName);
        Assert.True(status.IsMultimodal);
        Assert.False(status.HasLocalMmproj);
        Assert.True(status.HasRemoteMmproj);
        Assert.Equal("missing", status.Status);
    }

    [Fact]
    public async Task DownloadChatProjector_WhenModelNotFound_ReturnsNotFound()
    {
        var controller = CreateController();
        var modelName = "NonExistentModel";

        _mockModelManager.Setup(m => m.GetAvailableModelsAsync("chat"))
            .ReturnsAsync(new List<ModelInfo>());

        var result = await controller.DownloadChatProjector(modelName);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(notFound.Value);
    }

    [Fact]
    public async Task DownloadChatProjector_WhenModelExists_StartsDownloadAndReturnsOk()
    {
        var controller = CreateController();
        var modelName = "Qwen3.5-4B-Instruct-GGUF";

        _mockModelManager.Setup(m => m.GetAvailableModelsAsync("chat"))
            .ReturnsAsync(new List<ModelInfo>
            {
                new ModelInfo
                {
                    Name = modelName,
                    DisplayName = "Qwen3.5 4B Instruct",
                    ModelType = "chat",
                    IsMultimodal = true,
                    IsDownloaded = true
                }
            });

        _mockModelManager.Setup(m => m.DownloadProjectorAsync(
            modelName,
            It.IsAny<string?>(),
            It.IsAny<string?>(),
            It.IsAny<IProgress<DownloadProgress>?>(),
            It.IsAny<CancellationToken>()))
            .ReturnsAsync((true, null));

        var result = await controller.DownloadChatProjector(modelName, new DownloadProjectorRequest
        {
            ProjectorPath = "mmproj-F16.gguf"
        });

        var okResult = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(okResult.Value);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("视觉投影器下载任务已启动", doc.RootElement.GetProperty("message").GetString());
    }
}

