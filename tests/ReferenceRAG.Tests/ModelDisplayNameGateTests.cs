using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Helpers;
using ReferenceRAG.Core.Features.ModelManagement.Services;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.ModelManagement.Services;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// 模型展示名管理门禁（Model Display Name Gate）。
///
/// 锁定三条不变式：
/// 1. 用户自定义展示名持久化，且不被目录扫描/重启覆写；
/// 2. 展示名绝不参与身份比较——不改 EmbeddingSpec.ModelName、不影响配置漂移判定；
/// 3. 校验健全（空/超长/控制字符拒绝），孤儿条目可清理。
/// </summary>
public class ModelDisplayNameGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configPath;
    private readonly ConfigManager _configManager;

    public ModelDisplayNameGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rag_displayname_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "appsettings.json");
        _configManager = new ConfigManager(new ObsidianRagConfig
        {
            DataPath = _tempDir,
            ModelsRootPath = Path.Combine(_tempDir, "models")
        }, _configPath);
        _configManager.Save(_configManager.Load());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    // ─────────────────── 第一层：注册表基础语义 ───────────────────

    [Fact]
    public void SetAndResolve_PersistsAcrossInstances()
    {
        var urn = ModelUrn.ForLocal("bge-m3");
        var registry = new ModelDisplayNameRegistry(_configManager);

        Assert.True(registry.TrySet(urn, "主力向量模型", out var error), error);
        Assert.True(registry.HasOverride(urn));
        Assert.Equal("主力向量模型", registry.Resolve(urn, "BGE M3"));

        // 模拟重启：新实例从配置重建
        var reloaded = new ModelDisplayNameRegistry(_configManager);
        Assert.Equal("主力向量模型", reloaded.Resolve(urn, "BGE M3"));
        Assert.True(reloaded.HasOverride(urn));
    }

    [Fact]
    public void Resolve_PrefersOverride_ThenFallsBackToSystemDerived()
    {
        var urn = ModelUrn.ForLocal("bge-m3");
        var registry = new ModelDisplayNameRegistry(_configManager);

        // 无覆盖 → 回退系统推导名
        Assert.False(registry.HasOverride(urn));
        Assert.Equal("BGE M3", registry.Resolve(urn, "BGE M3"));
        Assert.Equal(string.Empty, registry.Resolve(urn, null));

        // 有覆盖 → 覆盖优先
        registry.TrySet(urn, "自定义名", out _);
        Assert.Equal("自定义名", registry.Resolve(urn, "BGE M3"));
    }

    [Fact]
    public void Reset_RestoresSystemDerivedName()
    {
        var urn = ModelUrn.ForLocal("bge-m3");
        var registry = new ModelDisplayNameRegistry(_configManager);

        registry.TrySet(urn, "临时名", out _);
        Assert.True(registry.Reset(urn));
        Assert.False(registry.HasOverride(urn));
        Assert.Equal("BGE M3", registry.Resolve(urn, "BGE M3"));

        // 重复重置返回 false（无覆盖可清）
        Assert.False(registry.Reset(urn));
    }

    [Fact]
    public void LocalAndProvider_ShareOneRegistry()
    {
        var registry = new ModelDisplayNameRegistry(_configManager);
        var local = ModelUrn.ForLocal("bge-m3");
        var online = ModelUrn.ForProvider("prov_sf", "BAAI/bge-m3");

        Assert.True(registry.TrySet(local, "本地主力", out _));
        Assert.True(registry.TrySet(online, "云端主力", out _));

        Assert.Equal("本地主力", registry.Resolve(local, "fallback"));
        Assert.Equal("云端主力", registry.Resolve(online, "fallback"));
        Assert.Equal(2, registry.Snapshot().Count);
    }

    // ─────────────────── 第二层：校验规则 ───────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TrySet_RejectsEmpty(string? value)
    {
        var registry = new ModelDisplayNameRegistry(_configManager);
        Assert.False(registry.TrySet(ModelUrn.ForLocal("m"), value, out var err));
        Assert.NotNull(err);
        Assert.False(registry.HasOverride(ModelUrn.ForLocal("m")));
    }

    [Fact]
    public void TrySet_RejectsTooLong()
    {
        var registry = new ModelDisplayNameRegistry(_configManager);
        var tooLong = new string('x', ModelDisplayNameRegistry.MaxDisplayNameLength + 1);
        Assert.False(registry.TrySet(ModelUrn.ForLocal("m"), tooLong, out var err));
        Assert.Contains("过长", err);
    }

    [Fact]
    public void TrySet_RejectsControlCharacters()
    {
        var registry = new ModelDisplayNameRegistry(_configManager);
        Assert.False(registry.TrySet(ModelUrn.ForLocal("m"), "bad\r\nname", out var err));
        Assert.Contains("控制字符", err);
        Assert.False(registry.TrySet(ModelUrn.ForLocal("m"), "tab\there", out _));
    }

    [Fact]
    public void TrySet_AcceptsMaxLengthBoundary()
    {
        var registry = new ModelDisplayNameRegistry(_configManager);
        var exact = new string('x', ModelDisplayNameRegistry.MaxDisplayNameLength);
        Assert.True(registry.TrySet(ModelUrn.ForLocal("m"), exact, out var err), err);
    }

    [Fact]
    public void TrySet_RejectsEmptyUrn()
    {
        var registry = new ModelDisplayNameRegistry(_configManager);
        Assert.False(registry.TrySet(ModelUrn.Empty, "name", out var err));
        Assert.NotNull(err);
    }

    // ─────────────────── 第三层：孤儿清理 ───────────────────

    [Fact]
    public void PruneOrphans_RemovesOnlyMissingModels()
    {
        var registry = new ModelDisplayNameRegistry(_configManager);
        registry.TrySet(ModelUrn.ForLocal("exists"), "存在的", out _);
        registry.TrySet(ModelUrn.ForLocal("gone"), "已删除的", out _);
        registry.TrySet(ModelUrn.ForProvider("prov_sf", "BAAI/bge-m3"), "在线的", out _);

        var removed = registry.PruneOrphans(urn =>
            urn.ToString() != "local:gone");

        Assert.Equal(1, removed);
        Assert.True(registry.HasOverride(ModelUrn.ForLocal("exists")));
        Assert.True(registry.HasOverride(ModelUrn.ForProvider("prov_sf", "BAAI/bge-m3")));
        Assert.False(registry.HasOverride(ModelUrn.ForLocal("gone")));
    }

    [Fact]
    public void PruneOrphans_NoOpWhenNothingOrphaned()
    {
        var registry = new ModelDisplayNameRegistry(_configManager);
        registry.TrySet(ModelUrn.ForLocal("m"), "名", out _);
        Assert.Equal(0, registry.PruneOrphans(_ => true));
    }

    [Fact]
    public void Snapshot_ToleratesCorruptEntries()
    {
        // 手工写入含空键/空值的损坏配置，验证解析容错
        var config = _configManager.Load();
        config.ModelDisplayNames = new Dictionary<string, string>
        {
            ["local:ok"] = "正常",
            [""] = "空键",
            ["local:empty-value"] = "   "
        };
        _configManager.Save(config);

        var registry = new ModelDisplayNameRegistry(_configManager);
        var snap = registry.Snapshot();
        Assert.Single(snap);
        Assert.Equal("正常", snap["local:ok"]);
    }

    // ─────────────── 第四层：与 ModelManager 集成 ───────────────

    [Fact]
    public void ModelManager_OverrideSurvivesRefreshLocalModels()
    {
        var modelsPath = Path.Combine(_tempDir, "models");
        Directory.CreateDirectory(modelsPath);
        using var manager = new ModelManager(modelsPath, _configManager);

        var urn = ModelUrn.ForLocal("bge-m3");
        Assert.True(manager.DisplayNames.TrySet(urn, "重启后仍生效", out _));

        // 扫描重建 registry（历史上会用目录名覆写 DisplayName）
        manager.RefreshLocalModels();

        var model = manager.GetEmbeddingModels().FirstOrDefault(m => m.Name == "bge-m3");
        Assert.NotNull(model);
        Assert.Equal("重启后仍生效", model.DisplayName);
    }

    [Fact]
    public void ModelManager_ResetRestoresPredefinedDisplayName()
    {
        var modelsPath = Path.Combine(_tempDir, "models");
        Directory.CreateDirectory(modelsPath);
        using var manager = new ModelManager(modelsPath, _configManager);

        var urn = ModelUrn.ForLocal("bge-m3");
        manager.DisplayNames.TrySet(urn, "覆盖名", out _);
        manager.RefreshLocalModels();
        Assert.Equal("覆盖名", manager.GetEmbeddingModels().First(m => m.Name == "bge-m3").DisplayName);

        Assert.True(manager.DisplayNames.Reset(urn));
        manager.RefreshLocalModels();
        // 回退到预定义模型的 DisplayName（硬编码 "BGE M3"）
        Assert.Equal("BGE M3", manager.GetEmbeddingModels().First(m => m.Name == "bge-m3").DisplayName);
    }

    [Fact]
    public void ModelManager_DeleteModel_CleansUpOverride()
    {
        var modelsPath = Path.Combine(_tempDir, "models");
        var modelDir = Path.Combine(modelsPath, "Embedding", "bge-m3");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "bge-m3-Q4_K_M.gguf"), "dummy-weights");

        using var manager = new ModelManager(modelsPath, _configManager);
        var urn = ModelUrn.ForLocal("bge-m3");
        manager.DisplayNames.TrySet(urn, "待删除的", out _);
        manager.RefreshLocalModels();

        Assert.True(manager.DisplayNames.HasOverride(urn));

        // 删除模型后覆盖条目应被清理（避免配置堆积）
        var deleted = manager.DeleteModelAsync("bge-m3").GetAwaiter().GetResult();
        if (deleted)
        {
            Assert.False(manager.DisplayNames.HasOverride(urn));
        }
    }

    // ─────────────── 第五层：身份隔离回归防线 ───────────────

    [Fact]
    public async Task DisplayName_NeverAltersEmbeddingSpecModelName()
    {
        // 回归防线：设展示名不得渗入身份字段。
        // 造一个含 GGUF 权重文件的本地模型目录，使扫描识别为「已下载」，
        // 从而进入 KnowledgeBaseService.ListEmbeddingSpecsAsync 的 local_* 生成分支。
        var modelsPath = Path.Combine(_tempDir, "models");
        var modelDir = Path.Combine(modelsPath, "Embedding", "bge-m3");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "bge-m3-Q4_K_M.gguf"), "dummy-weights");

        using var manager = new ModelManager(modelsPath, _configManager);
        manager.DisplayNames.TrySet(ModelUrn.ForLocal("bge-m3"), "我改了的名字", out _);
        manager.RefreshLocalModels();

        // 界面层确实看到自定义名
        var model = manager.GetEmbeddingModels().FirstOrDefault(m => m.Name == "bge-m3");
        Assert.NotNull(model);
        Assert.Equal("我改了的名字", model.DisplayName);

        // 但 Spec 生成的身份键必须仍是规范 ID
        var service = new ReferenceRAG.Business.Features.KnowledgeBase.Services.KnowledgeBaseService(
            catalog: new CatalogStore(Path.Combine(_tempDir, "catalog.db")),
            storageManager: new KnowledgeBaseStorageManager(Path.Combine(_tempDir, "kbdata")),
            logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<ReferenceRAG.Business.Features.KnowledgeBase.Services.KnowledgeBaseService>.Instance,
            configManager: _configManager,
            modelManager: manager);

        var specs = await service.ListEmbeddingSpecsAsync();
        var spec = specs.FirstOrDefault(s => s.Id == "local_bge-m3");
        Assert.NotNull(spec);
        Assert.Equal("bge-m3", spec.ModelName);
        Assert.NotEqual("我改了的名字", spec.ModelName);
    }

    [Fact]
    public void DisplayName_DoesNotAffectModelIdentityComparison()
    {
        // 回归防线：展示名变更不得让「不同模型」被判为相同，也不得让「相同模型」被判为不同
        var before = ModelIdentity.IsSameModel("bge-m3", "BGE M3");
        var registry = new ModelDisplayNameRegistry(_configManager);
        registry.TrySet(ModelUrn.ForLocal("bge-m3"), "随便什么名字", out _);

        Assert.Equal(before, ModelIdentity.IsSameModel("bge-m3", "BGE M3"));
        Assert.True(ModelIdentity.IsSameModel("bge-m3", "bge-m3-q4_k_m"));
        Assert.False(ModelIdentity.IsSameModel("bge-m3", "qwen-embedding-v2"));
    }
}
