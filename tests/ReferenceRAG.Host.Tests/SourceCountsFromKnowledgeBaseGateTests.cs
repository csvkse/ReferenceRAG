using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G-1 门禁：源管理列表的「文件数/分块数」必须取自 per-KB kb.db 事实源。
///
/// 背景（同类缺陷）：物理隔离架构落地后，SourcesController 仍读旧单库 vectors.db 的
/// files 表。结果是「只在知识库里入库过的路径恒为 0」，而旧快照又与 KB 真实值漂移，
/// 用户看到过期数字。本门禁锁定：计数只来自连接器物理统计，与旧库快照无关。
/// </summary>
public sealed class SourceCountsFromKnowledgeBaseGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalogStore;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly KnowledgeBaseService _kbService;
    private readonly Mock<IVectorStore> _legacyVectorStore;

    public SourceCountsFromKnowledgeBaseGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"refcount-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _catalogStore = new CatalogStore(Path.Combine(_tempDir, "system", "catalog.db"));
        _catalogStore.InitializeAsync().GetAwaiter().GetResult();
        _storageManager = new KnowledgeBaseStorageManager(_tempDir);
        _kbService = new KnowledgeBaseService(
            _catalogStore, _storageManager, modelRouter: null, logger: NullLogger<KnowledgeBaseService>.Instance);

        _legacyVectorStore = new Mock<IVectorStore>();
        // 旧库快照故意给出「错误但非零」的值：任何依赖它的实现都会被下面的断言抓到。
        _legacyVectorStore.Setup(v => v.GetSourceStatsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SourceFileStat>
            {
                new() { Source = "共享路径源", FileCount = 999, ChunkCount = 999 },
                new() { Source = "仅知识库路径源", FileCount = 777, ChunkCount = 777 }
            });
    }

    public void Dispose()
    {
        _storageManager.Dispose();
        _catalogStore.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private SourcesController CreateController(ObsidianRagConfig config)
    {
        var configManager = new ConfigManager(config);
        configManager.Save(config);
        return new SourcesController(
            configManager,
            _legacyVectorStore.Object,
            new Mock<IFileIndexPipeline>().Object,
            new Mock<IFileMonitorService>().Object,
            NullLogger<SourcesController>.Instance,
            catalogStore: _catalogStore,
            kbService: _kbService);
    }

    private async Task<(KnowledgeBaseDefinition Kb, ConnectorDefinition Connector)> SeedKnowledgeBaseAsync(
        string name, string path, int documentCount)
    {
        var kb = await _kbService.CreateKnowledgeBaseAsync(
            name, null, "spec_lexical_only", null, "spec_chunk_lexical_paragraph", null);
        var connector = await _kbService.AddConnectorAsync(kb.Id, "LocalFolder", path);
        for (var i = 0; i < documentCount; i++)
        {
            await _kbService.CreateDocumentAsync(kb.Id, connector.Id, $"doc{i}.md", $"# 文档 {i}\n\n正文内容 {i}");
        }
        return (kb, connector);
    }

    [Fact]
    public async Task GetAll_ReportsKnowledgeBaseCounts_NotLegacySnapshot()
    {
        var path = Path.Combine(_tempDir, "corpus");
        Directory.CreateDirectory(path);
        await SeedKnowledgeBaseAsync("计数库", path, documentCount: 3);

        var config = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder>
            {
                new() { Name = "仅知识库路径源", Path = path, Enabled = true }
            }
        };
        var controller = CreateController(config);

        var result = await controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var sources = Assert.IsAssignableFrom<List<SourceDetail>>(ok.Value);

        var row = Assert.Single(sources);
        Assert.Equal(3, row.FileCount);          // 旧库快照给的是 777/999
        Assert.True(row.ChunkCount > 0, "分块数应来自 kb.db 物理统计");
        Assert.NotEqual(777, row.FileCount);
        Assert.NotEqual(999, row.FileCount);
        Assert.NotNull(row.LastIndexed);
    }

    [Fact]
    public async Task GetAll_SumsCountsAcrossKnowledgeBasesSharingSamePath()
    {
        var path = Path.Combine(_tempDir, "shared");
        Directory.CreateDirectory(path);
        await SeedKnowledgeBaseAsync("库A", path, documentCount: 2);
        await SeedKnowledgeBaseAsync("库B", path, documentCount: 4);

        var config = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder> { new() { Name = "共享路径源", Path = path, Enabled = true } }
        };
        var controller = CreateController(config);

        var ok = Assert.IsType<OkObjectResult>((await controller.GetAll()).Result);
        var sources = Assert.IsAssignableFrom<List<SourceDetail>>(ok.Value);
        var row = Assert.Single(sources);
        Assert.Equal(6, row.FileCount);   // 跨库求和：同一路径允许挂多个知识库
    }

    [Fact]
    public async Task GetAll_PathWithoutConnector_ReturnsZeroNotLegacyValue()
    {
        var unmounted = Path.Combine(_tempDir, "unmounted");
        Directory.CreateDirectory(unmounted);

        var config = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder> { new() { Name = "共享路径源", Path = unmounted, Enabled = true } }
        };
        var controller = CreateController(config);

        var ok = Assert.IsType<OkObjectResult>((await controller.GetAll()).Result);
        var sources = Assert.IsAssignableFrom<List<SourceDetail>>(ok.Value);
        var row = Assert.Single(sources);
        Assert.Equal(0, row.FileCount);
        Assert.Equal(0, row.ChunkCount);
    }

    [Fact]
    public async Task GetAll_ConnectorOnlyRow_NotHardcodedZero()
    {
        var path = Path.Combine(_tempDir, "kb-only");
        Directory.CreateDirectory(path);
        var (kb, _) = await SeedKnowledgeBaseAsync("仅连接器库", path, documentCount: 5);

        // 该路径不属于 config.Sources —— 只以连接器投影出现在列表中
        var controller = CreateController(new ObsidianRagConfig { Sources = new List<SourceFolder>() });

        var ok = Assert.IsType<OkObjectResult>((await controller.GetAll()).Result);
        var sources = Assert.IsAssignableFrom<List<SourceDetail>>(ok.Value);
        var row = Assert.Single(sources);
        Assert.True(row.IsConnector);
        Assert.Equal(kb.Id, row.KnowledgeBaseId);
        Assert.Equal(5, row.FileCount);
    }

    [Fact]
    public async Task Get_SingleSource_AlsoUsesKnowledgeBaseCounts()
    {
        var path = Path.Combine(_tempDir, "single");
        Directory.CreateDirectory(path);
        await SeedKnowledgeBaseAsync("单源库", path, documentCount: 7);

        var config = new ObsidianRagConfig
        {
            Sources = new List<SourceFolder> { new() { Name = "单源", Path = path, Enabled = true } }
        };
        var controller = CreateController(config);

        var ok = Assert.IsType<OkObjectResult>((await controller.Get("单源")).Result);
        var detail = Assert.IsType<SourceDetail>(ok.Value);
        Assert.Equal(7, detail.FileCount);
    }

    [Fact]
    public async Task GetConnectorStatsSnapshot_SkipsKnowledgeBasesWithoutConnectors()
    {
        var path = Path.Combine(_tempDir, "withconn");
        Directory.CreateDirectory(path);
        var (kb, connector) = await SeedKnowledgeBaseAsync("有连接器", path, documentCount: 2);
        await _kbService.CreateKnowledgeBaseAsync("空库", null, "spec_lexical_only", null, null, null);

        var snapshot = await _kbService.GetConnectorStatsSnapshotAsync();

        var entry = Assert.Single(snapshot);
        Assert.Equal(kb.Id, entry.KnowledgeBaseId);
        Assert.Equal(connector.Id, entry.ConnectorId);
        Assert.Equal(2, entry.DocumentCount);
        Assert.True(entry.ChunkCount > 0);
    }
}

