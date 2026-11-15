using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Services;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// 模型身份规范化门禁（Model Identity Normalization Gate）。
///
/// 背景：生产中「从旧版全局数据源自动收敛迁移的默认知识库」在模型已对齐的情况下
/// 仍恒显示「偏离(建议重排)」。根因是 EmbeddingSpec.ModelName 曾存入展示名
/// （ModelInfo.DisplayName = "BGE M3"），而全局配置存规范 ID（"bge-m3"），
/// 漂移检测直接字符串比较导致同一模型被误判。
///
/// 本门禁锁定两层防线：
///   1. 上游：local_* / prov_* 规格生成必须使用规范 ID（Name / ModelId）；
///   2. 下游：漂移检测必须走 ModelIdentity 规范化比较，对历史展示名数据免疫。
/// </summary>
public class ModelIdentityDriftGateTests
{
    // ─────────────────────────── 第一层：规范化器语义 ───────────────────────────

    [Theory]
    // 展示名 vs 规范 ID（生产实际案例）
    [InlineData("BGE M3", "bge-m3")]
    [InlineData("bge-m3", "BGE M3")]
    [InlineData("BGE-M3", "bge_m3")]
    [InlineData("bge.m3", "BGEM3")]
    // 量化/微调版本后缀不改变身份主干
    [InlineData("bge-m3", "bge-m3-q4_k_m")]
    [InlineData("bge-large-zh-v1.5", "bge-large-zh")]
    // 厂商前缀（"Provider / Model" 形式）剥离后仅比较模型本体
    [InlineData("SiliconFlow / BGE M3", "bge-m3")]
    [InlineData("OpenAI / text-embedding-3-small", "text-embedding-3-small")]
    // 多级厂商路径 + 尾部维度括注（dev 环境实测形态：Spec.ModelName 混存全路径与维度）
    [InlineData("SiliconFlow / BAAI / BGE-M3 (1024d)", "BAAI/bge-m3")]
    [InlineData("BAAI/bge-m3", "bge-m3")]
    [InlineData("Qwen/Qwen3-Embedding-4B", "Qwen3-Embedding-4B")]
    [InlineData("Pro/BAAI/bge-m3", "BAAI/bge-m3")]
    [InlineData("bge-m3 (1024d)", "bge-m3")]
    [InlineData("bge-m3 (1024 d)", "bge-m3")]
    // 大小写与空白无关
    [InlineData("  BGE   M3  ", "bge-m3")]
    public void ModelIdentity_DisplayNameAndCanonicalId_ShouldBeSameModel(string left, string right)
    {
        Assert.True(ModelIdentity.IsSameModel(left, right), $"'{left}' 与 '{right}' 应判定为同一模型");
    }

    [Theory]
    [InlineData("bge-m3", "bge-large-zh-v1.5")]
    [InlineData("bge-m3", "qwen-embedding-v2")]
    [InlineData("text-embedding-3-small", "text-embedding-3-large")]
    [InlineData("all-MiniLM-L6-v2", "bge-small-zh-v1.5")]
    // 尺寸档次（small/large）是不同模型，不得被后缀剥离规则误判为同族
    [InlineData("text-embedding-3-small", "text-embedding-3")]
    public void ModelIdentity_DifferentModels_ShouldNotBeSameModel(string left, string right)
    {
        Assert.False(ModelIdentity.IsSameModel(left, right), $"'{left}' 与 '{right}' 不应判定为同一模型");
    }

    [Fact]
    public void ModelIdentity_EmptyOrWhitespace_ShouldNotMatchAnything()
    {
        // 空值语义：无法判定即不判等，交由调用方处理（避免空值把任意模型误判为对齐）
        Assert.False(ModelIdentity.IsSameModel(null, "bge-m3"));
        Assert.False(ModelIdentity.IsSameModel("bge-m3", null));
        Assert.False(ModelIdentity.IsSameModel("", "bge-m3"));
        Assert.False(ModelIdentity.IsSameModel("   ", "bge-m3"));
        Assert.False(ModelIdentity.IsSameModel(null, null));
    }

