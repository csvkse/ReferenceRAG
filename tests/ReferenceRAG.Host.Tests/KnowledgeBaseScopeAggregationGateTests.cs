using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G-KB2 / G-KB3 / G-KB4：作用域聚合与写操作显式作用域。
///
/// 背景（生产实测）：
/// - <c>/api/graph/stats?kbId=__all__</c> 与不传参都返回第一个库的数字（17196），两库求和是 17767；
/// - <c>/api/bm25index/summary</c> 的 <c>__all__</c> 用「平均的平均」得到 693.44，按分块加权应为 539.64；
/// - 写操作（重建/清空/建索引）缺省时静默作用到第一个库。
/// </summary>
public sealed class KnowledgeBaseScopeAggregationGateTests : IDisposable
{
    private readonly string _tempDir;

    public KnowledgeBaseScopeAggregationGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RR_ScopeAgg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    // ---------------- 基础设施 ----------------

    private static KnowledgeBaseDefinition Kb(string id, string name) =>
        new(id, name, null, Enabled: true, 1, 1, "spec", DateTime.UtcNow, DateTime.UtcNow);

    private static async Task<CatalogStore> BuildCatalogAsync(string root, params (string Id, string Name)[] kbs)
    {
        var catalog = new CatalogStore(Path.Combine(root, "system", "catalog.db"));
        await catalog.InitializeAsync();
        foreach (var (id, name) in kbs) await catalog.SaveKnowledgeBaseAsync(Kb(id, name));
        return catalog;
    }

    private sealed class LeaseStub : IStorageLease
    {
        public LeaseStub(IKnowledgeBaseStore store, string kbId)
        {
            Store = store;
            KnowledgeBaseId = kbId;
        }

