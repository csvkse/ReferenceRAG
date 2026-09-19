using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

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
        Directory.SetCurrentDirectory(_originalDir);
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    [Fact]
    public void Save_AlwaysPreservesExistingSources_EvenWhenClientSendsEmptyOrAlteredSources()
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

        var result = controller.Save(clientConfig);
        Assert.IsType<OkObjectResult>(result);

        // 门禁断言：无论客户端传来什么 Sources，磁盘上的配置必须完整保留原有 Sources
        var saved = configManager.Load();
        Assert.Single(saved.Sources);
        Assert.Equal("CriticalKnowledgeBase", saved.Sources[0].Name);
        Assert.Equal("D:/Vault", saved.Sources[0].Path);
        Assert.Equal("new_data_path", saved.DataPath);
    }
}
