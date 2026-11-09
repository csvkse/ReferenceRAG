using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using ReferenceRAG.Core.Features.ModelManagement.Helpers;
using ReferenceRAG.Core.Features.ModelManagement.Services;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;

namespace ReferenceRAG.Tests;

public class SettingsUnifiedModelIdAssociationTests
{
    [Fact]
    public void ModelUrn_ParseAndRoundtrip_LocalAndProvider()
    {
        // 1. Local URN
        var localUrn = ModelUrn.ForLocal("bge-small-zh-v1.5");
        Assert.True(localUrn.IsLocal);
        Assert.False(localUrn.IsProvider);
        Assert.Equal("local:bge-small-zh-v1.5", localUrn.ToString());

        var parsedLocal = ModelUrn.Parse("local:bge-small-zh-v1.5");
        Assert.Equal(localUrn, parsedLocal);
        Assert.Equal("bge-small-zh-v1.5", parsedLocal.ModelName);

        // 2. Provider URN
        var provUrn = ModelUrn.ForProvider("prov_silicon", "deepseek-ai/DeepSeek-V3");
        Assert.False(provUrn.IsLocal);
        Assert.True(provUrn.IsProvider);
        Assert.Equal("prov_silicon", provUrn.ProviderId);
        Assert.Equal("deepseek-ai/DeepSeek-V3", provUrn.ModelId);
        Assert.Equal("provider:prov_silicon:deepseek-ai/DeepSeek-V3", provUrn.ToString());

        var parsedProv = ModelUrn.Parse("provider:prov_silicon:deepseek-ai/DeepSeek-V3");
        Assert.Equal(provUrn, parsedProv);

        // 3. Fallback and Edge Cases
        Assert.Equal(ModelUrn.Empty, ModelUrn.Parse(null));
        Assert.Equal(ModelUrn.Empty, ModelUrn.Parse("   "));

        // Naked string fallback to local
        var naked = ModelUrn.Parse("qwen2.5-7b-instruct");
        Assert.True(naked.IsLocal);
        Assert.Equal("qwen2.5-7b-instruct", naked.ModelName);
    }

    [Fact]
    public void ModelAssetResolver_CatalogAndResolution_WorksCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "referencerag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var configPath = Path.Combine(tempDir, "config.json");
            var configManager = new ConfigManager(customConfigPath: configPath);
            var initialConfig = configManager.Load();

            // Setup a mock provider
            initialConfig.ModelProviders = new List<ModelProviderConfig>
            {
                new ModelProviderConfig
                {
                    Id = "test_provider",
                    Name = "测试在线服务商",
                    BaseUrl = "https://api.testprovider.com/v1",
                    ApiKey = "sk-test-key-123456",
                    Enabled = true,
                    Models = new List<RegisteredModelAsset>
                    {
                        new RegisteredModelAsset { ModelId = "test-chat-model", DisplayName = "测试对话模型", Enabled = true, ModelType = "chat" },
                        new RegisteredModelAsset { ModelId = "test-emb-model", DisplayName = "测试嵌入模型", Enabled = true, ModelType = "embedding", Dimension = 1024 },
                        new RegisteredModelAsset { ModelId = "test-disabled-model", DisplayName = "停用模型", Enabled = false, ModelType = "chat" }
                    }
                }
            };
            configManager.Save(initialConfig);

            var resolver = new ModelAssetResolver(configManager, null);

            // 1. Catalog for chat
            var chatCatalog = resolver.GetCatalog("chat");
            Assert.NotNull(chatCatalog);
            Assert.Contains(chatCatalog.Groups, g => g.Label.Contains("在线服务商"));
            var onlineGroup = chatCatalog.Groups.Find(g => g.Label.Contains("在线服务商"));
            Assert.NotNull(onlineGroup);
            Assert.Contains(onlineGroup.Options, opt => opt.Value == "provider:test_provider:test-chat-model");
            // Disabled model should appear as disabled in catalog
            var disabledOpt = onlineGroup.Options.Find(opt => opt.Value == "provider:test_provider:test-disabled-model");
            Assert.NotNull(disabledOpt);
            Assert.True(disabledOpt.Disabled);

            // 2. Resolve provider model
            var resolved = resolver.Resolve("provider:test_provider:test-emb-model", "embedding");
            Assert.NotNull(resolved);
            Assert.False(resolved.IsLocal);
            Assert.True(resolved.IsReady);
            Assert.Equal("https://api.testprovider.com/v1", resolved.BaseUrl);
            Assert.Equal("sk-test-key-123456", resolved.ApiKey);
            Assert.Equal("test-emb-model", resolved.RemoteModelId);
            Assert.Equal(1024, resolved.Dimension);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}