    [Fact]
    public void ModelIdentity_NormalizeKey_ShouldStripNonAlphanumerics()
    {
        Assert.Equal("bgem3", ModelIdentity.NormalizeKey("BGE M3"));
        Assert.Equal("bgem3", ModelIdentity.NormalizeKey("bge-m3"));
        Assert.Equal("bgem3", ModelIdentity.NormalizeKey("  bge_m3  "));
        Assert.Equal(string.Empty, ModelIdentity.NormalizeKey(null));
        Assert.Equal(string.Empty, ModelIdentity.NormalizeKey("   "));
    }

    // ─────────────────── 第二层：规格生成必须用规范 ID ───────────────────

    [Fact]
    public async Task LocalEmbeddingSpec_ShouldUseCanonicalModelId_NotDisplayName()
    {
        // 复现生产场景：ModelManager 提供 Name="bge-m3" 与 DisplayName="BGE M3"
        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.GetDownloadedModels()).Returns(new List<ModelInfo>
        {
            new ModelInfo
            {
                Name = "bge-m3",
                DisplayName = "BGE M3",
                ModelType = "embedding",
                IsDownloaded = true,
                PhysicalExists = true,
                IsEnabled = true,
                Dimension = 1024,
                MaxSequenceLength = 8192
            }
        });

        var service = CreateService(modelManager.Object);
        var specs = await service.ListEmbeddingSpecsAsync();

