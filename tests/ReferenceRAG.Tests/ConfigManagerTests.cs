using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Core.Helpers;

namespace ReferenceRAG.Tests;

public class ConfigManagerTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _originalDir;

    public ConfigManagerTests()
    {
        // 初始化 StaticLogger 以避免 NullReferenceException
        StaticLogger.LoggerFactory ??= LoggerFactory.Create(builder => builder.AddConsole());

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
    public void Save_AfterInvalidRemoteModelSettings_ReplacesUrlAndKey()
    {
        var cm = CreateConfigManager();
        var config = new ObsidianRagConfig();
        config.Embedding.Mode = "openai";
        config.Embedding.ApiBaseUrl = "not-a-valid-url";
        config.Embedding.ApiKey = "wrong-key";
        cm.Save(config);

        config.Embedding.ApiBaseUrl = "https://example.test/v1";
        config.Embedding.ApiKey = "corrected-key";
        cm.Save(config);

        using var document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_testDir, "data", "appsettings.json")));
        var embedding = document.RootElement
            .GetProperty("ReferenceRAG")
            .GetProperty("embedding");

        Assert.Equal("https://example.test/v1", embedding.GetProperty("apiBaseUrl").GetString());
        Assert.Equal("corrected-key", embedding.GetProperty("apiKey").GetString());
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

    [Fact(Skip = "CI 环境文件系统隔离问题")]
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

    [Fact]
    public void ServiceConfig_AllowNetworkAccess_False_EffectiveHostIsLocalhost()
    {
        var sc = new ServiceConfig { AllowNetworkAccess = false };
        Assert.Equal("localhost", sc.EffectiveHost);
    }

    [Fact]
    public void ServiceConfig_AllowNetworkAccess_True_EffectiveHostIs0000()
    {
        var sc = new ServiceConfig { AllowNetworkAccess = true };
        Assert.Equal("0.0.0.0", sc.EffectiveHost);
    }

    [Fact]
    public void ServiceConfig_Default_AllowNetworkAccess_IsFalse()
    {
        var sc = new ServiceConfig();
        Assert.False(sc.AllowNetworkAccess);
        Assert.Equal("localhost", sc.EffectiveHost);
    }

    [Fact]
    public void ServiceConfig_AllowNetworkAccess_Serialization_RoundTrip()
    {
        // 验证 JSON 序列化/反序列化保留 AllowNetworkAccess（不依赖文件 I/O）
        var original = new ObsidianRagConfig();
        original.Service.AllowNetworkAccess = true;
        original.Service.Host = "0.0.0.0";

        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ObsidianRagConfig>(json);

        Assert.NotNull(restored);
        Assert.True(restored!.Service.AllowNetworkAccess);
        Assert.Equal("0.0.0.0", restored.Service.Host);
    }

    [Fact]
    public async Task ConcurrentSave_DoesNotCorruptFile()
    {
        var cm = CreateConfigManager();
        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
        {
            var config = new ObsidianRagConfig();
            config.DataPath = $"/data/{i}";
            cm.Save(config);
        }));

        await Task.WhenAll(tasks);

        var loaded = cm.Load();
        Assert.NotNull(loaded);
        Assert.StartsWith("/data/", loaded.DataPath);
    }

    [Fact]
    public void EmbeddingConfig_MultiBackendIsolatedProfiles_DoNotOverwriteEachOther()
    {
        var config = new ObsidianRagConfig();
        config.Embedding.LocalOnnx.ModelName = "bge-small-zh-v1.5";
        config.Embedding.LocalOnnx.ModelPath = "models/Embedding/bge-small-zh-v1.5/model.onnx";
        config.Embedding.OpenAiApi.ModelName = "text-embedding-3-small";
        config.Embedding.OpenAiApi.BaseUrl = "https://api.openai.com/v1";

        // Active mode: local ONNX
        config.Embedding.Mode = "onnx";
        Assert.Equal("bge-small-zh-v1.5", config.Embedding.ModelName);

        // Switch active mode to openai
        config.Embedding.Mode = "openai";
        Assert.Equal("text-embedding-3-small", config.Embedding.ModelName);
        Assert.Equal("https://api.openai.com/v1", config.Embedding.ApiBaseUrl);

        // Modifying API modelName does not affect localOnnx
        config.Embedding.ModelName = "bge-m3-remote";
        Assert.Equal("bge-m3-remote", config.Embedding.OpenAiApi.ModelName);
        Assert.Equal("bge-small-zh-v1.5", config.Embedding.LocalOnnx.ModelName);
    }

    [Fact]
    public void RerankConfig_MultiBackendIsolatedProfiles_DoNotOverwriteEachOther()
    {
        var config = new ObsidianRagConfig();
        config.Rerank.LocalOnnx.ModelName = "bge-reranker-base";
        config.Rerank.LocalOnnx.ModelPath = "models/Rerank/bge-reranker-base/model.onnx";
        config.Rerank.OpenAiApi.ModelName = "jina-reranker-v2";

        // Active mode: local
        config.Rerank.Mode = "onnx";
        Assert.Equal("bge-reranker-base", config.Rerank.ModelName);

        // Switch to api
        config.Rerank.Mode = "openai";
        Assert.Equal("jina-reranker-v2", config.Rerank.ModelName);

        // Updating API model does not corrupt local
        config.Rerank.ModelName = "cohere-rerank-v3";
        Assert.Equal("cohere-rerank-v3", config.Rerank.OpenAiApi.ModelName);
        Assert.Equal("bge-reranker-base", config.Rerank.LocalOnnx.ModelName);
    }

    [Fact]
    public void ChatModelConfig_Initialization_And_Properties()
    {
        var config = new ObsidianRagConfig();
        Assert.NotNull(config.Chat);
        Assert.Equal("OpenAiApi", config.Chat.ActiveBackend);
        Assert.NotNull(config.Chat.LocalGguf);
        Assert.NotNull(config.Chat.LocalOnnxGenAi);
        Assert.NotNull(config.Chat.OpenAiApi);
        Assert.Equal("Qwen2.5-7B-Instruct-GGUF", config.Chat.LocalGguf.ModelName);
    }

    public void Dispose()
    {
        try { Directory.SetCurrentDirectory(_originalDir); } catch { }
        try { Directory.Delete(_testDir, true); } catch { }
    }
}
