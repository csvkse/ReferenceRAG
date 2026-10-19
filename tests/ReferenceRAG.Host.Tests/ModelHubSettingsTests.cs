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
public class ModelHubSettingsTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _originalDir;
    private readonly string? _originalHfToken;
    private readonly string? _originalHfEndpoint;

    public ModelHubSettingsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"modelhub-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _originalDir = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_testDir);

        _originalHfToken = Environment.GetEnvironmentVariable("HF_TOKEN");
        _originalHfEndpoint = Environment.GetEnvironmentVariable("HF_ENDPOINT");
    }

    public void Dispose()
    {
        try
        {
            Environment.SetEnvironmentVariable("HF_TOKEN", _originalHfToken);
            Environment.SetEnvironmentVariable("HF_ENDPOINT", _originalHfEndpoint);

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
    public void Load_DefaultConfig_InitializesModelHubWithDefaults()
    {
        Environment.SetEnvironmentVariable("HF_TOKEN", null);
        Environment.SetEnvironmentVariable("HF_ENDPOINT", null);

        var configManager = new ConfigManager();
        var config = configManager.Load();

        Assert.NotNull(config.ModelHub);
        Assert.True(string.IsNullOrEmpty(config.ModelHub.HfToken));
        Assert.Equal("https://hf-mirror.com", config.ModelHub.HfEndpoint);
        Assert.Equal(30, config.ModelHub.TimeoutSeconds);
    }

    [Fact]
    public void Save_ThenLoad_PersistsModelHubAndSyncsEnvironmentVariables()
    {
        var configManager = new ConfigManager();
        var config = new ObsidianRagConfig
        {
            ModelHub = new ModelHubConfig
            {
                HfToken = "hf_custom_mock_token_123456",
                HfEndpoint = "https://custom-mirror.internal",
                TimeoutSeconds = 450
            }
        };

        configManager.Save(config);

        // 验证当前进程环境变量同步成功
        Assert.Equal("hf_custom_mock_token_123456", Environment.GetEnvironmentVariable("HF_TOKEN"));
        Assert.Equal("https://custom-mirror.internal", Environment.GetEnvironmentVariable("HF_ENDPOINT"));

        // 验证重新载入配置正确持久化
        var reloaded = configManager.Load();
        Assert.NotNull(reloaded.ModelHub);
        Assert.Equal("hf_custom_mock_token_123456", reloaded.ModelHub.HfToken);
        Assert.Equal("https://custom-mirror.internal", reloaded.ModelHub.HfEndpoint);
        Assert.Equal(450, reloaded.ModelHub.TimeoutSeconds);
    }

    [Fact]
    public void Load_WhenConfigEmpty_FallsBackToEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("HF_TOKEN", "hf_env_token_override");
        Environment.SetEnvironmentVariable("HF_ENDPOINT", "https://env-endpoint.local");

        var configManager = new ConfigManager();
        var config = configManager.Load();

        Assert.NotNull(config.ModelHub);
        Assert.Equal("hf_env_token_override", config.ModelHub.HfToken);
        Assert.Equal("https://env-endpoint.local", config.ModelHub.HfEndpoint);
    }

    [Fact]
    public async Task VerifyHfToken_EmptyOrWhitespaceToken_ReturnsBadRequest()
    {
        var configManager = new ConfigManager();
        var mockModelManager = new Mock<IModelManager>();
        var controller = new SettingsController(
            configManager,
            mockModelManager.Object,
            NullLogger<SettingsController>.Instance);

        var resultEmpty = await controller.VerifyHfToken(new VerifyHfTokenRequest { Token = "" });
        Assert.IsType<BadRequestObjectResult>(resultEmpty);

        var resultWhitespace = await controller.VerifyHfToken(new VerifyHfTokenRequest { Token = "   \t\n  " });
        Assert.IsType<BadRequestObjectResult>(resultWhitespace);
    }

    [Fact]
    public async Task SettingsController_Save_PreservesModelHubSettings()
    {
        var configManager = new ConfigManager();
        var initial = new ObsidianRagConfig
        {
            ModelHub = new ModelHubConfig
            {
                HfToken = "hf_initial_token",
                HfEndpoint = "https://initial-mirror.com",
                TimeoutSeconds = 200
            }
        };
        configManager.Save(initial);

        var mockModelManager = new Mock<IModelManager>();
        var controller = new SettingsController(
            configManager,
            mockModelManager.Object,
            NullLogger<SettingsController>.Instance);

        var updateConfig = new ObsidianRagConfig
        {
            DataPath = "new_data_path",
            ModelHub = new ModelHubConfig
            {
                HfToken = "hf_updated_token",
                HfEndpoint = "https://updated-mirror.com",
                TimeoutSeconds = 500
            }
        };

        var saveResult = await controller.Save(updateConfig);
        Assert.IsType<OkObjectResult>(saveResult.Result);

        var saved = configManager.Load();
        Assert.Equal("hf_updated_token", saved.ModelHub.HfToken);
        Assert.Equal("https://updated-mirror.com", saved.ModelHub.HfEndpoint);
        Assert.Equal(500, saved.ModelHub.TimeoutSeconds);
    }
}
