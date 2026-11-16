using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.McpTools;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G-MCP-4..8 门禁：MCP 工具结果信封、文件沙箱、源解析一致性与统计口径。
///
/// 背景（生产实测）：
/// - read_note_content 顶层返回 `content`，与 CallToolResult.content 冲突 → 结果被整段吞空；
/// - get_note_outline 在 sourceName 为空时不做包含性校验 → /etc/passwd 可读；
/// - list_sources 给出的连接器名在 get_file_tree 里解析不了；
/// - get_source_info 仍读旧库 → 同路径与 list_sources 数字矛盾。
/// </summary>
public sealed class McpToolContractHardeningGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalog;
    private readonly KnowledgeBaseStorageManager _storage;
    private readonly KnowledgeBaseService _kbService;
    private readonly ConfigManager _configManager;

    public McpToolContractHardeningGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _catalog = new CatalogStore(Path.Combine(_tempDir, "system", "catalog.db"));
        _catalog.InitializeAsync().GetAwaiter().GetResult();
        _storage = new KnowledgeBaseStorageManager(_tempDir);
        _kbService = new KnowledgeBaseService(_catalog, _storage, modelRouter: null,
            logger: NullLogger<KnowledgeBaseService>.Instance);

        var config = new ObsidianRagConfig
        {
            DataPath = _tempDir,
            Sources = new List<SourceFolder>()
        };
        _configManager = new ConfigManager(config);
        _configManager.Save(config);
    }

    public void Dispose()
    {
        _storage.Dispose();
        _catalog.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_configManager);
        services.AddSingleton<ICatalogStore>(_catalog);
        services.AddSingleton<IKnowledgeBaseService>(_kbService);
        services.AddSingleton<IKnowledgeBaseStorageManager>(_storage);
        services.AddSingleton<ISourceCatalog, SourceCatalog>();
        services.AddSingleton<IFileAccessPolicy, SourceScopedFileAccessPolicy>();
        return services.BuildServiceProvider();
    }

    private async Task<(KnowledgeBaseDefinition Kb, ConnectorDefinition Conn)> SeedConnectorAsync(
        string kbName, string path, int documents)
    {
        var kb = await _kbService.CreateKnowledgeBaseAsync(
            kbName, null, "spec_lexical_only", null, "spec_chunk_lexical_paragraph", null);
        var conn = await _kbService.AddConnectorAsync(kb.Id, "LocalFolder", path);
        for (var i = 0; i < documents; i++)
            await _kbService.CreateDocumentAsync(kb.Id, conn.Id, $"doc{i}.md", $"# 文档 {i}\n\n正文 {i}");
        return (kb, conn);
    }

    // ───────────────────────── G-MCP-4 结果信封 ─────────────────────────

    [Fact]
    public void Fail_Envelope_IsProtocolDetectable()
    {
        var json = McpResult.Fail("boom", "some_code");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("isError").GetBoolean());
        var text = root.GetProperty("content").EnumerateArray().Single().GetProperty("text").GetString()!;
        using var inner = JsonDocument.Parse(text);
        Assert.False(inner.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("boom", inner.RootElement.GetProperty("error").GetString());
        Assert.Equal("some_code", inner.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public void Ok_Envelope_RelocatesReservedTopLevelFields()
    {
        // read_note_content 曾返回顶层 content，被协议层覆盖成空数组
        var json = McpResult.OkRaw("""{"success":true,"content":"BODY","text":"keep"}""");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("content", out _), "顶层不得出现协议保留字段 content");
        Assert.Equal("keep", root.GetProperty("text").GetString());
        Assert.Equal("BODY", root.GetProperty("payload").GetProperty("content").GetString());
    }

    [Fact]
    public void ToolSources_MustNotReturnTopLevelContentOrIsError()
    {
        // 静态防线：工具实现里一旦再出现顶层 content，运行期就会被 McpResult.Sanitize 兜住，
        // 但这里直接禁止以减少隐式行为。
        var dir = FindRepoRoot();
        var files = Directory.GetFiles(Path.Combine(dir, "Host", "ReferenceRAG.ApiHost", "McpTools"), "*.cs");
        var offenders = new List<string>();
        foreach (var file in files)
        {
            if (Path.GetFileName(file) is "McpResult.cs" or "McpInputSchemaEnricher.cs") continue;
            var text = File.ReadAllText(file);
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"JsonSerializer\.Serialize\(new\s*\{[^}]*content\s*="))
                offenders.Add(Path.GetFileName(file));
        }
        Assert.True(offenders.Count == 0, "以下工具仍以顶层 content 返回（会被协议层吞掉）：" + string.Join(",", offenders));
    }

    // ───────────────────────── G-MCP-5 文件沙箱 ─────────────────────────

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/app/data/appsettings.json")]
    [InlineData("/tmp/../etc/passwd")]
    public async Task FileAccessPolicy_RejectsAbsolutePathsOutsideRegisteredSources(string path)
    {
        var services = BuildServices();
        var policy = services.GetRequiredService<IFileAccessPolicy>();

        var ex = await Assert.ThrowsAsync<FileAccessDeniedException>(
            () => policy.ResolveAsync(path, null));
        Assert.Contains("不在任何已注册数据源内", ex.Message);
    }

    [Fact]
    public async Task FileAccessPolicy_AllowsPathsInsideRegisteredSource()
    {
        var corpus = Path.Combine(_tempDir, "corpus");
        Directory.CreateDirectory(corpus);
        var file = Path.Combine(corpus, "a.md");
        await File.WriteAllTextAsync(file, "# hi");

        ReplaceSources(new SourceFolder { Name = "corpus", Path = corpus, Enabled = true });

        var services = BuildServices();
        var policy = services.GetRequiredService<IFileAccessPolicy>();
        var resolved = await policy.ResolveAsync(file, null);
        Assert.Equal(file.Replace('\\', '/'), resolved);
    }

    [Fact]
    public async Task FileAccessPolicy_RejectsRelativeTraversalInsideSource()
    {
        var corpus = Path.Combine(_tempDir, "corpus");
        Directory.CreateDirectory(corpus);
        ReplaceSources(new SourceFolder { Name = "corpus", Path = corpus, Enabled = true });

        var services = BuildServices();
        var policy = services.GetRequiredService<IFileAccessPolicy>();
        await Assert.ThrowsAsync<FileAccessDeniedException>(
            () => policy.ResolveAsync("../../etc/passwd", "corpus"));
    }

    // ───────────────────────── G-MCP-6 源解析一致 ─────────────────────────

    [Fact]
    public async Task SourceCatalog_ResolvesKnowledgeBaseConnectorNames()
    {
        var path = Path.Combine(_tempDir, "kbdir");
        Directory.CreateDirectory(path);
        var (kb, _) = await SeedConnectorAsync("测试库", path, documents: 2);

        var services = BuildServices();
        var catalog = services.GetRequiredService<ISourceCatalog>();
        var list = await catalog.ListAsync();

        var connectorName = $"{kb.Name} - kbdir";
        var descriptor = await catalog.ResolveAsync(connectorName);

        Assert.NotNull(descriptor);
        Assert.Equal("KnowledgeBaseOnly", descriptor!.Origin);
        Assert.Equal(path, descriptor.Path);
        Assert.Contains(list, s => s.Name == connectorName);

        // 同一名字必须也能被路径解析到（get_file_tree 依赖此语义）
        var byPath = await catalog.ResolveAsync(path);
        Assert.Equal(connectorName, byPath!.Name);
    }

    // ───────────────────────── G-MCP-7 统计口径一致 ─────────────────────────

    [Fact]
    public async Task ListSourcesAndGetSourceInfo_AgreeOnSamePath()
    {
        var path = Path.Combine(_tempDir, "shared");
        Directory.CreateDirectory(path);
        ReplaceSources(new SourceFolder { Name = "共享源", Path = path, Enabled = true });
        await SeedConnectorAsync("统计库", path, documents: 3);

        var services = BuildServices();
        var tools = new SourceManagementTools(services);

        var listJson = await tools.ListSources();
        using var listDoc = JsonDocument.Parse(listJson);
        var row = listDoc.RootElement.GetProperty("sources").EnumerateArray()
            .First(r => r.GetProperty("path").GetString() == path);
        var listFiles = row.GetProperty("fileCount").GetInt32();
        var listChunks = row.GetProperty("chunkCount").GetInt32();

        var infoJson = await tools.GetSourceInfo("共享源");
        using var infoDoc = JsonDocument.Parse(infoJson);
        var stats = infoDoc.RootElement.GetProperty("source").GetProperty("stats");

        Assert.Equal(listFiles, stats.GetProperty("documentCount").GetInt32());
        Assert.Equal(listChunks, stats.GetProperty("chunkCount").GetInt32());
        Assert.True(listFiles > 0);
    }

    // ───────────────────────── helpers ─────────────────────────

    private void ReplaceSources(params SourceFolder[] sources)
    {
        var config = _configManager.Load();
        config.Sources = sources.ToList();
        _configManager.Save(config);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到代码仓根目录");
    }
}
