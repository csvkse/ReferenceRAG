using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class MafChatControllerConfigTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _origCurrentDir;
    private readonly string _appSettingsPath;

    public MafChatControllerConfigTests()
    {
        _origCurrentDir = Directory.GetCurrentDirectory();
        _tempDir = Path.Combine(Path.GetTempPath(), "MafChatControllerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        Directory.SetCurrentDirectory(_tempDir);
        _appSettingsPath = Path.Combine(_tempDir, "appsettings.json");
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_origCurrentDir);
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp dir cleanup errors
        }
    }

    private MafChatController CreateController(string initialApiKey = "sk-initial-secret")
    {
        var initialJson = new JsonObject
        {
            ["Chat"] = new JsonObject
            {
                ["Endpoint"] = "https://api.openai.com/v1",
                ["ApiKey"] = initialApiKey,
                ["Model"] = "gpt-4o-mini",
                ["SystemPrompt"] = "Initial prompt"
            }
        };
        File.WriteAllText(_appSettingsPath, initialJson.ToJsonString());

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:Endpoint"] = "https://api.openai.com/v1",
                ["Chat:ApiKey"] = initialApiKey,
                ["Chat:Model"] = "gpt-4o-mini",
                ["Chat:SystemPrompt"] = "Initial prompt"
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddHttpClient()
            .BuildServiceProvider();

        var chatService = new MafChatService(
            config,
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IHttpClientFactory>(),
            NullLogger<MafChatService>.Instance);

        return new MafChatController(chatService, config, NullLogger<MafChatController>.Instance);
    }

    [Fact]
    public void GetConfig_DesensitizesApiKey_AndReportsConfigured()
    {
        var controller = CreateController("sk-test-secret-key");
        var result = controller.GetConfig();

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var chatConfig = Assert.IsType<ChatConfig>(okResult.Value);

        Assert.Equal(string.Empty, chatConfig.ApiKey);
        Assert.True(chatConfig.ApiKeyConfigured);
        Assert.Equal("gpt-4o-mini", chatConfig.Model);
    }

    [Fact]
    public void SaveConfig_WithEmptyApiKey_PreservesExistingConfiguredKey()
    {
        var controller = CreateController("sk-existing-protected-key");

        // 模拟前端脱敏传入空 ApiKey，仅修改 Model 和 SystemPrompt
        var saveResult = controller.SaveConfig(new ChatConfig
        {
            Endpoint = "https://api.deepseek.com/v1",
            ApiKey = "",
            Model = "deepseek-chat",
            SystemPrompt = "Updated prompt"
        });

        Assert.IsType<OkObjectResult>(saveResult);

        // 验证 appsettings.json 中原有的有效 ApiKey 坚决不被抹空
        var savedJson = File.ReadAllText(_appSettingsPath);
        var root = JsonNode.Parse(savedJson)?.AsObject();
        var keyInFile = root?["Chat"]?["ApiKey"]?.GetValue<string>();

        Assert.Equal("sk-existing-protected-key", keyInFile);
        Assert.Equal("deepseek-chat", root?["Chat"]?["Model"]?.GetValue<string>());

        // 再次获取配置确认仍标记为已配置
        var getConfigResult = controller.GetConfig();
        var okResult = Assert.IsType<OkObjectResult>(getConfigResult.Result);
        var chatConfig = Assert.IsType<ChatConfig>(okResult.Value);
        Assert.True(chatConfig.ApiKeyConfigured);
    }

    [Fact]
    public void SaveConfig_WithNewApiKey_UpdatesKeySuccessfully()
    {
        var controller = CreateController("sk-old-key");

        var saveResult = controller.SaveConfig(new ChatConfig
        {
            Endpoint = "https://api.openai.com/v1",
            ApiKey = "sk-brand-new-key",
            Model = "gpt-4o",
            SystemPrompt = "New prompt"
        });

        Assert.IsType<OkObjectResult>(saveResult);

        var savedJson = File.ReadAllText(_appSettingsPath);
        var root = JsonNode.Parse(savedJson)?.AsObject();
        var keyInFile = root?["Chat"]?["ApiKey"]?.GetValue<string>();

        Assert.Equal("sk-brand-new-key", keyInFile);
    }

    [Fact]
    public void SaveConfig_WithExplicitClearSentinel_ClearsApiKey()
    {
        var controller = CreateController("sk-existing-key-to-clear");

        var saveResult = controller.SaveConfig(new ChatConfig
        {
            Endpoint = "https://api.openai.com/v1",
            ApiKey = "__CLEAR__",
            Model = "gpt-4o-mini",
            SystemPrompt = "Prompt"
        });

        Assert.IsType<OkObjectResult>(saveResult);

        var savedJson = File.ReadAllText(_appSettingsPath);
        var root = JsonNode.Parse(savedJson)?.AsObject();
        var keyInFile = root?["Chat"]?["ApiKey"]?.GetValue<string>();

        Assert.Equal(string.Empty, keyInFile);

        var getConfigResult = controller.GetConfig();
        var okResult = Assert.IsType<OkObjectResult>(getConfigResult.Result);
        var chatConfig = Assert.IsType<ChatConfig>(okResult.Value);
        Assert.False(chatConfig.ApiKeyConfigured);
    }
}
