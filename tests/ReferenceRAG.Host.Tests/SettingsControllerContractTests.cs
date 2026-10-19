using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

[Collection("CurrentDirectoryCollection")]
public class SettingsControllerContractTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _originalDir;

    public SettingsControllerContractTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"settings-ctrl-tests-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task Save_AlwaysPreservesExistingSources_EvenWhenClientSendsEmptyOrAlteredSources()
    {
        var configManager = new ConfigManager();
        var initial = new ObsidianRagConfig
        {
            Sources =
            [
                new SourceFolder { Name = "CriticalKnowledgeBase", Path = "D:/Vault", Enabled = true }
            ]
        };
        configManager.Save(initial);

        var mockModelManager = new Mock<IModelManager>();
        var controller = new SettingsController(
            configManager,
            mockModelManager.Object,
            NullLogger<SettingsController>.Instance);

        // 客户端请求试图保存修改后的配置，但没有带 Sources（或传了空）
        var clientConfig = new ObsidianRagConfig
        {
            DataPath = "new_data_path",
            Sources = new List<SourceFolder>() // 试图覆盖为空
        };

        var result = await controller.Save(clientConfig);
        Assert.IsType<OkObjectResult>(result.Result);

        // 门禁断言：无论客户端传来什么 Sources，磁盘上的配置必须完整保留原有 Sources
        var saved = configManager.Load();
        Assert.Single(saved.Sources);
        Assert.Equal("CriticalKnowledgeBase", saved.Sources[0].Name);
        Assert.Equal("D:/Vault", saved.Sources[0].Path);
        Assert.Equal("new_data_path", saved.DataPath);
    }

    [Fact]
    public async Task Save_ModelParametersChanged_DoesNotRequireRestartAndInvokesHotReload()
    {
        var configManager = new ConfigManager();
        var initial = new ObsidianRagConfig
        {
            Embedding = new EmbeddingConfig { Mode = "onnx", ModelName = "bge-small", ApiBaseUrl = "http://old:11434" },
            Rerank = new RerankConfig { Mode = "onnx", ModelName = "bge-reranker", Enabled = true }
        };
        configManager.Save(initial);

        var mockModelManager = new Mock<IModelManager>();
        var mockEmbedding = new Mock<IEmbeddingService>();
        var mockConfigurableEmbedding = mockEmbedding.As<IModelConfigurableTarget>();
        mockConfigurableEmbedding
            .Setup(m => m.ApplyConfigurationAsync(It.IsAny<EmbeddingConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var mockRerank = new Mock<IRerankService>();
        var mockConfigurableRerank = mockRerank.As<IRerankConfigurableTarget>();
        mockConfigurableRerank
            .Setup(m => m.ApplyConfigurationAsync(It.IsAny<RerankConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var controller = new SettingsController(
            configManager,
            mockModelManager.Object,
            NullLogger<SettingsController>.Instance,
            mockEmbedding.Object,
            mockRerank.Object);

        var updated = new ObsidianRagConfig
        {
            Embedding = new EmbeddingConfig { Mode = "onnx", ModelName = "bge-small", ApiBaseUrl = "http://new:11434" },
            Rerank = new RerankConfig { Mode = "onnx", ModelName = "bge-reranker", Enabled = false }
        };

        var actionResult = await controller.Save(updated);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var val = Assert.IsType<SettingsSaveResult>(okResult.Value);
        Assert.False(val.RequiresRestart, "仅模型参数/启用状态变更不应要求重启服务");
        Assert.False(val.IndexRequiresRebuild, "同模型仅修改API/启用状态不应要求重建索引");

        // 验证热更新适配器契约被调用
        mockConfigurableEmbedding.Verify(m => m.ApplyConfigurationAsync(It.IsAny<EmbeddingConfig>(), It.IsAny<CancellationToken>()), Times.Once);
        mockConfigurableRerank.Verify(m => m.ApplyConfigurationAsync(It.IsAny<RerankConfig>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Save_PortChanged_FlagsRequiresRestart()
    {
        var configManager = new ConfigManager();
        var initial = new ObsidianRagConfig
        {
            Service = new ServiceConfig { Port = 5000, AllowNetworkAccess = false }
        };
        configManager.Save(initial);

        var mockModelManager = new Mock<IModelManager>();
        var controller = new SettingsController(
            configManager,
            mockModelManager.Object,
            NullLogger<SettingsController>.Instance);

        var updated = new ObsidianRagConfig
        {
            Service = new ServiceConfig { Port = 5001, AllowNetworkAccess = false }
        };

        var actionResult = await controller.Save(updated);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var val = Assert.IsType<SettingsSaveResult>(okResult.Value);
        Assert.True(val.RequiresRestart, "服务端口变更是底层网络套接字变更，必须标记为需要重启");
    }
}
