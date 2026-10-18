using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class ModelVariantLifecycleTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _modelsPath;

    public ModelVariantLifecycleTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "rag_variant_test_" + Guid.NewGuid().ToString("N"));
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

    private ConfigManager CreateTestConfig(string? embModelPath = null, string? rerankModelPath = null)
    {
        var config = new ObsidianRagConfig
        {
            DataPath = _testDir,
            Embedding = new EmbeddingConfig
            {
                ModelPath = embModelPath ?? string.Empty,
                ModelName = "bge-m3"
            },
            Rerank = new RerankConfig
            {
                ModelPath = rerankModelPath ?? string.Empty,
                ModelName = "bge-reranker-v2-m3"
            }
        };

        var appSettings = new { ReferenceRAG = config };
        var json = System.Text.Json.JsonSerializer.Serialize(appSettings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_testDir, "appsettings.json"), json);

        Directory.SetCurrentDirectory(_testDir);
        return new ConfigManager();
    }

    [Fact]
    public async Task ScanDirectoryVariants_DiscoversAllVariants_AndMarksActive()
    {
        // Arrange
        var modelDir = Path.Combine(_modelsPath, "bge-m3");
        Directory.CreateDirectory(modelDir);

        var q4File = Path.Combine(modelDir, "bge-m3-Q4_K_M.gguf");
        var q8File = Path.Combine(modelDir, "bge-m3-Q8_0.gguf");
        File.WriteAllBytes(q4File, new byte[1024]);
        File.WriteAllBytes(q8File, new byte[2048]);

        var cfg = CreateTestConfig(embModelPath: q4File);
        var manager = new ModelManager(_modelsPath, cfg);

        // Act
        var models = await manager.GetAvailableModelsAsync();
        var bgeM3 = models.FirstOrDefault(m => m.Name == "bge-m3");

        // Assert
        Assert.NotNull(bgeM3);
        Assert.True(bgeM3.IsDownloaded);
        Assert.Equal(2, bgeM3.InstalledVariants.Count);

        var q4Variant = bgeM3.InstalledVariants.FirstOrDefault(v => v.FileName == "bge-m3-Q4_K_M.gguf");
        var q8Variant = bgeM3.InstalledVariants.FirstOrDefault(v => v.FileName == "bge-m3-Q8_0.gguf");

        Assert.NotNull(q4Variant);
        Assert.NotNull(q8Variant);
        Assert.True(q4Variant.IsActive);
        Assert.False(q8Variant.IsActive);
        Assert.Equal("Q4_K_M", q4Variant.Quantization);
        Assert.Equal("Q8_0", q8Variant.Quantization);
        Assert.Equal("gguf", q4Variant.Format);
    }

    [Fact]
    public async Task SwitchModelVariantAsync_Embedding_SwitchesActiveVariant_AndUpdatesConfig()
    {
        // Arrange
        var modelDir = Path.Combine(_modelsPath, "bge-m3");
        Directory.CreateDirectory(modelDir);

        var q4File = Path.Combine(modelDir, "bge-m3-Q4_K_M.gguf");
        var q8File = Path.Combine(modelDir, "bge-m3-Q8_0.gguf");
        File.WriteAllBytes(q4File, new byte[1024]);
        File.WriteAllBytes(q8File, new byte[2048]);

        var cfg = CreateTestConfig(embModelPath: q4File);
        var manager = new ModelManager(_modelsPath, cfg);

        // Act: switch from Q4 to Q8
        var switched = await manager.SwitchModelVariantAsync("bge-m3", "bge-m3-Q8_0.gguf", isRerank: false);

        // Assert
        Assert.True(switched);

        var updatedCfg = cfg.Load();
        Assert.Equal(q8File, updatedCfg.Embedding.ModelPath);

        var bgeM3 = (await manager.GetAvailableModelsAsync()).First(m => m.Name == "bge-m3");
        var q4Variant = bgeM3.InstalledVariants.First(v => v.FileName == "bge-m3-Q4_K_M.gguf");
        var q8Variant = bgeM3.InstalledVariants.First(v => v.FileName == "bge-m3-Q8_0.gguf");

        Assert.False(q4Variant.IsActive);
        Assert.True(q8Variant.IsActive);
        Assert.Equal("bge-m3-Q8_0.gguf", bgeM3.ActiveVariantFileName);
    }

    [Fact]
    public async Task SwitchModelVariantAsync_Rerank_SwitchesActiveVariant_AndUpdatesConfig()
    {
        // Arrange
        var modelDir = Path.Combine(_modelsPath, "bge-reranker-v2-m3");
        Directory.CreateDirectory(modelDir);

        var q4File = Path.Combine(modelDir, "bge-reranker-v2-m3-Q4_K_M.gguf");
        var fp16File = Path.Combine(modelDir, "bge-reranker-v2-m3-f16.gguf");
        File.WriteAllBytes(q4File, new byte[1024]);
        File.WriteAllBytes(fp16File, new byte[4096]);

        var cfg = CreateTestConfig(rerankModelPath: q4File);
        var manager = new ModelManager(_modelsPath, cfg);

        // Act: switch from Q4 to f16
        var switched = await manager.SwitchModelVariantAsync("bge-reranker-v2-m3", "bge-reranker-v2-m3-f16.gguf", isRerank: true);

        // Assert
        Assert.True(switched);

        var updatedCfg = cfg.Load();
        Assert.Equal(fp16File, updatedCfg.Rerank.ModelPath);

        var rerankModel = manager.GetRerankModels().First(m => m.Name == "bge-reranker-v2-m3");
        var fp16Variant = rerankModel.InstalledVariants.First(v => v.FileName == "bge-reranker-v2-m3-f16.gguf");
        Assert.True(fp16Variant.IsActive);
    }

    [Fact]
    public async Task DeleteModelVariantAsync_InactiveVariant_RemovesFileAndKeepsActive()
    {
        // Arrange
        var modelDir = Path.Combine(_modelsPath, "bge-m3");
        Directory.CreateDirectory(modelDir);

        var q4File = Path.Combine(modelDir, "bge-m3-Q4_K_M.gguf");
        var q8File = Path.Combine(modelDir, "bge-m3-Q8_0.gguf");
        File.WriteAllBytes(q4File, new byte[1024]);
        File.WriteAllBytes(q8File, new byte[2048]);

        var cfg = CreateTestConfig(embModelPath: q4File);
        var manager = new ModelManager(_modelsPath, cfg);

        // Act: delete inactive Q8 variant
        var deleted = await manager.DeleteModelVariantAsync("bge-m3", "bge-m3-Q8_0.gguf", isRerank: false);

        // Assert
        Assert.True(deleted);
        Assert.False(File.Exists(q8File));
        Assert.True(File.Exists(q4File));

        var bgeM3 = (await manager.GetAvailableModelsAsync()).First(m => m.Name == "bge-m3");
        Assert.Single(bgeM3.InstalledVariants);
        Assert.True(bgeM3.InstalledVariants[0].IsActive);
        Assert.Equal("bge-m3-Q4_K_M.gguf", bgeM3.InstalledVariants[0].FileName);
    }

    [Fact]
    public async Task DeleteModelVariantAsync_ActiveVariant_FallsBackToRemainingVariant()
    {
        // Arrange
        var modelDir = Path.Combine(_modelsPath, "bge-m3");
        Directory.CreateDirectory(modelDir);

        var q4File = Path.Combine(modelDir, "bge-m3-Q4_K_M.gguf");
        var q8File = Path.Combine(modelDir, "bge-m3-Q8_0.gguf");
        File.WriteAllBytes(q4File, new byte[1024]);
        File.WriteAllBytes(q8File, new byte[2048]);

        var cfg = CreateTestConfig(embModelPath: q4File);
        var manager = new ModelManager(_modelsPath, cfg);

        // Act: delete active Q4 variant
        var deleted = await manager.DeleteModelVariantAsync("bge-m3", "bge-m3-Q4_K_M.gguf", isRerank: false);

        // Assert
        Assert.True(deleted);
        Assert.False(File.Exists(q4File));
        Assert.True(File.Exists(q8File));

        var bgeM3 = (await manager.GetAvailableModelsAsync()).First(m => m.Name == "bge-m3");
        Assert.Single(bgeM3.InstalledVariants);
        Assert.True(bgeM3.InstalledVariants[0].IsActive);
        Assert.Equal("bge-m3-Q8_0.gguf", bgeM3.InstalledVariants[0].FileName);

        var updatedCfg = cfg.Load();
        Assert.Equal(q8File, updatedCfg.Embedding.ModelPath);
    }
}
