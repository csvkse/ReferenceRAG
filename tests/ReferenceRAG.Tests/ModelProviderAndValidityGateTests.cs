using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class ModelProviderAndValidityGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly ConfigManager _configManager;

    public ModelProviderAndValidityGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rag_model_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "appsettings.json");

        var config = new ObsidianRagConfig
        {
            DataPath = _tempDir,
            ModelsRootPath = Path.Combine(_tempDir, "models"),
            ModelProviders = new List<ModelProviderConfig>
            {
                new ModelProviderConfig
                {
                    Id = "prov_sf",
                    Name = "SiliconFlow",
                    BaseUrl = "https://api.siliconflow.cn/v1",
                    ApiKey = "sk-test-secret-key",
                    Enabled = true,
                    Models = new List<RegisteredModelAsset>
                    {
                        new RegisteredModelAsset
                        {
                            ModelId = "BAAI/bge-m3",
                            DisplayName = "BAAI/bge-m3",
                            ModelType = "embedding",
                            Dimension = 1024,
                            Enabled = true
                        },
                        new RegisteredModelAsset
                        {
                            ModelId = "disabled-model",
                            DisplayName = "Disabled Model",
                            ModelType = "embedding",
                            Dimension = 768,
                            Enabled = false
                        }
                    }
                },
                new ModelProviderConfig
                {
                    Id = "prov_disabled",
                    Name = "Disabled Provider",
                    BaseUrl = "https://api.disabled.com/v1",
                    ApiKey = "sk-disabled",
                    Enabled = false,
                    Models = new List<RegisteredModelAsset>
                    {
                        new RegisteredModelAsset
                        {
                            ModelId = "offline-model",
                            DisplayName = "Offline Model",
                            ModelType = "embedding",
                            Dimension = 1536,
                            Enabled = true
                        }
                    }
                }
            }
        };

        var root = new { ReferenceRAG = config };
        File.WriteAllText(_configPath, System.Text.Json.JsonSerializer.Serialize(root));
        _configManager = new ConfigManager(customConfigPath: _configPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void ModelInfo_PhysicalGate_And_IsEnabled_Cloning()
    {
        var model = new ModelInfo
        {
            Name = "bge-small-zh-v1.5",
            DisplayName = "BGE Small",
            Dimension = 512,
            IsEnabled = false,
            PhysicalExists = true,
            PhysicalStatus = "ready"
        };

        var clone = model.Clone();

        Assert.Equal(model.Name, clone.Name);
        Assert.False(clone.IsEnabled);
        Assert.True(clone.PhysicalExists);
        Assert.Equal("ready", clone.PhysicalStatus);
    }

    [Fact]
    public void ModelManager_ToggleModelEnabled_PersistsToConfig()
    {
        var modelsPath = Path.Combine(_tempDir, "models");
        Directory.CreateDirectory(modelsPath);

        using var manager = new ModelManager(modelsPath, _configManager);

        // Initially enabled
        var state1 = manager.ToggleModelEnabled("custom-test-model", false);
        Assert.False(state1);

        var cfg = _configManager.Load();
        Assert.Contains("custom-test-model", cfg.DisabledModels);

        // Toggle back to enabled
        var state2 = manager.ToggleModelEnabled("custom-test-model", true);
        Assert.True(state2);

        var cfg2 = _configManager.Load();
        Assert.DoesNotContain("custom-test-model", cfg2.DisabledModels);
    }

    [Fact]
    public async Task KnowledgeBaseService_ListEmbeddingSpecs_AppliesGateAndAggregatesProviders()
    {
        var catalogDb = Path.Combine(_tempDir, "catalog.db");
        using var catalog = new CatalogStore(catalogDb, NullLogger<CatalogStore>.Instance);
        await catalog.InitializeAsync();

        var storageManager = new KnowledgeBaseStorageManager(_tempDir);
        var modelsPath = Path.Combine(_tempDir, "models");
        Directory.CreateDirectory(modelsPath);
        using var modelManager = new ModelManager(modelsPath, _configManager);

        var kbService = new KnowledgeBaseService(
            catalog,
            storageManager,
            modelRouter: null,
            logger: null,
            loggerFactory: null,
            configManager: _configManager,
            modelManager: modelManager
        );

        var specs = await kbService.ListEmbeddingSpecsAsync();

        // 1. 系统默认模板必须存在
        Assert.Contains(specs, s => s.Id == "default-spec");

        // 2. 线上供应商启用的模型必须被聚合进入
        var sfSpec = specs.FirstOrDefault(s => s.Id == "prov_prov_sf_BAAI/bge-m3");
        Assert.NotNull(sfSpec);
        Assert.Equal(1024, sfSpec.Dimension);
        Assert.Equal("OpenAiApi", sfSpec.IngestionBackend);
        Assert.Equal("https://api.siliconflow.cn/v1", sfSpec.IngestionBaseUrl);

        // 3. 停用的模型绝对不应该出现 (Disabled Model Gate)
        Assert.DoesNotContain(specs, s => s.Id.Contains("disabled-model"));

        // 4. 停用的服务商绝对不应该出现 (Disabled Provider Gate)
        Assert.DoesNotContain(specs, s => s.Id.Contains("prov_disabled"));

        // 5. 历史幽灵预设必须被安全过滤
        Assert.DoesNotContain(specs, s => s.Id == "bge-m3-default");
        Assert.DoesNotContain(specs, s => s.Id == "openai-small-default");
        Assert.DoesNotContain(specs, s => s.Id == "minilm-default");
    }
}