        var localSpec = specs.FirstOrDefault(s => s.Id == "local_bge-m3");
        Assert.NotNull(localSpec);
        // 身份键必须是规范 ID；存入展示名会使漂移检测恒不相等
        Assert.Equal("bge-m3", localSpec.ModelName);
        Assert.NotEqual("BGE M3", localSpec.ModelName);
    }

    [Fact]
    public async Task ProviderEmbeddingSpec_ShouldUseCanonicalModelId_NotProviderQualifiedDisplayName()
    {
        var modelManager = new Mock<IModelManager>();
        modelManager.Setup(m => m.GetDownloadedModels()).Returns(new List<ModelInfo>());

        var configManager = new ConfigManager(new ObsidianRagConfig
        {
            Embedding = new EmbeddingConfig { ModelName = "bge-m3" },
            ModelProviders = new List<ModelProviderConfig>
            {
                new()
                {
                    Id = "sf",
                    Name = "SiliconFlow",
                    Enabled = true,
                    Models = new List<RegisteredModelAsset>
                    {
                        new()
                        {
                            ModelId = "BAAI/bge-m3",
                            DisplayName = "BGE M3",
                            ModelType = "embedding",
                            Enabled = true,
                            Dimension = 1024,
                            MaxTokens = 8192
                        }
                    }
                }
            }
        });

        var service = CreateService(modelManager.Object, configManager);
        var specs = await service.ListEmbeddingSpecsAsync();

        var provSpec = specs.FirstOrDefault(s => s.Id == "prov_sf_BAAI/bge-m3");
        Assert.NotNull(provSpec);
        Assert.Equal("BAAI/bge-m3", provSpec.ModelName);
        Assert.DoesNotContain("SiliconFlow", provSpec.ModelName);
    }

    // ─────────────── 第三层：漂移检测对展示名历史数据免疫 ───────────────

    [Fact]
    public async Task ConfigDrift_HistoricalDisplayNameSpec_ShouldReportAligned_NotDrifted()
    {
        // 模拟生产库中已固化展示名的历史规格（DB 迁移前的存量数据）
        var dbPath = Path.Combine(Path.GetTempPath(), "drift_gate_" + System.Guid.NewGuid().ToString("N") + ".db");
        var catalog = new CatalogStore(dbPath);
        try
        {
            await catalog.SaveEmbeddingSpecAsync(new EmbeddingSpec(
                Id: "local_bge-m3",
                ModelName: "BGE M3", // 历史展示名
                Dimension: 1024,
                Metric: "cosine",
                MaxTokens: 8192,
                SearchBackend: "LocalOnnx"));

            await catalog.SaveChunkingSpecAsync(new ChunkingSpec(
                Id: "spec_chunk_lexical_dense",
                StrategyType: "Balanced",
                MaxTokens: 512,
                MinTokens: 50,
                OverlapTokens: 50));

            await catalog.SaveKnowledgeBaseAsync(new KnowledgeBaseDefinition(
                Id: "kb-drift-test",
                Name: "DriftTest",
                Description: "hist",
                Enabled: true,
                DesiredConfigRevision: 1,
                EffectiveConfigRevision: 1,
                EmbeddingSpecId: "local_bge-m3",
                ChunkingSpecId: "spec_chunk_lexical_dense",
                CreatedAt: System.DateTime.UtcNow,
                UpdatedAt: System.DateTime.UtcNow));

            var configManager = new ConfigManager(new ObsidianRagConfig
            {
                Embedding = new EmbeddingConfig { ModelName = "bge-m3" }, // 全局为规范 ID
                Chunking = new ChunkingConfig { AdaptiveStrategyType = "Balanced" }
            });

            var service = CreateService(null, configManager, catalog);
            var drift = await service.CheckConfigDriftAsync("kb-drift-test");

            // 核心断言：同一模型的展示名/规范 ID 不得误报为偏离
            Assert.Equal(DriftStatus.Aligned, drift.Status);
            Assert.False(drift.CanUpgrade);
        }
        finally
        {
            try { System.IO.File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task ConfigDrift_GenuinelyDifferentModel_ShouldStillReportDrifted()
    {
        // 反向断言：规范化不得把「不同模型」误判为对齐（防止修复过度）
        var dbPath = Path.Combine(Path.GetTempPath(), "drift_gate2_" + System.Guid.NewGuid().ToString("N") + ".db");
        var catalog = new CatalogStore(dbPath);
        try
        {
            await catalog.SaveEmbeddingSpecAsync(new EmbeddingSpec(
                Id: "local_bge-m3",
                ModelName: "BGE M3",
                Dimension: 1024,
                Metric: "cosine",
                MaxTokens: 8192,
                SearchBackend: "LocalOnnx"));

            await catalog.SaveChunkingSpecAsync(new ChunkingSpec(
                Id: "spec_chunk_lexical_dense",
                StrategyType: "Balanced",
                MaxTokens: 512,
                MinTokens: 50,
                OverlapTokens: 50));

            await catalog.SaveKnowledgeBaseAsync(new KnowledgeBaseDefinition(
                Id: "kb-drift-test2",
                Name: "DriftTest2",
                Description: "hist",
                Enabled: true,
                DesiredConfigRevision: 1,
                EffectiveConfigRevision: 1,
                EmbeddingSpecId: "local_bge-m3",
                ChunkingSpecId: "spec_chunk_lexical_dense",
                CreatedAt: System.DateTime.UtcNow,
                UpdatedAt: System.DateTime.UtcNow));

            var configManager = new ConfigManager(new ObsidianRagConfig
            {
                Embedding = new EmbeddingConfig { ModelName = "qwen-embedding-v2" },
                Chunking = new ChunkingConfig { AdaptiveStrategyType = "Balanced" }
            });

            var service = CreateService(null, configManager, catalog);
            var drift = await service.CheckConfigDriftAsync("kb-drift-test2");

            Assert.Equal(DriftStatus.Drifted, drift.Status);
            Assert.True(drift.CanUpgrade);
        }
        finally
        {
            try { System.IO.File.Delete(dbPath); } catch { }
        }
    }

    // ─────────────────────────── 测试辅助 ───────────────────────────

    private static KnowledgeBaseService CreateService(
        IModelManager? modelManager = null,
        ConfigManager? configManager = null,
        ICatalogStore? catalog = null)
    {
        catalog ??= new CatalogStore(Path.Combine(Path.GetTempPath(), "svc_" + System.Guid.NewGuid().ToString("N") + ".db"));
        return new KnowledgeBaseService(
            catalog: catalog,
            storageManager: new KnowledgeBaseStorageManager(Path.Combine(Path.GetTempPath(), "svcdata_" + System.Guid.NewGuid().ToString("N"))),
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: configManager,
            modelManager: modelManager);
    }
}
