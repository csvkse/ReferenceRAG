namespace ReferenceRAG.Host.Tests;

using System;
using System.IO;
using System.Text.Json.Nodes;
using ReferenceRAG.Infrastructure.Features.Storage.Bootstrap;
using Xunit;

public class ConfigPathResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dataDir;

    public ConfigPathResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ConfigPathTests_" + Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_tempDir, "data");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_dataDir);

        // 清理可能污染测试的环境变量
        Environment.SetEnvironmentVariable(ConfigPathResolver.ConfigPathEnvVar, null);
        Environment.SetEnvironmentVariable("REFERENCERAG_DATA_PATH", null);
        Environment.SetEnvironmentVariable("DataPath", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ConfigPathResolver.ConfigPathEnvVar, null);
        Environment.SetEnvironmentVariable("REFERENCERAG_DATA_PATH", null);
        Environment.SetEnvironmentVariable("DataPath", null);

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void ResolveConfigPath_Default_ReturnsDataDirectoryAppsettings()
    {
        var resolved = ConfigPathResolver.ResolveConfigPath(_tempDir);
        var expected = Path.GetFullPath(Path.Combine(_tempDir, "data", "appsettings.json"));

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveConfigPath_ExplicitEnvVar_TakesHighestPrecedence()
    {
        var customPath = Path.Combine(_tempDir, "custom", "my-config.json");
        Environment.SetEnvironmentVariable(ConfigPathResolver.ConfigPathEnvVar, customPath);

        var resolved = ConfigPathResolver.ResolveConfigPath(_tempDir);

        Assert.Equal(Path.GetFullPath(customPath), resolved);
    }

    [Fact]
    public void ResolveConfigPath_CustomDataPathEnvVar_ResolvesToCustomDataConfig()
    {
        var customDataDir = Path.Combine(_tempDir, "custom_data");
        Environment.SetEnvironmentVariable("DataPath", customDataDir);

        var resolved = ConfigPathResolver.ResolveConfigPath(_tempDir);
        var expected = Path.GetFullPath(Path.Combine(customDataDir, "appsettings.json"));

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ConfigBootstrap_ColdStart_AutoSeedsAndInitializesDataAppsettings()
    {
        var rootConfig = Path.Combine(_tempDir, "appsettings.json");
        var rootJson = new JsonObject
        {
            ["ReferenceRAG"] = new JsonObject
            {
                ["dataPath"] = "/app/data",
                ["service"] = new JsonObject
                {
                    ["port"] = 5123
                }
            }
        };
        File.WriteAllText(rootConfig, rootJson.ToJsonString());

        var adapter = new LocalConfigEnvironmentAdapter(baseDirectory: _tempDir);
        Assert.Equal(Path.GetFullPath(Path.Combine(_dataDir, "appsettings.json")), adapter.ConfigPath);

        var report = ConfigBootstrap.EnsureInitialized(customAdapter: adapter, forceRecheck: true);

        Assert.Equal(ConfigLifecycleState.Ready, report.FinalState);
        Assert.True(report.WasSeeded);

        var targetDataConfig = Path.Combine(_dataDir, "appsettings.json");
        Assert.True(File.Exists(targetDataConfig));

        var dataJson = File.ReadAllText(targetDataConfig);
        var node = JsonNode.Parse(dataJson)?.AsObject();
        Assert.NotNull(node);
        Assert.Equal(5123, node?["ReferenceRAG"]?["service"]?["port"]?.GetValue<int>());
        Assert.Equal(2, node?["ReferenceRAG"]?["schemaVersion"]?.GetValue<int>());
    }
}
