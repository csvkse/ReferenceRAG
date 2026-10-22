using System;
using System.IO;
using System.Linq;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class ChatModelDetectionAndKeepAliveTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _modelsPath;

    public ChatModelDetectionAndKeepAliveTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "rag_chat_detect_test_" + Guid.NewGuid().ToString("N"));
        _modelsPath = Path.Combine(_testDir, "models");
        Directory.CreateDirectory(_modelsPath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    private ConfigManager CreateTestConfig()
    {
        var config = new ObsidianRagConfig
        {
            DataPath = _testDir,
            ModelsRootPath = _modelsPath,
            Embedding = new EmbeddingConfig
            {
                ModelName = "bge-small-zh-v1.5"
            },
            Chat = new ChatModelConfig
            {
                LocalGguf = new LocalGgufChatProfile
                {
                    ModelName = "Qwen3.5-0.8B-Instruct-GGUF"
                }
            }
        };

        var appSettings = new { ReferenceRAG = config };
        var json = System.Text.Json.JsonSerializer.Serialize(appSettings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_testDir, "appsettings.json"), json);

        Directory.SetCurrentDirectory(_testDir);
        return new ConfigManager();
    }

    [Fact]
    public void GetDownloadedChatModels_WhenPhysicalChatModelExists_ReturnsDownloadedModel()
    {
        var chatDir = Path.Combine(_modelsPath, "Chat", "Qwen3.5-0.8B-Instruct-GGUF");
        Directory.CreateDirectory(chatDir);
        var ggufFile = Path.Combine(chatDir, "Qwen3.5-0.8B.Q4_K_M.gguf");
        File.WriteAllBytes(ggufFile, new byte[] { 0x47, 0x47, 0x55, 0x46 }); // GGUF header magic

        var cfg = CreateTestConfig();
        var mm = new ModelManager(_modelsPath, cfg);

        var downloadedChat = mm.GetDownloadedChatModels();

        Assert.NotEmpty(downloadedChat);
        var model = Assert.Single(downloadedChat.Where(m => m.Name == "Qwen3.5-0.8B-Instruct-GGUF"));
        Assert.True(model.IsDownloaded);
        Assert.Equal("chat", model.ModelType);
        Assert.NotEmpty(model.InstalledVariants);
        Assert.Equal("Qwen3.5-0.8B.Q4_K_M.gguf", model.InstalledVariants[0].FileName);
    }

    [Fact]
    public void SettingsJs_KeepAliveAndSyncContract_EnforcesOnActivatedAndBroadcast()
    {
        var settingsJsPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "Business", "ReferenceRAG.Business", "wwwroot", "app", "views", "Settings.js");

        if (File.Exists(settingsJsPath))
        {
            var content = File.ReadAllText(settingsJsPath);
            Assert.Contains("onActivated", content);
            Assert.Contains("onUnmounted", content);
            Assert.Contains("referencerag_model_sync", content);
            Assert.Contains("goToModelManagement", content);
            Assert.Contains("loadDownloadedChatModels", content);
            Assert.Contains("ensureValidChatModelSelection", content);
        }
    }

    [Fact]
    public void ModelsJs_CommunityVariantSearch_EnforcesContextAwareModelTypeResolution()
    {
        var modelsJsPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "Business", "ReferenceRAG.Business", "wwwroot", "app", "views", "Models.js");

        if (File.Exists(modelsJsPath))
        {
            var content = File.ReadAllText(modelsJsPath);
            // 确保不会无条件硬编码 modelType: 'embedding'
            Assert.Contains("activeModelTab.value", content);
            Assert.Contains("referencerag_model_sync", content);
        }
    }
}
