using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Service.McpTools;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G-KB-3 门禁：MCP 的索引/源统计必须取自 per-KB kb.db。
///
/// 背景：v2 之后旧单库 vectors.db 已停止写入，但 get_index_status / list_sources
/// 仍读它 —— 真机实测返回 940（旧库残留），且两个数据源各报 940/940（同一全局数字被复制到每一行）。
/// 本门禁锁定：两者都按知识库连接器物理统计输出，纯词法库也必须报出真实分块数。
/// </summary>
public sealed class McpKnowledgeBaseStatsGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalog;
    private readonly KnowledgeBaseStorageManager _storage;
    private readonly KnowledgeBaseService _kbService;
    private readonly ServiceProvider _provider;

    public McpKnowledgeBaseStatsGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-stats-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _catalog = new CatalogStore(Path.Combine(_tempDir, "system", "catalog.db"));
        _catalog.InitializeAsync().GetAwaiter().GetResult();
        _storage = new KnowledgeBaseStorageManager(_tempDir);
        _kbService = new KnowledgeBaseService(_catalog, _storage, modelRouter: null,
            logger: NullLogger<KnowledgeBaseService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton<IKnowledgeBaseService>(_kbService);
        services.AddSingleton<IKnowledgeBaseStorageManager>(_storage);
        // config.Sources 投影：给连接器路径一个已有源名，验证「同路径求和、异路径分别计数」
        var config = new ReferenceRAG.Core.Models.ObsidianRagConfig
        {
            Sources = new List<ReferenceRAG.Core.Models.SourceFolder>()
        };
        var configManager = new ReferenceRAG.Core.Services.ConfigManager(config);
        configManager.Save(config);
        services.AddSingleton(configManager);
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _storage.Dispose();
        _catalog.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
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
    public async Task GetIndexStatus_ReportsChunksForLexicalOnlyKnowledgeBase()
    {
        var path = Path.Combine(_tempDir, "lexkb");
        Directory.CreateDirectory(path);
        var kb = await SeedAsync("词法库", path, "# 主题\n\n第一段正文。\n\n第二段正文。");

        var json = await new IndexStatusTools(_provider).GetIndexStatus();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean(), json);
        Assert.Equal(1, root.GetProperty("knowledgeBaseCount").GetInt32());
        Assert.True(root.GetProperty("totalChunks").GetInt32() > 0, "纯词法库必须报出真实分块数：" + json);

        var kbEntry = root.GetProperty("knowledgeBases").EnumerateArray().Single();
        Assert.Equal(kb.Id, kbEntry.GetProperty("knowledgeBaseId").GetString());
        Assert.True(kbEntry.GetProperty("chunkCount").GetInt32() > 0);
        Assert.Equal(0, kbEntry.GetProperty("vectorCount").GetInt32()); // 纯词法库无向量，但不应因此判为空
        Assert.Equal("indexed", kbEntry.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ListSources_CountsPerConnector_NotGlobalSnapshotCopiedPerRow()
    {
        var pathA = Path.Combine(_tempDir, "srcA");
        var pathB = Path.Combine(_tempDir, "srcB");
        Directory.CreateDirectory(pathA);
        Directory.CreateDirectory(pathB);

        // 同一 KB 下两个连接器，文档数不同 → 两行计数必须不同
        var kb = await _kbService.CreateKnowledgeBaseAsync(
            "计数库", null, "spec_lexical_only", null, "spec_chunk_lexical_paragraph", null);
        var connA = await _kbService.AddConnectorAsync(kb.Id, "LocalFolder", pathA);
        var connB = await _kbService.AddConnectorAsync(kb.Id, "LocalFolder", pathB);
        for (var i = 0; i < 3; i++)
            await _kbService.CreateDocumentAsync(kb.Id, connA.Id, $"a{i}.md", $"# A{i}\n\n正文 {i}");
        await _kbService.CreateDocumentAsync(kb.Id, connB.Id, "b0.md", "# B0\n\n正文");

        var json = await new SourceManagementTools(_provider).ListSources($"""["{kb.Id}"]""");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("success").GetBoolean(), json);
        var rows = root.GetProperty("sources").EnumerateArray()
            .Select(r => (Path: r.GetProperty("path").GetString()!, Files: r.GetProperty("fileCount").GetInt32()))
            .ToList();

        var rowA = rows.Single(r => r.Path == pathA);
        var rowB = rows.Single(r => r.Path == pathB);
        Assert.Equal(3, rowA.Files);
        Assert.Equal(1, rowB.Files);
        Assert.NotEqual(rowA.Files, rowB.Files); // 旧实现会把全局总数复制到每一行
    }

    [Fact]
    public async Task ListSources_WithUnknownKnowledgeBaseId_ReturnsNoRowsForIt()
    {
        var json = await new SourceManagementTools(_provider).ListSources("""["deadbeefdeadbeef"]""");
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, doc.RootElement.GetProperty("knowledgeBaseCount").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("sources").EnumerateArray());
    }
}
