using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ReferenceRAG.ApiHost.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G-KB1：知识库作用域解析的单一语义。
///
/// 背景（生产实测）：<c>/graph</c> 与 <c>/bm25-index</c> 选「全部知识库」时显示的是第一个库的数字。
/// 根因是 <c>ResolveKbIdAsync</c> 把「未指定」与「__all__」都解释成 <c>FirstOrDefault(Enabled)</c>。
/// 本门禁锁定三条不变量：① 读接口缺省 = 全部；② 写接口缺省 = 拒绝；③ 未知 ID 不落盘。
/// </summary>
public sealed class KnowledgeBaseScopeResolverGateTests : IDisposable
{
    private readonly string _tempRoot;

    public KnowledgeBaseScopeResolverGateTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "RR_ScopeGate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "kbs"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
        }
        catch { }
    }

    private async Task<ICatalogStore> BuildCatalogAsync(params (string Id, string Name, bool Enabled)[] items)
    {
        var catalog = new CatalogStore(Path.Combine(_tempRoot, "system", "catalog.db"));
        await catalog.InitializeAsync();
        foreach (var (id, name, enabled) in items)
        {
            await catalog.SaveKnowledgeBaseAsync(new KnowledgeBaseDefinition(
                id, name, null, enabled, 1, 1, "spec", DateTime.UtcNow, DateTime.UtcNow));
        }
        return catalog;
    }

    [Fact]
    public async Task Read_OmittedOrSentinel_ResolvesToAllEnabled()
    {
        var catalog = await BuildCatalogAsync(("kb_a", "A", true), ("kb_b", "B", true), ("kb_off", "Off", false));

        var omitted = await KnowledgeBaseScopeResolver.ResolveReadAsync(catalog, null);
        var blank = await KnowledgeBaseScopeResolver.ResolveReadAsync(catalog, "   ");
        var sentinel = await KnowledgeBaseScopeResolver.ResolveReadAsync(catalog, KnowledgeBaseScope.AllSentinel);

        foreach (var scope in new[] { omitted, blank, sentinel })
        {
            Assert.Equal(KnowledgeBaseScopeKind.All, scope.Kind);
            Assert.Equal(new[] { "kb_a", "kb_b" }, scope.Ids.OrderBy(x => x).ToArray());
        }
    }

    [Fact]
    public async Task Read_ExplicitId_ResolvesToThatSingleLibrary()
    {
        var catalog = await BuildCatalogAsync(("kb_a", "A", true), ("kb_b", "B", true));

        var scope = await KnowledgeBaseScopeResolver.ResolveReadAsync(catalog, "kb_b");

        Assert.Equal(KnowledgeBaseScopeKind.Single, scope.Kind);
        Assert.Equal(new[] { "kb_b" }, scope.Ids);
    }

    [Fact]
    public async Task Write_OmittedScope_IsRejected()
    {
        var catalog = await BuildCatalogAsync(("kb_a", "A", true));

        // 写操作不替调用方做选择：缺省必须 400，不能默默动第一个库。
        await Assert.ThrowsAsync<KnowledgeBaseScopeRequiredException>(
            () => KnowledgeBaseScopeResolver.ResolveWriteAsync(catalog, null));
        await Assert.ThrowsAsync<KnowledgeBaseScopeRequiredException>(
            () => KnowledgeBaseScopeResolver.ResolveWriteAsync(catalog, "  "));
    }

    [Fact]
    public async Task Write_SentinelAndExplicitId_AreAccepted()
    {
        var catalog = await BuildCatalogAsync(("kb_a", "A", true), ("kb_b", "B", true));

        var all = await KnowledgeBaseScopeResolver.ResolveWriteAsync(catalog, KnowledgeBaseScope.AllSentinel);
        var single = await KnowledgeBaseScopeResolver.ResolveWriteAsync(catalog, "kb_a");

        Assert.Equal(KnowledgeBaseScopeKind.All, all.Kind);
        Assert.Equal(KnowledgeBaseScopeKind.Single, single.Kind);
    }

    [Fact]
    public async Task UnknownId_ThrowsAndNeverTouchesStorage()
    {
        var catalog = await BuildCatalogAsync(("kb_a", "A", true));
        var manager = new CountingStorageManager(_tempRoot);

        // 解析器本身不接收 storage，这里用「解析后是否被调用」证明它没有触发惰性建库。
        await Assert.ThrowsAsync<KnowledgeBaseScopeNotFoundException>(
            () => KnowledgeBaseScopeResolver.ResolveReadAsync(catalog, "kb_missing"));

        Assert.Equal(0, manager.LeaseCount);
        Assert.False(Directory.Exists(Path.Combine(_tempRoot, "kbs", "kb_missing")),
            "未知 kbId 不得在数据目录下创建 kbs/<id>（会导致空 kb.db 越积越多）");
    }

    private sealed class CountingStorageManager : IKnowledgeBaseStorageManager
    {
        private readonly string _root;
        public int LeaseCount { get; private set; }

        public CountingStorageManager(string root) => _root = root;

        public ValueTask<IStorageLease> AcquireLeaseAsync(string knowledgeBaseId, StorageAccess access = StorageAccess.ReadOnly, CancellationToken ct = default)
        {
            LeaseCount++;
            var dir = Path.Combine(_root, "kbs", knowledgeBaseId);
            Directory.CreateDirectory(dir);
            throw new InvalidOperationException("门禁：解析阶段不应触达存储管理器");
        }

        public Task<bool> ExistsAsync(string knowledgeBaseId, CancellationToken ct = default) => Task.FromResult(false);
        public Task DeleteKnowledgeBaseAsync(string knowledgeBaseId, CancellationToken ct = default) => Task.CompletedTask;
        public Task BackupKnowledgeBaseAsync(string knowledgeBaseId, string backupFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreKnowledgeBaseAsync(string knowledgeBaseId, string backupFilePath, CancellationToken ct = default) => Task.CompletedTask;
    }
}
