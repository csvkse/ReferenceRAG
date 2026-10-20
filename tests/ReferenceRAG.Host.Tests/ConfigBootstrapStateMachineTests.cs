using System.Text.Json.Nodes;
using ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class ConfigBootstrapStateMachineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly string _templatePath;

    public ConfigBootstrapStateMachineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ConfigBootstrapTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "appsettings.json");
        _templatePath = Path.Combine(_tempDir, "appsettings.template.json");
    }

    public void Dispose()
    {
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

    [Fact]
    public void MissingConfig_WithTemplate_ExecutesTemplateSeedStrategy_CreatesValidV2Config()
    {
        var templateJson = new JsonObject
        {
            ["Logging"] = new JsonObject { ["Default"] = "Information" },
            ["ReferenceRAG"] = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["dataPath"] = "template_data",
                ["chat"] = new JsonObject
                {
                    ["openAiApi"] = new JsonObject
                    {
                        ["endpoint"] = "https://template.api/v1",
                        ["model"] = "template-model"
                    }
                }
            }
        };
        File.WriteAllText(_templatePath, templateJson.ToJsonString());

        var adapter = new LocalConfigEnvironmentAdapter(
            baseDirectory: _tempDir,
            customConfigPath: _configPath,
            customTemplatePath: _templatePath);

        var report = ConfigBootstrap.EnsureInitialized(customAdapter: adapter, forceRecheck: true);

        Assert.Equal(ConfigLifecycleState.Ready, report.FinalState);
        Assert.True(report.WasSeeded);
        Assert.False(report.WasHealed);
        Assert.Equal("TemplateFileSeed", report.UsedSeedStrategy);

        Assert.True(File.Exists(_configPath));
        var content = File.ReadAllText(_configPath);
        var root = JsonNode.Parse(content)?.AsObject();
        Assert.NotNull(root);
        Assert.Equal(2, root?["ReferenceRAG"]?["schemaVersion"]?.GetValue<int>());
        Assert.Equal("template_data", root?["ReferenceRAG"]?["dataPath"]?.GetValue<string>());
        Assert.Equal("template-model", root?["ReferenceRAG"]?["chat"]?["openAiApi"]?["model"]?.GetValue<string>());
    }

    [Fact]
    public void MissingConfig_WithoutTemplate_FallsBackToFactoryDefaultSeed_CreatesValidV2Config()
    {
        // 确保无模板文件且无配置文件
        if (File.Exists(_templatePath)) File.Delete(_templatePath);
        if (File.Exists(_configPath)) File.Delete(_configPath);

        var adapter = new LocalConfigEnvironmentAdapter(
            baseDirectory: _tempDir,
            customConfigPath: _configPath,
            customTemplatePath: Path.Combine(_tempDir, "non_existent_template.json"));

        var report = ConfigBootstrap.EnsureInitialized(customAdapter: adapter, forceRecheck: true);

        Assert.Equal(ConfigLifecycleState.Ready, report.FinalState);
        Assert.True(report.WasSeeded);
        Assert.False(report.WasHealed);
        Assert.Equal("FactoryDefaultSeed", report.UsedSeedStrategy);

        Assert.True(File.Exists(_configPath));
        var content = File.ReadAllText(_configPath);
        var root = JsonNode.Parse(content)?.AsObject();
        Assert.NotNull(root);
        Assert.Equal(2, root?["ReferenceRAG"]?["schemaVersion"]?.GetValue<int>());
        Assert.Equal("data", root?["ReferenceRAG"]?["dataPath"]?.GetValue<string>());
    }

    [Fact]
    public void CorruptedConfig_ZeroBytes_TriggersHealing_BacksUp_And_Recreates()
    {
        // 模拟断电产生的 0 字节损坏文件
        File.WriteAllBytes(_configPath, Array.Empty<byte>());

        var adapter = new LocalConfigEnvironmentAdapter(
            baseDirectory: _tempDir,
            customConfigPath: _configPath,
            customTemplatePath: Path.Combine(_tempDir, "non_existent_template.json"));

        var report = ConfigBootstrap.EnsureInitialized(customAdapter: adapter, forceRecheck: true);

        Assert.Equal(ConfigLifecycleState.Ready, report.FinalState);
        Assert.True(report.WasHealed);
        Assert.True(report.WasSeeded);
        Assert.NotNull(report.BackupPath);
        Assert.True(File.Exists(report.BackupPath));

        // 重新生成的 appsettings.json 必须合法健康
        Assert.True(File.Exists(_configPath));
        var content = File.ReadAllText(_configPath);
        var root = JsonNode.Parse(content)?.AsObject();
        Assert.NotNull(root);
        Assert.Equal(2, root?["ReferenceRAG"]?["schemaVersion"]?.GetValue<int>());
    }

    [Fact]
    public void CorruptedConfig_InvalidJsonSyntax_TriggersHealing_BacksUp_And_Recreates()
    {
        // 模拟 JSON 语法损坏
        File.WriteAllText(_configPath, "{ \"ReferenceRAG\": { \"broken_syntax\": [");

        var adapter = new LocalConfigEnvironmentAdapter(
            baseDirectory: _tempDir,
            customConfigPath: _configPath,
            customTemplatePath: Path.Combine(_tempDir, "non_existent_template.json"));

        var report = ConfigBootstrap.EnsureInitialized(customAdapter: adapter, forceRecheck: true);

        Assert.Equal(ConfigLifecycleState.Ready, report.FinalState);
        Assert.True(report.WasHealed);
        Assert.True(report.WasSeeded);
        Assert.NotNull(report.BackupPath);
        Assert.True(File.Exists(report.BackupPath));

        var backupContent = File.ReadAllText(report.BackupPath);
        Assert.Contains("broken_syntax", backupContent);

        Assert.True(File.Exists(_configPath));
        var content = File.ReadAllText(_configPath);
        var root = JsonNode.Parse(content)?.AsObject();
        Assert.NotNull(root);
        Assert.Equal(2, root?["ReferenceRAG"]?["schemaVersion"]?.GetValue<int>());
    }

    [Fact]
    public void ExistingLegacyV1Config_TriggersMigration_UpgradesToV2_PreservingApiKey()
    {
        var legacyJson = new JsonObject
        {
            ["Chat"] = new JsonObject
            {
                ["Endpoint"] = "https://legacy.api/v1",
                ["ApiKey"] = "sk-legacy-preserved-key",
                ["Model"] = "legacy-model",
                ["SystemPrompt"] = "Legacy prompt"
            }
        };
        File.WriteAllText(_configPath, legacyJson.ToJsonString());

        var adapter = new LocalConfigEnvironmentAdapter(
            baseDirectory: _tempDir,
            customConfigPath: _configPath);

        var report = ConfigBootstrap.EnsureInitialized(customAdapter: adapter, forceRecheck: true);

        Assert.Equal(ConfigLifecycleState.Ready, report.FinalState);
        Assert.False(report.WasSeeded);
        Assert.False(report.WasHealed);
        Assert.True(report.WasMigrated);

        var content = File.ReadAllText(_configPath);
        var root = JsonNode.Parse(content)?.AsObject();
        Assert.NotNull(root);
        Assert.Null(root?["Chat"]);
        Assert.Equal(2, root?["ReferenceRAG"]?["schemaVersion"]?.GetValue<int>());
        Assert.Equal("sk-legacy-preserved-key", root?["ReferenceRAG"]?["chat"]?["openAiApi"]?["apiKey"]?.GetValue<string>());
        Assert.Equal("https://legacy.api/v1", root?["ReferenceRAG"]?["chat"]?["openAiApi"]?["endpoint"]?.GetValue<string>());
    }

    [Fact]
    public void ExistingHealthyV2Config_PreservesEverything_NoSeed_NoHeal_NoMigration()
    {
        var healthyJson = new JsonObject
        {
            ["ReferenceRAG"] = new JsonObject
            {
                ["schemaVersion"] = 2,
                ["dataPath"] = "my_custom_data_path",
                ["chat"] = new JsonObject
                {
                    ["openAiApi"] = new JsonObject
                    {
                        ["apiKey"] = "sk-my-active-secret",
                        ["model"] = "custom-model"
                    }
                }
            }
        };
        File.WriteAllText(_configPath, healthyJson.ToJsonString());

        var adapter = new LocalConfigEnvironmentAdapter(
            baseDirectory: _tempDir,
            customConfigPath: _configPath);

        var report = ConfigBootstrap.EnsureInitialized(customAdapter: adapter, forceRecheck: true);

        Assert.Equal(ConfigLifecycleState.Ready, report.FinalState);
        Assert.False(report.WasSeeded);
        Assert.False(report.WasHealed);
        Assert.False(report.WasMigrated);

        var content = File.ReadAllText(_configPath);
        var root = JsonNode.Parse(content)?.AsObject();
        Assert.NotNull(root);
        Assert.Equal("my_custom_data_path", root?["ReferenceRAG"]?["dataPath"]?.GetValue<string>());
        Assert.Equal("sk-my-active-secret", root?["ReferenceRAG"]?["chat"]?["openAiApi"]?["apiKey"]?.GetValue<string>());
    }

    [Fact]
    public void AtomicWrite_ProducesValidFile_WithoutLeftoverTempFiles()
    {
        var adapter = new LocalConfigEnvironmentAdapter(
            baseDirectory: _tempDir,
            customConfigPath: _configPath);

        const string testContent = "{\"test\": 123}";
        adapter.WriteAtomic(_configPath, testContent);

        Assert.True(File.Exists(_configPath));
        Assert.Equal(testContent, File.ReadAllText(_configPath));

        var tempFiles = Directory.GetFiles(_tempDir, "*.tmp.*");
        Assert.Empty(tempFiles);
    }
}
