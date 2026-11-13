using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// remaining-plan §3「补 EnsureConnectorAsync 单测」+ §1 P2 对账/事件/缓存的回归门禁。
/// 使用内存态 ICatalogStore，不落盘、不依赖容器。
/// </summary>
public class SourceLinkReconciliationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly CatalogHarness _catalog = new();
    private readonly Mock<IKnowledgeBaseStorageManager> _storageManager = new();

    public SourceLinkReconciliationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "ReferenceRAG_LinkTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { }
    }

    private KnowledgeBaseService CreateService(ObsidianRagConfig? config = null)
        => new(_catalog.Mock.Object, _storageManager.Object,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: new ConfigManager(config ?? new ObsidianRagConfig()));

    private static KnowledgeBaseDefinition NewKb(string id, string name = "KB")
        => new(id, name, null, true, 1, 1, "spec_lexical_only",
            DateTime.UtcNow, DateTime.UtcNow);

    private static ConnectorDefinition NewConnector(string kbId, string path, string? pattern = null)
        => new(Guid.NewGuid().ToString("N")[..16], kbId, "LocalFolder", path, pattern,
            1, true, DateTime.UtcNow, DateTime.UtcNow);

    // ==================== EnsureConnectorAsync ====================

    [Fact]
    public async Task EnsureConnectorAsync_SameKnowledgeBase_SecondCallIsIdempotent()
    {
        _catalog.Kbs["kb1"] = NewKb("kb1");
        var service = CreateService();

        var (first, created1) = await service.EnsureConnectorAsync("kb1", "LocalFolder", "/data/Notes");
        var (second, created2) = await service.EnsureConnectorAsync("kb1", "LocalFolder", @"\data\notes\");

        Assert.True(created1);
        Assert.False(created2);
        Assert.Equal(first.Id, second.Id);
        Assert.Single(_catalog.Connectors);
        Assert.Equal(1, _catalog.SaveCalls);
    }

    [Fact]
    public async Task EnsureConnectorAsync_ExistingConnector_OnlyBackfillsEmptyFilePattern()
    {
        _catalog.Kbs["kb1"] = NewKb("kb1");
        var seeded = NewConnector("kb1", "/data/Notes", pattern: null);
        _catalog.Connectors.Add(seeded);
        _catalog.SaveCalls = 0;
        var service = CreateService();

        var (patched, created) = await service.EnsureConnectorAsync("kb1", "LocalFolder", "/data/Notes", "*.md");
        Assert.False(created);
        Assert.Equal("*.md", patched.FilePattern);
        Assert.Equal(1, _catalog.SaveCalls);

        var (kept, createdAgain) = await service.EnsureConnectorAsync("kb1", "LocalFolder", "/data/Notes", "*.txt");
        Assert.False(createdAgain);
        Assert.Equal("*.md", kept.FilePattern); // 不覆盖用户已配置的规则
        Assert.Equal(1, _catalog.SaveCalls);
    }

    [Fact]
    public async Task EnsureConnectorAsync_CrossKnowledgeBase_CreatesIndependentConnectors()
    {
        _catalog.Kbs["kb1"] = NewKb("kb1", "KB-1");
        _catalog.Kbs["kb2"] = NewKb("kb2", "KB-2");
        var service = CreateService();

        var (a, createdA) = await service.EnsureConnectorAsync("kb1", "LocalFolder", "/data/Notes");
        var (b, createdB) = await service.EnsureConnectorAsync("kb2", "LocalFolder", "/data/Notes");

        Assert.True(createdA);
        Assert.True(createdB);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, _catalog.Connectors.Count);
    }

    [Fact]
    public async Task EnsureConnectorAsync_RacingUniqueConstraint_RequeriesAndReturnsExisting()
    {
        _catalog.Kbs["kb1"] = NewKb("kb1");
        var service = CreateService();
        var winner = NewConnector("kb1", "/data/Notes");
        _catalog.ThrowOnceOnSave = true;
        _catalog.OnBeforeSaveThrow = _ => _catalog.Connectors.Add(winner); // 模拟并发方先插入成功

        var (resolved, created) = await service.EnsureConnectorAsync("kb1", "LocalFolder", "/data/Notes");

        Assert.False(created);
        Assert.Equal(winner.Id, resolved.Id);
        Assert.Single(_catalog.Connectors);
    }

    [Fact]
    public async Task EnsureConnectorAsync_SaveFailsWithoutRace_PropagatesException()
    {
        _catalog.Kbs["kb1"] = NewKb("kb1");
        var service = CreateService();
        _catalog.ThrowOnceOnSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureConnectorAsync("kb1", "LocalFolder", "/data/Notes"));
        Assert.Empty(_catalog.Connectors);
    }

    [Fact]
    public async Task EnsureConnectorAsync_UnknownKnowledgeBase_Throws()
    {
        var service = CreateService();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnsureConnectorAsync("missing", "LocalFolder", "/data/Notes"));
    }

    // ==================== ReconcileAsync ====================

    [Fact]
    public async Task ReconcileAsync_ReportsOrphanedConnectorAndUnlinkedSource_WithoutMutating()
    {
        _catalog.Kbs["kb1"] = NewKb("kb1", "KB-1");
        var orphanPath = Path.Combine(_tempRoot, "no-such-parent", "deleted-notes");
        _catalog.Connectors.Add(NewConnector("kb1", orphanPath));

        var config = new ObsidianRagConfig();
        config.Sources.Add(new SourceFolder { Name = "leftover", Path = Path.Combine(_tempRoot, "leftover") });
        var service = CreateService(config);

        var report = await service.ReconcileAsync();

        Assert.True(report.ReportOnly);
        Assert.Equal(1, report.KnowledgeBaseCount);
        Assert.Equal(1, report.ConnectorCount);
        Assert.Equal(1, report.SourceCount);
        Assert.Equal(1, report.OrphanedCount);
        Assert.Equal(1, report.UnlinkedCount);
        Assert.Equal(0, report.DriftedCount);
        Assert.Single(_catalog.Connectors); // 只读：不删除连接器
        Assert.Equal(0, _catalog.DeleteCalls);
        Assert.Contains(report.Issues, i => i.Kind == SourceLinkDriftKind.Orphaned && i.ConnectorId != null);
        Assert.Contains(report.Issues, i => i.Kind == SourceLinkDriftKind.Unlinked && i.SourceName == "leftover");
    }

    [Fact]
    public async Task ReconcileAsync_DetectsRenamedDirectoryAsDriftedWithRebindCandidates()
    {
        _catalog.Kbs["kb1"] = NewKb("kb1", "KB-1");
        var parent = Path.Combine(_tempRoot, "vault");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(Path.Combine(parent, "foo2")); // 疑似改名后的目录
        var missing = Path.Combine(parent, "foo");               // 连接器仍指向旧名
        _catalog.Connectors.Add(NewConnector("kb1", missing));
        var service = CreateService();

        var report = await service.ReconcileAsync();

        Assert.Equal(1, report.DriftedCount);
        Assert.Equal(0, report.OrphanedCount);
        var issue = Assert.Single(report.Issues);
        Assert.NotNull(issue.RebindCandidates);
        Assert.Contains(Path.Combine(parent, "foo2"), issue.RebindCandidates);
    }

    [Fact]
    public async Task ReconcileAsync_AllPathsHealthy_ReturnsNoIssues()
    {
        _catalog.Kbs["kb1"] = NewKb("kb1", "KB-1");
        var live = Path.Combine(_tempRoot, "live");
        Directory.CreateDirectory(live);
        _catalog.Connectors.Add(NewConnector("kb1", live));
        var config = new ObsidianRagConfig();
        config.Sources.Add(new SourceFolder { Name = "live", Path = live });
        var service = CreateService(config);

        var report = await service.ReconcileAsync();

        Assert.Empty(report.Issues);
        Assert.Equal(0, report.OrphanedCount + report.UnlinkedCount + report.DriftedCount);
    }

    // ==================== RebindConnectorAsync ====================

    [Fact]
    public async Task RebindConnectorAsync_UpdatesPathButPreservesConnectorId()
    {
        _catalog.Kbs["kb1"] = NewKb("kb1");
        var oldPath = Path.Combine(_tempRoot, "old");
        var newPath = Path.Combine(_tempRoot, "new");
        Directory.CreateDirectory(newPath);
        var connector = NewConnector("kb1", oldPath);
        _catalog.Connectors.Add(connector);
        var service = CreateService();

        var rebound = await service.RebindConnectorAsync("kb1", connector.Id, newPath);

        Assert.Equal(connector.Id, rebound.Id);
        Assert.Equal(newPath, rebound.TargetPath);
        Assert.Single(_catalog.Connectors);
        Assert.Equal(newPath, _catalog.Connectors[0].TargetPath);
    }

    [Fact]
    public async Task RebindConnectorAsync_RejectsMissingDirectoryAndDuplicatePath()
    {
        _catalog.Kbs["kb1"] = NewKb("kb1");
        var connector = NewConnector("kb1", Path.Combine(_tempRoot, "old"));
        var occupied = Path.Combine(_tempRoot, "occupied");
        Directory.CreateDirectory(occupied);
        _catalog.Connectors.Add(connector);
        _catalog.Connectors.Add(NewConnector("kb1", occupied));
        var service = CreateService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RebindConnectorAsync("kb1", connector.Id, Path.Combine(_tempRoot, "not-exist")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RebindConnectorAsync("kb1", connector.Id, occupied));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.RebindConnectorAsync("kb1", "unknown-id", occupied));
    }

    // ==================== 事件 / 投影缓存 ====================

    [Fact]
    public async Task SourceLinkEventPublisher_InvalidatesProjectionCacheAndKeepsRecentEvents()
    {
        var cache = new SourceLinkProjectionCache();
        var handler = new SourceProjectionCacheInvalidationHandler(cache);
        var publisher = new SourceLinkEventPublisher(new ISourceLinkEventHandler[] { handler });
        var loadCalls = 0;

        Task<IReadOnlyList<ConnectorProjection>> Loader(CancellationToken _)
        {
            loadCalls++;
            return Task.FromResult<IReadOnlyList<ConnectorProjection>>(Array.Empty<ConnectorProjection>());
        }

        await cache.GetOrLoadAsync(Loader);
        await cache.GetOrLoadAsync(Loader);
        Assert.Equal(1, loadCalls); // 命中缓存

        publisher.Publish(new SourceLinkEvent { Kind = SourceLinkEventKind.SourceLinked, Path = "/a" });
        publisher.Publish(new SourceLinkEvent { Kind = SourceLinkEventKind.ConnectorDetached, Path = "/b" });

        await cache.GetOrLoadAsync(Loader);
        Assert.Equal(2, loadCalls); // 事件已失效缓存
        // 版本：首次加载 1 + 两次事件失效 2 + 重新加载 1 = 4
        Assert.Equal(4, cache.Version);

        var recent = publisher.Recent();
        Assert.Equal(2, recent.Count);
        Assert.Equal(SourceLinkEventKind.ConnectorDetached, recent[0].Kind); // 最新在前
    }

    [Fact]
    public void SourceLinkEventPublisher_HandlerExceptionDoesNotBlockOtherHandlers()
    {
        var cache = new SourceLinkProjectionCache();
        var throwing = new Mock<ISourceLinkEventHandler>();
        throwing.Setup(h => h.Handle(It.IsAny<SourceLinkEvent>())).Throws(new InvalidOperationException("boom"));
        var healthy = new SourceProjectionCacheInvalidationHandler(cache);
        var publisher = new SourceLinkEventPublisher(
            new[] { throwing.Object, healthy }, NullLogger<SourceLinkEventPublisher>.Instance);

        publisher.Publish(new SourceLinkEvent { Kind = SourceLinkEventKind.SourceRemoved, Path = "/x" });

        Assert.Equal(1, cache.Version);
        Assert.Single(publisher.Recent());
    }

    // ==================== 内存态 CatalogStore ====================

    private sealed class CatalogHarness
    {
        public Dictionary<string, KnowledgeBaseDefinition> Kbs { get; } = new();
        public List<ConnectorDefinition> Connectors { get; } = new();
        public int SaveCalls { get; set; }
        public int DeleteCalls { get; set; }
        public bool ThrowOnceOnSave { get; set; }
        public Action<ConnectorDefinition>? OnBeforeSaveThrow { get; set; }
        public Mock<ICatalogStore> Mock { get; }

        public CatalogHarness()
        {
            Mock = new Mock<ICatalogStore>();
            Mock.Setup(c => c.GetKnowledgeBaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => Kbs.TryGetValue(id, out var kb) ? kb : null);
            Mock.Setup(c => c.ListKnowledgeBasesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Kbs.Values.ToList());
            Mock.Setup(c => c.GetConnectorsByKnowledgeBaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string kbId, CancellationToken _) =>
                    Connectors.Where(c => c.KnowledgeBaseId == kbId).ToList());
            Mock.Setup(c => c.GetConnectorAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) => Connectors.FirstOrDefault(c => c.Id == id));
            Mock.Setup(c => c.SaveConnectorAsync(It.IsAny<ConnectorDefinition>(), It.IsAny<CancellationToken>()))
                .Returns((ConnectorDefinition connector, CancellationToken _) =>
                {
                    SaveCalls++;
                    if (ThrowOnceOnSave)
                    {
                        ThrowOnceOnSave = false;
                        OnBeforeSaveThrow?.Invoke(connector);
                        throw new InvalidOperationException("UNIQUE constraint failed: connectors.knowledge_base_id, connectors.path_key");
                    }
                    var index = Connectors.FindIndex(c => c.Id == connector.Id);
                    if (index >= 0) Connectors[index] = connector;
                    else Connectors.Add(connector);
                    return Task.FromResult(connector);
                });
            Mock.Setup(c => c.DeleteConnectorAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string id, CancellationToken _) =>
                {
                    DeleteCalls++;
                    return Connectors.RemoveAll(c => c.Id == id) > 0;
                });
        }
    }
}
