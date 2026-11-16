using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Service.McpTools;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// list_files 过滤语义门禁（G-N1..G-N5）。
///
/// 历史缺陷：sourceId 在 KB 分支被当作未转义 LIKE 关键词，生产实测
/// sourceId="%" 匹配全库、"_" 为单字符通配，且 0 条与"过滤生效但没命中"无法区分。
/// 本组锁定：connectorId 精确、未知 connectorId 报错、通配符按字面、可判别计数。
/// </summary>
public sealed class McpListFilesFilterGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalog;
    private readonly KnowledgeBaseStorageManager _storage;
    private readonly KnowledgeBaseService _kbService;
    private readonly ServiceProvider _provider;

    public McpListFilesFilterGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-listfiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _catalog = new CatalogStore(Path.Combine(_tempDir, "system", "catalog.db"));
        _catalog.InitializeAsync().GetAwaiter().GetResult();
        _storage = new KnowledgeBaseStorageManager(_tempDir);
        _kbService = new KnowledgeBaseService(_catalog, _storage, modelRouter: null,
            logger: NullLogger<KnowledgeBaseService>.Instance);

        var config = new ObsidianRagConfig { DataPath = _tempDir, Sources = new List<SourceFolder>() };
        var configManager = new ConfigManager(config);
        configManager.Save(config);

        var services = new ServiceCollection();
        services.AddSingleton<IKnowledgeBaseService>(_kbService);
        services.AddSingleton<IKnowledgeBaseStorageManager>(_storage);
        services.AddSingleton(configManager);
        services.AddSingleton<ISourceCatalog, SourceCatalog>();
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _storage.Dispose();
        _catalog.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task<(KnowledgeBaseDefinition Kb, ConnectorDefinition ConnA, ConnectorDefinition ConnB)>
        SeedTwoConnectorsAsync()
    {
        var pathA = Path.Combine(_tempDir, "kb-a");
        var pathB = Path.Combine(_tempDir, "kb-b");
        Directory.CreateDirectory(pathA);
        Directory.CreateDirectory(pathB);

        var kb = await _kbService.CreateKnowledgeBaseAsync(
            "语料库", null, "spec_lexical_only", null, "spec_chunk_lexical_paragraph", null);
        var connA = await _kbService.AddConnectorAsync(kb.Id, "LocalFolder", pathA);
        var connB = await _kbService.AddConnectorAsync(kb.Id, "LocalFolder", pathB);

        for (var i = 0; i < 3; i++)
            await _kbService.CreateDocumentAsync(kb.Id, connA.Id, $"note-{i}.md", $"# A {i}\n\n正文");
        for (var i = 0; i < 2; i++)
            await _kbService.CreateDocumentAsync(kb.Id, connB.Id, $"book-{i}.txt", $"# B {i}\n\n正文");

        return (kb, connA, connB);
    }

    private async Task<JsonElement> ListAsync(string? kbIds, string? connectorId, string? pathKeyword, int limit = 20)
    {
        var json = await new IndexStatusTools(_provider).ListFiles(kbIds, connectorId, pathKeyword, limit);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ConnectorId_FiltersExactly_AndEchoesAppliedFilters()
    {
        var (kb, connA, _) = await SeedTwoConnectorsAsync();

        var root = await ListAsync($"""["{kb.Id}"]""", connA.Id, null);

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        Assert.Equal(3, root.GetProperty("totalMatched").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(connA.Id,
            root.GetProperty("appliedFilters").GetProperty("connectorId").GetString());

        var files = root.GetProperty("files").EnumerateArray().ToList();
        Assert.All(files, f => Assert.Equal(connA.Id, f.GetProperty("connectorId").GetString()));
    }

    [Fact]
    public async Task UnknownConnectorId_ReturnsError_NotEmptySuccess()
    {
        var (kb, _, _) = await SeedTwoConnectorsAsync();

        var root = await ListAsync($"""["{kb.Id}"]""", "deadbeefdeadbeef", null);

        var text = root.GetProperty("content").EnumerateArray().Single().GetProperty("text").GetString()!;
        Assert.True(root.GetProperty("isError").GetBoolean());
        Assert.Contains("connector_not_found",
            root.GetProperty("structuredContent").GetProperty("errorCode").GetString());
        Assert.Contains("connector_not_found", text);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    public async Task WildcardCharacters_AreTreatedLiterally(string keyword)
    {
        var (kb, _, _) = await SeedTwoConnectorsAsync();

        var root = await ListAsync($"""["{kb.Id}"]""", null, keyword);

        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.True(0 == root.GetProperty("count").GetInt32(), "通配符必须按字面匹配，不得匹配全库");
        Assert.Equal(0, root.GetProperty("totalMatched").GetInt32());
        Assert.Equal(keyword, root.GetProperty("appliedFilters").GetProperty("pathKeyword").GetString());
    }

    [Fact]
    public async Task NonMatchingKeyword_IsDistinguishableFromNoFilter()
    {
        var (kb, _, _) = await SeedTwoConnectorsAsync();

        var none = await ListAsync($"""["{kb.Id}"]""", null, "ZZZNOTEXIST");
        Assert.True(none.GetProperty("success").GetBoolean());
        Assert.Equal(0, none.GetProperty("count").GetInt32());
        Assert.Equal(0, none.GetProperty("totalMatched").GetInt32());
        Assert.Equal("ZZZNOTEXIST",
            none.GetProperty("appliedFilters").GetProperty("pathKeyword").GetString());

        var all = await ListAsync($"""["{kb.Id}"]""", null, null);
        Assert.True(5 == all.GetProperty("totalMatched").GetInt32(), "无过滤时应覆盖两个连接器的全部文档");
        Assert.Null(all.GetProperty("appliedFilters").GetProperty("pathKeyword").GetString());
    }

    [Fact]
    public async Task ConnectorAndKeyword_ComposeAsIntersection()
    {
        var (kb, connA, _) = await SeedTwoConnectorsAsync();

        var onlyA = await ListAsync($"""["{kb.Id}"]""", connA.Id, null);
        var composed = await ListAsync($"""["{kb.Id}"]""", connA.Id, ".md");

        Assert.True(onlyA.GetProperty("count").GetInt32() == composed.GetProperty("count").GetInt32(),
            "连接器内所有文档都是 .md，交集应等于单连接器结果");

        var miss = await ListAsync($"""["{kb.Id}"]""", connA.Id, ".txt");
        Assert.True(0 == miss.GetProperty("count").GetInt32(), "交集不得跨连接器泄露");
    }

    [Fact]
    public async Task Limit_ReportsTruncatedWhenTotalExceeds()
    {
        var (kb, _, _) = await SeedTwoConnectorsAsync();

        var root = await ListAsync($"""["{kb.Id}"]""", null, null, limit: 2);

        Assert.Equal(2, root.GetProperty("count").GetInt32());
        Assert.Equal(5, root.GetProperty("totalMatched").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean(), "被截断必须可判别，否则调用方会以为只有 2 条");
    }

    [Fact]
    public async Task UnknownKnowledgeBase_ReturnsError_NotEmptySuccess()
    {
        var root = await ListAsync("""["deadbeefdeadbeef"]""", null, null);

        Assert.True(root.GetProperty("isError").GetBoolean());
        Assert.Equal("knowledge_base_not_found",
            root.GetProperty("structuredContent").GetProperty("errorCode").GetString());
    }
}
