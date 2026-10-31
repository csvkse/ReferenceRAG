using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class KnowledgeBasesControllerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalogStore;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly KnowledgeBaseService _kbService;
    private readonly KnowledgeBaseSearchService _searchService;

    public KnowledgeBasesControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ReferenceRAG_HostTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task KnowledgeBasesController_E2E_Workflow()
    {
        var controller = new KnowledgeBasesController(_kbService, NullLogger<KnowledgeBasesController>.Instance);

        // 1. 创建知识库
        var createResult = await controller.CreateKnowledgeBase(
            new CreateKnowledgeBaseRequest("Engineering Docs", "Internal Documentation"),
            CancellationToken.None);

        var createdAtAction = Assert.IsType<CreatedAtActionResult>(createResult.Result);
        var kb = Assert.IsType<KnowledgeBaseDefinition>(createdAtAction.Value);
        Assert.Equal("Engineering Docs", kb.Name);
        Assert.NotEmpty(kb.Id);

        // 2. 查询列表
        var listResult = await controller.ListKnowledgeBases(CancellationToken.None);
        var okList = Assert.IsType<OkObjectResult>(listResult.Result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<KnowledgeBaseDefinition>>(okList.Value);
        Assert.Single(list);

        // 3. 添加连接器
        var addConnResult = await controller.AddConnector(
            kb.Id,
            new AddConnectorRequest("Folder", _tempDir, "*.md"),
            CancellationToken.None);

        var okConn = Assert.IsType<OkObjectResult>(addConnResult.Result);
        var conn = Assert.IsType<ConnectorDefinition>(okConn.Value);
        Assert.Equal(kb.Id, conn.KnowledgeBaseId);

        // 4. 查询连接器列表
        var connsResult = await controller.GetConnectors(kb.Id, CancellationToken.None);
        var okConns = Assert.IsType<OkObjectResult>(connsResult.Result);
        var conns = Assert.IsAssignableFrom<IReadOnlyList<ConnectorDefinition>>(okConns.Value);
        Assert.Single(conns);

        // 5. 触发同步任务
        var triggerResult = await controller.TriggerSyncJob(
            kb.Id,
            new TriggerJobRequest(conn.Id),
            CancellationToken.None);

        var acceptedResult = Assert.IsType<AcceptedResult>(triggerResult.Result);
        var job = Assert.IsType<JobDefinition>(acceptedResult.Value);
        Assert.Equal(kb.Id, job.KnowledgeBaseId);
        Assert.Equal("sync", job.Operation);

        // 6. 验证 JobsController
        var jobsController = new JobsController(_kbService);
        var getJobResult = await jobsController.GetJob(job.Id, CancellationToken.None);
        var okJob = Assert.IsType<OkObjectResult>(getJobResult.Result);
        var retrievedJob = Assert.IsType<JobDefinition>(okJob.Value);
        Assert.Equal(job.Id, retrievedJob.Id);

        // 7. 验证 KnowledgeBaseSearchController
        var searchController = new KnowledgeBaseSearchController(_searchService);
        var searchResult = await searchController.Search(
            new CrossKnowledgeBaseSearchRequest("Architecture", new[] { kb.Id }, TopK: 5),
            CancellationToken.None);

        var okSearch = Assert.IsType<OkObjectResult>(searchResult.Result);
        var response = Assert.IsType<CrossKnowledgeBaseSearchResponse>(okSearch.Value);
        Assert.NotNull(response);
        Assert.Contains(kb.Id, response.SuccessfulKnowledgeBaseIds);

        // 8. 删除知识库
        var deleteResult = await controller.DeleteKnowledgeBase(kb.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteResult);
    }

    [Fact]
    public async Task KnowledgeBasesController_WithCustomEmbeddingSpec_PersistsAndBindsCorrectly()
    {
        var controller = new KnowledgeBasesController(_kbService, NullLogger<KnowledgeBasesController>.Instance);

        // 1. 验证默认 EmbeddingSpec 列表包含预置规范
        var listSpecsResult = await controller.ListEmbeddingSpecs(CancellationToken.None);
        var okSpecs = Assert.IsType<OkObjectResult>(listSpecsResult.Result);
        var specs = Assert.IsAssignableFrom<IReadOnlyList<EmbeddingSpec>>(okSpecs.Value);
        Assert.Contains(specs, s => s.ModelName == "BAAI/bge-m3" && s.Dimension == 1024);
        Assert.Contains(specs, s => s.ModelName == "text-embedding-3-small" && s.Dimension == 1536);

        // 2. 创建绑定自定义线上模型的知识库
        var customSpecDto = new EmbeddingSpecDto(
            Id: "spec_siliconflow_bge_large",
            ModelName: "BAAI/bge-large-zh-v1.5",
            Dimension: 1024,
            Metric: "cosine",
            QueryPrefix: "query: ",
            MaxTokens: 512
        );

        var createResult = await controller.CreateKnowledgeBase(
            new CreateKnowledgeBaseRequest(
                Name: "Online Custom KB",
                Description: "Bound to SiliconFlow embedding",
                EmbeddingSpecId: null,
                EmbeddingSpec: customSpecDto
            ),
            CancellationToken.None);

        var createdAtAction = Assert.IsType<CreatedAtActionResult>(createResult.Result);
        var kb = Assert.IsType<KnowledgeBaseDefinition>(createdAtAction.Value);
        Assert.Equal("spec_siliconflow_bge_large", kb.EmbeddingSpecId);

        // 3. 验证该 Spec 已被持久化在 CatalogStore 中
        var retrievedSpec = await _catalogStore.GetEmbeddingSpecAsync("spec_siliconflow_bge_large");
        Assert.NotNull(retrievedSpec);
        Assert.Equal("BAAI/bge-large-zh-v1.5", retrievedSpec.ModelName);
        Assert.Equal(1024, retrievedSpec.Dimension);
        Assert.Equal("query: ", retrievedSpec.QueryPrefix);
    }
}
