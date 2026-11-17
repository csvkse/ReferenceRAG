using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.McpTools;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G-KB5 / G-KB6：前后端「全部知识库」意图不得被吞掉，MCP 图谱工具必须跨库。
///
/// 背景（生产实测）：前端在「全部」时传 null（后端据此选了第一个库）；
/// MCP <c>graph_search</c> / <c>graph_search_by_title</c> 省略 knowledgeBaseId 时同样只搜首库，
/// 且回显的 knowledgeBaseId 让模型误以为范围正确。
/// </summary>
public sealed class KnowledgeBaseScopePropagationGateTests : IDisposable
{
    private readonly string _tempRoot;

    public KnowledgeBaseScopePropagationGateTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "RR_ScopeProp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    // ---------------- G-KB5：前端不得把 __all__ 折叠成 null ----------------

    [Theory]
    [InlineData("Graph.js")]
    [InlineData("BM25Index.js")]
    public void FrontendView_ForwardsAllSentinel_InsteadOfNulling(string viewFile)
    {
        var path = FindViewPath(viewFile);
        var src = File.ReadAllText(path);

        // 允许出现 `selectedKbId.value || '__all__'` 或 `return selectedKbId.value;`
        // 不允许出现「`!== '__all__' ? ... : null`」这种把全量意图丢掉的写法。
        var swallowPattern = new Regex(
            @"(?:!==|!=)\s*'__all__'\s*\?[^:]*:\s*null",
            RegexOptions.CultureInvariant);

        Assert.DoesNotContain("cbId", ""); // 占位，保持断言链可读
        Assert.False(swallowPattern.IsMatch(src),
            $"{viewFile} 仍把「全部知识库」折叠成 null：后端只能读到「未指定」，于是回落到第一个库");
    }