/// <summary>
/// G-5 门禁：SPA 静态页面深链必须在无 API Key 时返回页面而非 401 JSON。
/// 从路由定义反推路径清单，避免新页面再犯「白名单漏加」。
/// </summary>
public sealed class SpaRouteWhitelistParityGateTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ReferenceRAG.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("未找到仓库根目录");
    }

    [Fact]
    public void EveryStaticSpaRoute_IsWhitelistedInApiKeyMiddleware()
    {
        var root = FindRepoRoot();
        var routerPath = Path.Combine(root, "Business", "ReferenceRAG.Business", "wwwroot", "app", "router", "index.js");
        var middlewarePath = Path.Combine(root, "Host", "ReferenceRAG.ApiHost", "Middleware", "ApiKeyMiddleware.cs");
        Assert.True(File.Exists(routerPath), routerPath);
        Assert.True(File.Exists(middlewarePath), middlewarePath);

        var router = File.ReadAllText(routerPath);
        var middleware = File.ReadAllText(middlewarePath);

        // 仅取主布局 children 下的静态叶子路由（path: 'xxx'），忽略 '/' 与动态 ':id' 段。
        var routes = Regex.Matches(router, @"\bpath:\s*'([^'/:*]+)'\s*,")
            .Select(m => m.Groups[1].Value)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.NotEmpty(routes);

        var whitelistBlock = Regex.Match(
            middleware,
            @"WhitelistedSpaPageRoutes\s*=\s*new\([^)]*\)\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);
        Assert.True(whitelistBlock.Success, "未找到 WhitelistedSpaPageRoutes 声明");
        var whitelisted = Regex.Matches(whitelistBlock.Groups["body"].Value, "\"(/[^\"]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = routes.Where(r => !whitelisted.Contains("/" + r)).ToList();
        Assert.True(
            missing.Count == 0,
            "以下 SPA 路由未加入 ApiKeyMiddleware 白名单，深链刷新会返回 401 JSON：" + string.Join(", ", missing));
    }
}
