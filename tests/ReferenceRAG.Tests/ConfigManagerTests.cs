using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Tests;

public class ConfigManagerTests : IDisposable
{
    private readonly string _testConfigPath;
    private readonly ConfigManager _configManager;

    public ConfigManagerTests()
    {
        _testConfigPath = Path.Combine(Path.GetTempPath(), $"test_config_{Guid.NewGuid()}.json");
        _configManager = new ConfigManager(_testConfigPath);
    }

    [Fact]
    public void Load_WithNoExistingConfig_ReturnsDefaultConfig()
    {
        if (File.Exists(_testConfigPath))
            File.Delete(_testConfigPath);

        var config = _configManager.Load();

        Assert.NotNull(config);
        Assert.Equal("data", config.DataPath);
        Assert.NotNull(config.Sources);
        Assert.Empty(config.Sources);
    }

    [Fact]
    public void Save_ThenLoad_ReturnsSavedConfig()
    {
        var config = new ObsidianRagConfig
        {
            DataPath = "/custom/data/path",
            Sources = new List<SourceFolder>
            {
                new SourceFolder
                {
                    Name = "Test Source",
                    Path = "/test/path",
                    Type = SourceType.Obsidian
                }
            }
        };

        _configManager.Save(config);

        var loaded = _configManager.Load();

        Assert.Equal("/custom/data/path", loaded.DataPath);
        Assert.Single(loaded.Sources);
        Assert.Equal("Test Source", loaded.Sources[0].Name);
    }

    [Fact]
    public void AddSource_AddsSourceToConfig()
    {
        var source = new SourceFolder
        {
            Name = "New Source",
            Path = "/new/path",
            Type = SourceType.Markdown
        };

        _configManager.AddSource(source);

        var config = _configManager.Load();
        Assert.Single(config.Sources);
        Assert.Equal("New Source", config.Sources[0].Name);
    }

    [Fact]
    public void RemoveSource_RemovesSourceFromConfig()
    {
        var source = new SourceFolder
        {
            Name = "Source To Remove",
            Path = "/remove/path"
        };
        _configManager.AddSource(source);

        _configManager.RemoveSource("Source To Remove");

        var config = _configManager.Load();
        Assert.Empty(config.Sources);
    }

    [Fact]
    public void ToggleSource_TogglesEnabledState()
    {
        var source = new SourceFolder
        {
            Name = "Toggle Test",
            Path = "/toggle/path",
            Enabled = true
        };
        _configManager.AddSource(source);

        _configManager.ToggleSource("Toggle Test", false);

        var config = _configManager.Load();
        Assert.False(config.Sources[0].Enabled);
    }

    [Fact]
    public void AddSource_WithDuplicatePath_DoesNotAddDuplicate()
    {
        var source1 = new SourceFolder { Name = "Source 1", Path = "/duplicate/path" };
        var source2 = new SourceFolder { Name = "Source 2", Path = "/duplicate/path" };

        _configManager.AddSource(source1);
        _configManager.AddSource(source2);

        var config = _configManager.Load();
        Assert.Single(config.Sources);
        Assert.Equal("Source 1", config.Sources[0].Name);
    }

    public void Dispose()
    {
        if (File.Exists(_testConfigPath))
        {
            File.Delete(_testConfigPath);
        }
    }
}