    private static string FindViewPath(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, "Business", "ReferenceRAG.Business", "wwwroot", "app", "views", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"找不到前端视图 {fileName}（从 {AppContext.BaseDirectory} 向上查找 8 层）");
    }

    // ---------------- G-KB6：MCP 图谱工具跨库 ----------------

    [Fact]
    public async Task McpGraphSearch_OmittedScope_SearchesAllLibraries_AndTagsNodes()
    {
        var catalog = new CatalogStore(Path.Combine(_tempRoot, "catalog.db"));
        await catalog.InitializeAsync();
        await catalog.SaveKnowledgeBaseAsync(Kb("kb_a", "A"));
        await catalog.SaveKnowledgeBaseAsync(Kb("kb_b", "B"));

        var storeA = new SqliteKnowledgeBaseStore("kb_a", Path.Combine(_tempRoot, "kb_a.db"));
        var storeB = new SqliteKnowledgeBaseStore("kb_b", Path.Combine(_tempRoot, "kb_b.db"));
        await storeA.InitializeAsync();
        await storeB.InitializeAsync();

        // 两个库各放一个标题含「MCP」的节点：只搜首库会漏掉另一个。
        await storeA.GraphStore.UpsertNodeAsync(new GraphNode { Id = "a/mcp.md", Title = "MCP 指南", Type = "document" });
        await storeB.GraphStore.UpsertNodeAsync(new GraphNode { Id = "b/mcp.md", Title = "MCP 协议", Type = "document" });

        var manager = new SimpleStorage(new Dictionary<string, IKnowledgeBaseStore>
        {
            ["kb_a"] = storeA,
            ["kb_b"] = storeB,
        });

        var services = new ServiceCollection()
            .AddSingleton<ICatalogStore>(catalog)
            .AddSingleton<IKnowledgeBaseStorageManager>(manager)
            .AddSingleton<IGraphStore>(new SqliteGraphStore(Path.Combine(_tempRoot, "legacy.db")))
            .AddSingleton<ConfigManager>(new ConfigManager(new ObsidianRagConfig { DataPath = _tempRoot }))
            .AddLogging()
            .BuildServiceProvider();

        var tool = new GraphSearchTools(services);
        var json = await tool.GraphSearchByTitle("MCP", 10, null);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("kbScope", out var scopeProp));
        Assert.Equal("all", scopeProp.GetString());

        var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(2, nodes.Count);
        // 每个节点都必须能回指到具体库，否则模型无法把结果用起来。
        Assert.All(nodes, n =>
        {
            Assert.True(n.TryGetProperty("knowledgeBaseId", out var kbProp));
            Assert.False(string.IsNullOrWhiteSpace(kbProp.GetString()));
        });
        Assert.Equal(2, nodes.Select(n => n.GetProperty("knowledgeBaseId").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task McpGraphSearch_ExplicitScope_StaysSingle()
    {
        var catalog = new CatalogStore(Path.Combine(_tempRoot, "catalog.db"));
        await catalog.InitializeAsync();
        await catalog.SaveKnowledgeBaseAsync(Kb("kb_a", "A"));

        var storeA = new SqliteKnowledgeBaseStore("kb_a", Path.Combine(_tempRoot, "kb_a.db"));
        await storeA.InitializeAsync();
        await storeA.GraphStore.UpsertNodeAsync(new GraphNode { Id = "a/mcp.md", Title = "MCP 指南", Type = "document" });

        var manager = new SimpleStorage(new Dictionary<string, IKnowledgeBaseStore> { ["kb_a"] = storeA });
        var services = new ServiceCollection()
            .AddSingleton<ICatalogStore>(catalog)
            .AddSingleton<IKnowledgeBaseStorageManager>(manager)
            .AddSingleton<IGraphStore>(new SqliteGraphStore(Path.Combine(_tempRoot, "legacy.db")))
            .AddSingleton<ConfigManager>(new ConfigManager(new ObsidianRagConfig { DataPath = _tempRoot }))
            .AddLogging()
            .BuildServiceProvider();

        var tool = new GraphSearchTools(services);
        var json = await tool.GraphSearchByTitle("MCP", 10, "kb_a");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("single", root.GetProperty("kbScope").GetString());
        Assert.Equal("kb_a", root.GetProperty("knowledgeBaseId").GetString());
    }

    private static KnowledgeBaseDefinition Kb(string id, string name) =>
        new(id, name, null, Enabled: true, 1, 1, "spec", DateTime.UtcNow, DateTime.UtcNow);

    private sealed class SimpleStorage : IKnowledgeBaseStorageManager
    {
        private readonly Dictionary<string, IKnowledgeBaseStore> _stores;
        public SimpleStorage(Dictionary<string, IKnowledgeBaseStore> stores) => _stores = stores;

        public ValueTask<IStorageLease> AcquireLeaseAsync(string knowledgeBaseId, StorageAccess access = StorageAccess.ReadOnly, CancellationToken ct = default)
        {
            if (!_stores.TryGetValue(knowledgeBaseId, out var store))
                throw new InvalidOperationException($"未预置存储: {knowledgeBaseId}");
            return ValueTask.FromResult<IStorageLease>(new Lease(store, knowledgeBaseId));
        }

        public Task<bool> ExistsAsync(string knowledgeBaseId, CancellationToken ct = default) => Task.FromResult(_stores.ContainsKey(knowledgeBaseId));
        public Task DeleteKnowledgeBaseAsync(string knowledgeBaseId, CancellationToken ct = default) => Task.CompletedTask;
        public Task BackupKnowledgeBaseAsync(string knowledgeBaseId, string backupFilePath, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreKnowledgeBaseAsync(string knowledgeBaseId, string backupFilePath, CancellationToken ct = default) => Task.CompletedTask;

        private sealed class Lease : IStorageLease
        {
            public Lease(IKnowledgeBaseStore store, string kbId) { Store = store; KnowledgeBaseId = kbId; }
            public string KnowledgeBaseId { get; }
            public long IndexEpochSnapshot => 1;
            public long ReadRevisionSnapshot => 1;
            public IKnowledgeBaseStore Store { get; }
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
