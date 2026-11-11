using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class KnowledgeBasePathsAndFilterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalogStore;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly KnowledgeBaseService _kbService;
    private readonly KnowledgeBaseSearchService _searchService;

    public KnowledgeBasePathsAndFilterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ReferenceRAG_PathFilterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var catalogDb = Path.Combine(_tempDir, "system", "catalog.db");
        _catalogStore = new CatalogStore(catalogDb);
        _catalogStore.InitializeAsync().GetAwaiter().GetResult();

        _storageManager = new KnowledgeBaseStorageManager(_tempDir);
        _kbService = new KnowledgeBaseService(_catalogStore, _storageManager, modelRouter: null, logger: NullLogger<KnowledgeBaseService>.Instance);
        _searchService = new KnowledgeBaseSearchService(_catalogStore, _storageManager, new RrfSearchFusionStrategy(), NullLogger<KnowledgeBaseSearchService>.Instance);
    }

    public void Dispose()
    {
        _storageManager.Dispose();
        _catalogStore.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task SqliteKnowledgeBaseStore_GetPathsAsync_ExtractsUniqueFolderHierarchy()
    {
        // 1. 创建纯词法知识库
        var kb = await _kbService.CreateKnowledgeBaseAsync("武侠测试库", "金庸小说全集", "spec_lexical_only");
        var connTargetPath = Path.Combine(_tempDir, "corpus", "jinyong");
        Directory.CreateDirectory(connTargetPath);

        var connector = await _kbService.AddConnectorAsync(kb.Id, "Folder", connTargetPath, "*.md");

        // 2. 插入不同目录下的文档
        await using (var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadWrite))
        {
            await lease.Store.CommitDocumentAsync(new IndexBatch(
                KnowledgeBaseId: kb.Id,
                ExpectedIndexEpoch: 1,
                ExpectedDocumentVersion: 1,
                ConnectorId: connector.Id,
                SourceKey: "三联版/射雕英雄传.md",
                Locator: Path.Combine(connTargetPath, "三联版", "射雕英雄传.md"),
                SourceRevision: "rev1",
                ContentHash: "hash1",
                Chunks: new[] { new ChunkData("c1", 0, "郭靖学习降龙十八掌，掌力雄浑无匹。", 10) }
            ));

            await lease.Store.CommitDocumentAsync(new IndexBatch(
                KnowledgeBaseId: kb.Id,
                ExpectedIndexEpoch: 1,
                ExpectedDocumentVersion: 1,
                ConnectorId: connector.Id,
                SourceKey: "新修版/天龙八部.md",
                Locator: Path.Combine(connTargetPath, "新修版", "天龙八部.md"),
                SourceRevision: "rev2",
                ContentHash: "hash2",
                Chunks: new[] { new ChunkData("c2", 0, "段誉练习六脉神剑，剑气纵横开阖。", 10) }
            ));

            await lease.Store.CommitDocumentAsync(new IndexBatch(
                KnowledgeBaseId: kb.Id,
                ExpectedIndexEpoch: 1,
                ExpectedDocumentVersion: 1,
                ConnectorId: connector.Id,
                SourceKey: "旧版/神雕侠侣.md",
                Locator: Path.Combine(connTargetPath, "旧版", "神雕侠侣.md"),
                SourceRevision: "rev3",
                ContentHash: "hash3",
                Chunks: new[] { new ChunkData("c3", 0, "杨过与小龙女在活死人墓中修炼玉女心经。", 10) }
            ));

            await lease.Store.CommitDocumentAsync(new IndexBatch(
                KnowledgeBaseId: kb.Id,
                ExpectedIndexEpoch: 1,
                ExpectedDocumentVersion: 1,
                ConnectorId: connector.Id,
                SourceKey: "readme.md",
                Locator: Path.Combine(connTargetPath, "readme.md"),
                SourceRevision: "rev4",
                ContentHash: "hash4",
                Chunks: new[] { new ChunkData("c4", 0, "金庸全集目录索引说明文件。", 10) }
            ));

            // 3. 调用 GetPathsAsync 并校验
            var paths = await lease.Store.GetPathsAsync(new[] { connector });
            Assert.Single(paths);

            var pathInfo = paths[0];
            Assert.Equal("jinyong", pathInfo.Name);
            Assert.Equal(connTargetPath, pathInfo.RootPath);
            Assert.Contains("三联版", pathInfo.Folders);
            Assert.Contains("新修版", pathInfo.Folders);
            Assert.Contains("旧版", pathInfo.Folders);
            Assert.Equal(3, pathInfo.Folders.Count);
        }
    }

    [Fact]
    public async Task KnowledgeBaseSearch_FolderPreFiltering_ZeroLeakage()
    {
        var kb = await _kbService.CreateKnowledgeBaseAsync("武侠测试库2", "降龙十八掌检索测试", "spec_lexical_only");
        var connTargetPath = Path.Combine(_tempDir, "corpus", "jinyong2");
        Directory.CreateDirectory(connTargetPath);

        var connector = await _kbService.AddConnectorAsync(kb.Id, "Folder", connTargetPath, "*.md");

        await using (var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadWrite))
        {
            // 在三联版中插入包含降龙十八掌的文档
            await lease.Store.CommitDocumentAsync(new IndexBatch(
                KnowledgeBaseId: kb.Id,
                ExpectedIndexEpoch: 1,
                ExpectedDocumentVersion: 1,
                ConnectorId: connector.Id,
                SourceKey: "三联版/射雕英雄传.md",
                Locator: Path.Combine(connTargetPath, "三联版", "射雕英雄传.md"),
                SourceRevision: "rev1",
                ContentHash: "hash1",
                Chunks: new[] { new ChunkData("c1_sanlian", 0, "三联版郭靖学习降龙十八掌，亢龙有悔掌法威猛刚猛。", 10) }
            ));

            // 在新修版中也插入包含降龙十八掌的文档
            await lease.Store.CommitDocumentAsync(new IndexBatch(
                KnowledgeBaseId: kb.Id,
                ExpectedIndexEpoch: 1,
                ExpectedDocumentVersion: 1,
                ConnectorId: connector.Id,
                SourceKey: "新修版/天龙八部.md",
                Locator: Path.Combine(connTargetPath, "新修版", "天龙八部.md"),
                SourceRevision: "rev2",
                ContentHash: "hash2",
                Chunks: new[] { new ChunkData("c2_xinxiu", 0, "新修版萧峰施展降龙十八掌，威震聚贤庄豪杰。", 10) }
            ));
        }

        // 1. 无路径过滤检索：应召回两本小说中的降龙十八掌
        var allRes = await _searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "降龙十八掌",
            KnowledgeBaseIds: new[] { kb.Id },
            TopK: 10,
            UseLexicalTokenizer: true
        ));
        Assert.Equal(2, allRes.Items.Count);

        // 2. 指定 Folders: ["三联版"] 前置过滤：仅召回三联版，新修版 100% 隔离
        var sanlianRes = await _searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "降龙十八掌",
            KnowledgeBaseIds: new[] { kb.Id },
            TopK: 10,
            UseLexicalTokenizer: true,
            Folders: new[] { "三联版" }
        ));
        Assert.Single(sanlianRes.Items);
        Assert.Equal("c1_sanlian", sanlianRes.Items[0].ChunkId);
        Assert.StartsWith("三联版", sanlianRes.Items[0].SourceKey);

        // 3. 指定 Folders: ["新修版"] 前置过滤：仅召回新修版，三联版 100% 隔离
        var xinxiuRes = await _searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "降龙十八掌",
            KnowledgeBaseIds: new[] { kb.Id },
            TopK: 10,
            UseLexicalTokenizer: true,
            Folders: new[] { "新修版" }
        ));
        Assert.Single(xinxiuRes.Items);
        Assert.Equal("c2_xinxiu", xinxiuRes.Items[0].ChunkId);
        Assert.StartsWith("新修版", xinxiuRes.Items[0].SourceKey);

        // 4. 指定不存在的路径：安全返回 0 条匹配，不抛异常
        var nonExistentRes = await _searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "降龙十八掌",
            KnowledgeBaseIds: new[] { kb.Id },
            TopK: 10,
            UseLexicalTokenizer: true,
            Folders: new[] { "虚假不存在目录" }
        ));
        Assert.Empty(nonExistentRes.Items);
    }

    [Fact]
    public async Task KnowledgeBasesController_GetPaths_And_SourcesController_KbRoute_Integration()
    {
        var kb = await _kbService.CreateKnowledgeBaseAsync("路由测试库", "路径路由与控制器验证", "spec_lexical_only");
        var connTargetPath = Path.Combine(_tempDir, "corpus", "routing");
        Directory.CreateDirectory(connTargetPath);

        var connector = await _kbService.AddConnectorAsync(kb.Id, "Folder", connTargetPath, "*.md");

        await using (var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadWrite))
        {
            await lease.Store.CommitDocumentAsync(new IndexBatch(
                KnowledgeBaseId: kb.Id,
                ExpectedIndexEpoch: 1,
                ExpectedDocumentVersion: 1,
                ConnectorId: connector.Id,
                SourceKey: "sub_dir/doc.md",
                Locator: Path.Combine(connTargetPath, "sub_dir", "doc.md"),
                SourceRevision: "rev1",
                ContentHash: "hash1",
                Chunks: new[] { new ChunkData("c1", 0, "测试文档内容", 5) }
            ));
        }

        // 1. 验证 KnowledgeBasesController 的单库和批量 paths 端点
        var kbController = new KnowledgeBasesController(_kbService, NullLogger<KnowledgeBasesController>.Instance);

        var singleRes = await kbController.GetPaths(kb.Id, CancellationToken.None);
        var okSingle = Assert.IsType<OkObjectResult>(singleRes.Result);
        var pathsSingle = Assert.IsType<PathsResponse>(okSingle.Value);
        Assert.Single(pathsSingle.Sources);
        Assert.Equal("routing", pathsSingle.Sources[0].Name);
        Assert.Contains("sub_dir", pathsSingle.Sources[0].Folders);

        var batchRes = await kbController.GetBatchPaths(new GetKnowledgeBasePathsRequest(new[] { kb.Id }), CancellationToken.None);
        var okBatch = Assert.IsType<OkObjectResult>(batchRes.Result);
        var pathsBatch = Assert.IsType<PathsResponse>(okBatch.Value);
        Assert.Single(pathsBatch.Sources);
        Assert.Contains("sub_dir", pathsBatch.Sources[0].Folders);

        // 2. 验证 SourcesController 传入 kbId 时的自动路由代理
        var mockVectorStore = new Mock<IVectorStore>();
        mockVectorStore.Setup(v => v.GetAllFilesAsync()).ReturnsAsync(Enumerable.Empty<FileRecord>());

        var sourcesController = new SourcesController(
            new ConfigManager(new ObsidianRagConfig()),
            mockVectorStore.Object,
            pipeline: null!,
            fileMonitor: null!,
            NullLogger<SourcesController>.Instance,
            catalogStore: _catalogStore,
            kbService: _kbService);

        var delegatedRes = await sourcesController.GetPaths(enabledOnly: true, kbId: kb.Id);
        var okDelegated = Assert.IsType<OkObjectResult>(delegatedRes.Result);
        var pathsDelegated = Assert.IsType<PathsResponse>(okDelegated.Value);
        Assert.Single(pathsDelegated.Sources);
        Assert.Equal("routing", pathsDelegated.Sources[0].Name);
        Assert.Contains("sub_dir", pathsDelegated.Sources[0].Folders);

        // 3. 验证未传递 kbId 时，默认自动聚合所有已启用知识库的路由路径
        var defaultAllRes = await sourcesController.GetPaths(enabledOnly: true);
        var okDefaultAll = Assert.IsType<OkObjectResult>(defaultAllRes.Result);
        var pathsDefaultAll = Assert.IsType<PathsResponse>(okDefaultAll.Value);
        Assert.NotEmpty(pathsDefaultAll.Sources);
        Assert.Contains(pathsDefaultAll.Sources, s => s.Name == "routing");

        // 4. 验证 GetBatchPaths 传入空数组时，自动聚合所有已启用知识库的路由路径
        var batchEmptyRes = await kbController.GetBatchPaths(new GetKnowledgeBasePathsRequest(Array.Empty<string>()), CancellationToken.None);
        var okBatchEmpty = Assert.IsType<OkObjectResult>(batchEmptyRes.Result);
        var pathsBatchEmpty = Assert.IsType<PathsResponse>(okBatchEmpty.Value);
        Assert.NotEmpty(pathsBatchEmpty.Sources);

        // 5. 验证 KnowledgeBaseSearchService 检索时 KnowledgeBaseIds 为空时，自动检索所有已启用知识库
        var allKbSearchRes = await _searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "测试文档内容",
            KnowledgeBaseIds: Array.Empty<string>(),
            TopK: 10,
            UseLexicalTokenizer: true
        ));
        Assert.NotEmpty(allKbSearchRes.Items);
        Assert.Contains(allKbSearchRes.Items, i => i.ChunkId == "c1");
    }
}
