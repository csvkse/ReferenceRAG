using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Service.McpTools;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G-KB-1 门禁：MCP 检索工具必须以物理知识库为事实源。
///
/// 背景：v2 后所有新索引写入 per-KB kb.db，旧单库 vectors.db 不再收到写入。
/// 若 MCP 仍走旧 SearchService，会返回过期/空结果，且无法按库限定。
/// 本门禁锁定：semantic_search / hybrid_search / lexical_search 的结果来自
/// IKnowledgeBaseSearchService，且 knowledgeBaseIds 能真实收敛范围。
/// </summary>
public sealed class McpKnowledgeBaseRoutingGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalog;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly KnowledgeBaseService _kbService;
    private readonly KnowledgeBaseSearchService _kbSearch;
    private readonly ServiceProvider _provider;

    public McpKnowledgeBaseRoutingGateTests()
    {
        _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-kb-gate-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_tempDir);

        _catalog = new CatalogStore(System.IO.Path.Combine(_tempDir, "system", "catalog.db"));
        _catalog.InitializeAsync().GetAwaiter().GetResult();
        _storageManager = new KnowledgeBaseStorageManager(_tempDir);
        _kbService = new KnowledgeBaseService(_catalog, _storageManager,
            modelRouter: null, logger: NullLogger<KnowledgeBaseService>.Instance);
        _kbSearch = new KnowledgeBaseSearchService(_catalog, _storageManager,
            new RrfSearchFusionStrategy(), NullLogger<KnowledgeBaseSearchService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IKnowledgeBaseService>(_kbService);
        services.AddSingleton<IKnowledgeBaseSearchService>(_kbSearch);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _storageManager.Dispose();
        _catalog.Dispose();
        try { System.IO.Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<KnowledgeBaseDefinition> SeedAsync(string name, string path, params string[] contents)
    {
        var kb = await _kbService.CreateKnowledgeBaseAsync(
            name, null, "spec_lexical_only", null, "spec_chunk_lexical_paragraph", null);
        var connector = await _kbService.AddConnectorAsync(kb.Id, "LocalFolder", path);
        foreach (var content in contents)
        {
            await _kbService.CreateDocumentAsync(kb.Id, connector.Id, null, content);
        }
        return kb;
    }

    [Fact]
    public async Task SemanticSearch_ReturnsKnowledgeBaseChunks()
    {
        var path = System.IO.Path.Combine(_tempDir, "alpha");
        System.IO.Directory.CreateDirectory(path);
        var kb = await SeedAsync("Alpha", path, "# 量子计算\n\n量子比特与退相干原理。", "# 容器编排\n\nDocker 与 Kubernetes 调度。");

        var tools = new RagSearchTools(_provider);
        var json = await tools.SemanticSearch("量子", topK: 5);

        Assert.Contains("\"success\": true", json);
        Assert.Contains("\"chunkId\"", json);
        Assert.Contains("knowledgeBaseIds", json);
        Assert.Contains(kb.Id, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("量子", json);
    }

    [Fact]
    public async Task Search_WithKnowledgeBaseIds_ScopesToOneLibrary()
    {
        var pathA = System.IO.Path.Combine(_tempDir, "libA");
        var pathB = System.IO.Path.Combine(_tempDir, "libB");
        System.IO.Directory.CreateDirectory(pathA);
        System.IO.Directory.CreateDirectory(pathB);

        var kbA = await SeedAsync("库A", pathA, "# 甲主题\n\n甲库独有内容 AlphaOnly 关键词。");
        var kbB = await SeedAsync("库B", pathB, "# 乙主题\n\n乙库独有内容 BetaOnly 关键词。");

        var tools = new RagSearchTools(_provider);
        // 只查库A：结果不得来自库B，且不应因跨库聚合而混入
        var scoped = await tools.LexicalSearch("AlphaOnly", topK: 5, knowledgeBaseIds: $"[\"{kbA.Id}\"]");
        Assert.Contains(kbA.Id, scoped);

        // 未限定：两个库都可被检出
        var all = await tools.LexicalSearch("BetaOnly", topK: 5);
        Assert.Contains(kbB.Id, all);
    }

    [Fact]
    public async Task Search_WithoutAnyKnowledgeBase_ReturnsExplicitError_NotLegacyFallback()
    {
        var tools = new RagSearchTools(_provider);
        var json = await tools.SemanticSearch("任意查询", topK: 5);

        Assert.Contains("\"success\": false", json);
        Assert.Contains("知识库", json);
    }

    [Fact]
    public async Task DrillDown_ByDocumentId_ReturnsChunks()
    {
        var path = System.IO.Path.Combine(_tempDir, "drill");
        System.IO.Directory.CreateDirectory(path);
        var kb = await SeedAsync("Drill", path,
            "# 第一章\n\n第一段内容。\n\n## 小节\n\n第二段内容。\n\n# 第二章\n\n第三段内容。");

        var tools = new RagKnowledgeBaseToolsProbe(_provider);
        var searchJson = await new RagSearchTools(_provider).LexicalSearch("内容", topK: 3);
        Assert.Contains("documentId", searchJson);

        // 用检索出的 documentId 展开
        var docId = ExtractFirstDocumentId(searchJson);
        Assert.False(string.IsNullOrWhiteSpace(docId));

        var json = await tools.DrillDown(kb.Id, documentId: docId, limit: 10);
        Assert.Contains("\"success\": true", json);
        Assert.Contains("chunks", json);
        Assert.Contains("内容", json);
    }

    [Fact]
    public async Task DrillDown_WithUnknownChunk_ReturnsExplicitFailure()
    {
        var path = System.IO.Path.Combine(_tempDir, "drill2");
        System.IO.Directory.CreateDirectory(path);
        var kb = await SeedAsync("Drill2", path, "# 内容\n\n正文。");

        var tools = new RagKnowledgeBaseToolsProbe(_provider);
        var json = await tools.DrillDown(kb.Id, chunkId: "no-such-chunk");
        Assert.Contains("\"success\": false", json);
    }

    private static string? ExtractFirstDocumentId(string json)
    {
        const string key = "\"documentId\":";
        var idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = json.IndexOf('"', idx + key.Length);
        if (start < 0) return null;
        var end = json.IndexOf('"', start + 1);
        return end > start ? json[(start + 1)..end] : null;
    }

    /// <summary>仅用于把 KnowledgeBaseDrillDown 的字符串结果转成可断言对象。</summary>
    private sealed class RagKnowledgeBaseToolsProbe
    {
        private readonly RagSearchTools _inner;
        public RagKnowledgeBaseToolsProbe(IServiceProvider sp) => _inner = new RagSearchTools(sp);

        public Task<string> DrillDown(string kbId, string? chunkId = null, string? documentId = null, int limit = 20) =>
            _inner.KnowledgeBaseDrillDown(kbId, chunkId, documentId, limit);
    }
}
