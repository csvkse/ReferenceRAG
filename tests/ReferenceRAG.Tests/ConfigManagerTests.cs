using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Tests;

public class ConfigManagerTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _originalDir;

    public ConfigManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"config-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _originalDir = Directory.GetCurrentDirectory();
    }

    private ConfigManager CreateConfigManager()
    {
        Directory.SetCurrentDirectory(_testDir);
        var logger = NullLogger<SearchService>.Instance;
        return new ConfigManager();
    }

    [Fact]
    public void Load_WithNoExistingConfig_ReturnsDefaultConfig()
    {
        var cm = CreateConfigManager();
        var config = cm.Load();

        Assert.NotNull(config);
        Assert.Equal("data", config.DataPath);
        Assert.NotNull(config.Sources);
        Assert.Empty(config.Sources);
    }

    [Fact]
    public void Save_ThenLoad_ReturnsSavedConfig()
    {
        var cm = CreateConfigManager();
        var config = new ObsidianRagConfig
        {
            DataPath = "/custom/data/path",
            Sources =
            [
                new SourceFolder
                {
                    Name = "Test Source",
                    Path = "/test/path",
                    Type = SourceType.Obsidian
                }
            ]
        };

        cm.Save(config);

        var cm2 = CreateConfigManager();
        var loaded = cm2.Load();

        Assert.Equal("/custom/data/path", loaded.DataPath);
        Assert.Single(loaded.Sources);
        Assert.Equal("Test Source", loaded.Sources[0].Name);
    }

    [Fact]
    public void AddSource_AddsSourceToConfig()
    {
        var cm = CreateConfigManager();
        var source = new SourceFolder
        {
            Name = "New Source",
            Path = "/new/path",
            Type = SourceType.Markdown
        };

        cm.AddSource(source);

        var cm2 = CreateConfigManager();
        var config = cm2.Load();
        Assert.Single(config.Sources);
        Assert.Equal("New Source", config.Sources[0].Name);
    }

    [Fact]
    public void RemoveSource_RemovesSourceFromConfig()
    {
        var cm = CreateConfigManager();
        var source = new SourceFolder
        {
            Name = "Source To Remove",
            Path = "/remove/path"
        };
        cm.AddSource(source);

        cm.RemoveSource("Source To Remove");

        var cm2 = CreateConfigManager();
        var config = cm2.Load();
        Assert.Empty(config.Sources);
    }

    [Fact]
    public void ToggleSource_TogglesEnabledState()
    {
        var cm = CreateConfigManager();
        var source = new SourceFolder
        {
            Name = "Toggle Test",
            Path = "/toggle/path",
            Enabled = true
        };
        cm.AddSource(source);

        cm.ToggleSource("Toggle Test", false);

        var cm2 = CreateConfigManager();
        var config = cm2.Load();
        Assert.False(config.Sources[0].Enabled);
    }

    [Fact]
    public void AddSource_WithDuplicatePath_DoesNotAddDuplicate()
    {
        var cm = CreateConfigManager();
        var source1 = new SourceFolder { Name = "Source 1", Path = "/duplicate/path" };
        var source2 = new SourceFolder { Name = "Source 2", Path = "/duplicate/path" };

        cm.AddSource(source1);
        cm.AddSource(source2);

        var cm2 = CreateConfigManager();
        var config = cm2.Load();
        Assert.Single(config.Sources);
        Assert.Equal("Source 1", config.Sources[0].Name);
    }

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(_originalDir); } catch { }
        try { Directory.Delete(_testDir, true); } catch { }
    }
}