        public string KnowledgeBaseId { get; }
        public long IndexEpochSnapshot => 1;
        public long ReadRevisionSnapshot => 1;
        public IKnowledgeBaseStore Store { get; }

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StorageStub : IKnowledgeBaseStorageManager
    {
        private readonly Dictionary<string, IKnowledgeBaseStore> _stores;

        public List<string> LeasedKbIds { get; } = new();
        public List<string> FlushedKbIds { get; } = new();

        public StorageStub(Dictionary<string, IKnowledgeBaseStore> stores) => _stores = stores;

        public ValueTask<IStorageLease> AcquireLeaseAsync(string knowledgeBaseId, StorageAccess access = StorageAccess.ReadOnly, CancellationToken ct = default)
        {
            if (!_stores.TryGetValue(knowledgeBaseId, out var store))
                throw new InvalidOperationException($"未预置知识库存储: {knowledgeBaseId}");
            LeasedKbIds.Add(knowledgeBaseId);
            return ValueTask.FromResult<IStorageLease>(new LeaseStub(store, knowledgeBaseId));
        }

        public Task<bool> ExistsAsync(string knowledgeBaseId, CancellationToken ct = default) => Task.FromResult(_stores.ContainsKey(knowledgeBaseId));
        public Task DeleteKnowledgeBaseAsync(string knowledgeBaseId, CancellationToken ct = default) => Task.CompletedTask;
        public Task BackupKnowledgeBaseAsync(string knowledgeBaseId, string backupFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreKnowledgeBaseAsync(string knowledgeBaseId, string backupFilePath, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---------------- G-KB3：BM25 聚合口径 ----------------

    [Fact]
    public async Task Bm25Summary_AllScope_SumsTotalsAndWeightedAverage_NotAverageOfAverages()
    {
        // 两库分块数相差 10 倍：算术平均与加权平均会明显不同。
        var storeA = new SqliteKnowledgeBaseStore("kb_big", Path.Combine(_tempDir, "kb_big.db"));
        var storeB = new SqliteKnowledgeBaseStore("kb_small", Path.Combine(_tempDir, "kb_small.db"));
        await storeA.InitializeAsync();
        await storeB.InitializeAsync();

        await storeA.CommitDocumentAsync(new IndexBatch(
            "kb_big", 1, 1, "c1", "big.md", "loc", "rev1", "hash1",
            new List<ChunkData>
            {
                new("a1", 0, new string('x', 1000), 250),
                new("a2", 1, new string('x', 1000), 250),
                new("a3", 2, new string('x', 1000), 250),
            }));
        await storeB.CommitDocumentAsync(new IndexBatch(
            "kb_small", 1, 1, "c2", "small.md", "loc", "rev1", "hash2",
            new List<ChunkData> { new("b1", 0, new string('x', 100), 25) }));

        var catalog = await BuildCatalogAsync(_tempDir, ("kb_big", "Big"), ("kb_small", "Small"));
        var manager = new StorageStub(new Dictionary<string, IKnowledgeBaseStore>
        {
            ["kb_big"] = storeA,
            ["kb_small"] = storeB,
        });

        var controller = new BM25IndexController(
            new Fts5BM25Store(Path.Combine(_tempDir, "bm25.db")),
            new SqliteVectorStore(Path.Combine(_tempDir, "vectors.db")),
            new ConfigManager(new ObsidianRagConfig { DataPath = _tempDir }),
            null!,
            NullLogger<BM25IndexController>.Instance,
            null,
            manager,
            catalog);

        // 无参与 __all__ 都必须走聚合（此前无参 → 第一个库）。
        foreach (var raw in new[] { null, "__all__" })
        {
            var result = await controller.GetSummary(raw);
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var summary = Assert.IsType<BM25Summary>(ok.Value);

            Assert.Equal("all", summary.Scope);
            Assert.Equal(2, summary.KnowledgeBases.Count);
            Assert.Equal(4, summary.TotalChunks);

            // 加权： (3*1000 + 1*100)/4 = 775，而不是 (1000+100)/2 = 550
            Assert.Equal(775.0, summary.AverageDocLength, 1);
        }

        // 单库仍返回该库自身口径
        var single = await controller.GetSummary("kb_small");
        var singleOk = Assert.IsType<OkObjectResult>(single.Result);
        var singleSummary = Assert.IsType<BM25Summary>(singleOk.Value);
        Assert.Equal("single", singleSummary.Scope);
        Assert.Equal(1, singleSummary.TotalChunks);
        Assert.Equal(100.0, singleSummary.AverageDocLength, 1);
    }

    // ---------------- G-KB2：Graph 统计聚合 ----------------

    [Fact]
    public async Task GraphStats_AllScope_EqualsSumOfPerKb()
    {
        var controller = BuildGraphController(out var storage);

        var perA = await GetStatsAsync(controller, "kb_a");
        var perB = await GetStatsAsync(controller, "kb_b");
        var all1 = await GetStatsAsync(controller, null);
        var all2 = await GetStatsAsync(controller, "__all__");

        Assert.Equal(perA.NodeCount + perB.NodeCount, all1.NodeCount);
        Assert.Equal(perA.NodeCount + perB.NodeCount, all2.NodeCount);
        Assert.Equal(perA.EdgeCount + perB.EdgeCount, all1.EdgeCount);

        // 作用域必须回显，否则调用方无法判断「这是某个库还是合计」
        Assert.Equal("all", all1.Scope);
        Assert.Equal(2, all1.KnowledgeBases.Count);
        Assert.Equal("single", perA.Scope);
        Assert.Single(perA.KnowledgeBases);
        Assert.Equal(2, storage.LeasedKbIds.Distinct().Count());
    }

    private static GraphController BuildGraphController(out StorageStub storage)
    {
        var root = Path.Combine(Path.GetTempPath(), "RR_GraphGate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var catalog = new CatalogStore(Path.Combine(root, "catalog.db"));
        catalog.InitializeAsync().GetAwaiter().GetResult();
        catalog.SaveKnowledgeBaseAsync(Kb("kb_a", "A")).GetAwaiter().GetResult();
        catalog.SaveKnowledgeBaseAsync(Kb("kb_b", "B")).GetAwaiter().GetResult();

        var storeA = new SqliteKnowledgeBaseStore("kb_a", Path.Combine(root, "kb_a.db"));
        var storeB = new SqliteKnowledgeBaseStore("kb_b", Path.Combine(root, "kb_b.db"));
        storeA.InitializeAsync().GetAwaiter().GetResult();
        storeB.InitializeAsync().GetAwaiter().GetResult();

        // 3 个节点 / 2 条边（A） + 5 个节点 / 3 条边（B）
        foreach (var (store, count, edges) in new[] { (storeA, 3, 2), (storeB, 5, 3) })
        {
            for (var i = 0; i < count; i++)
            {
                store.GraphStore.UpsertNodeAsync(new GraphNode { Id = $"n{i}.md", Title = $"N{i}", Type = "document" })
                    .GetAwaiter().GetResult();
            }
            for (var i = 0; i < edges; i++)
            {
                store.GraphStore.UpsertEdgesAsync(new[]
                {
                    new GraphEdge { FromId = "n0.md", ToId = $"x{i}.md", Type = "wikilink" }
                }).GetAwaiter().GetResult();
            }
        }

        storage = new StorageStub(new Dictionary<string, IKnowledgeBaseStore>
        {
            ["kb_a"] = storeA,
            ["kb_b"] = storeB,
        });

        var config = new ConfigManager(new ObsidianRagConfig { DataPath = root });
        var controller = new GraphController(
            MockGraphStore(),            // legacy（不应被调用）
            null!,                       // graphIndexingService
            config,
            NullLogger<GraphController>.Instance,
            null,                        // vectorStore
            null,                        // serviceProvider
            storage,
            catalog);

        return controller;
    }

    private static IGraphStore MockGraphStore()
        => new SqliteGraphStore(Path.Combine(Path.GetTempPath(), $"legacy_{Guid.NewGuid():N}.db"));

    private static async Task<GraphStatsEnvelope> GetStatsAsync(GraphController controller, string? kbId)
    {
        var result = await controller.GetStats(kbId);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<GraphStatsEnvelope>(ok.Value);
    }

    // ---------------- G-KB4：写操作必须显式作用域 ----------------

    [Fact]
    public async Task WriteEndpoints_RejectOmittedScope()
    {
        var catalog = await BuildCatalogAsync(_tempDir, ("kb_a", "A"));
        var controller = new BM25IndexController(
            new Fts5BM25Store(Path.Combine(_tempDir, "bm25.db")),
            new SqliteVectorStore(Path.Combine(_tempDir, "vectors.db")),
            new ConfigManager(new ObsidianRagConfig { DataPath = _tempDir }),
            null!,
            NullLogger<BM25IndexController>.Instance,
            null,
            null,
            catalog);

        // 缺省作用域 = 400，不能静默作用到第一个库
        var index = await controller.IndexAllDocuments(null);
        var indexBad = Assert.IsType<BadRequestObjectResult>(index.Result);
        Assert.Contains("scope_required", indexBad.Value!.ToString()!, StringComparison.Ordinal);
    }
}
