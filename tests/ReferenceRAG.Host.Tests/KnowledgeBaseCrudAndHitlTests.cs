using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Service.Controllers;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public class KnowledgeBaseCrudAndHitlTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalogStore;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly KnowledgeBaseService _kbService;
    private readonly KnowledgeBasesController _kbController;
    private readonly GraphController _graphController;

    public KnowledgeBaseCrudAndHitlTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ReferenceRAG_CrudTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var catalogDb = Path.Combine(_tempDir, "system", "catalog.db");
        _catalogStore = new CatalogStore(catalogDb);
        _catalogStore.InitializeAsync().GetAwaiter().GetResult();

        _storageManager = new KnowledgeBaseStorageManager(_tempDir);
        _kbService = new KnowledgeBaseService(_catalogStore, _storageManager, modelRouter: null, logger: NullLogger<KnowledgeBaseService>.Instance);
        _kbController = new KnowledgeBasesController(_kbService, NullLogger<KnowledgeBasesController>.Instance);
        _graphController = new GraphController(
            graphStore: null!,
            vectorStore: null!,
            graphIndexingService: null!,
            configManager: null!,
            logger: NullLogger<GraphController>.Instance,
            kbStorageManager: _storageManager,
            catalogStore: _catalogStore
        );
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
    public async Task KnowledgeBase_UpdateAndToggle_WorksCorrectly()
    {
        // 1. 创建知识库
        var createRes = await _kbController.CreateKnowledgeBase(
            new CreateKnowledgeBaseRequest("Original KB", "Original Description"),
            CancellationToken.None);
        var created = Assert.IsType<KnowledgeBaseDefinition>(Assert.IsType<CreatedAtActionResult>(createRes.Result).Value);

        // 2. 更新元数据
        var updateRes = await _kbController.UpdateKnowledgeBase(
            created.Id,
            new UpdateKnowledgeBaseRequest(Name: "Renamed KB", Description: "Updated Description"),
            CancellationToken.None);
        var updated = Assert.IsType<KnowledgeBaseDefinition>(Assert.IsType<OkObjectResult>(updateRes.Result).Value);
        Assert.Equal("Renamed KB", updated.Name);
        Assert.Equal("Updated Description", updated.Description);
        Assert.True(updated.DesiredConfigRevision > created.DesiredConfigRevision);

        // 3. 启停切换
        var toggleRes = await _kbController.ToggleKnowledgeBase(
            created.Id,
            new ToggleKnowledgeBaseRequest(false),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(toggleRes);

        var retrieved = await _kbController.GetKnowledgeBase(created.Id, CancellationToken.None);
        var kb = Assert.IsType<KnowledgeBaseDefinition>(Assert.IsType<OkObjectResult>(retrieved.Result).Value);
        Assert.False(kb.Enabled);
    }

    [Fact]
    public async Task Connector_Update_WorksCorrectly()
    {
        // 1. 创建知识库与连接器
        var createRes = await _kbController.CreateKnowledgeBase(
            new CreateKnowledgeBaseRequest("Connector KB", "Desc"),
            CancellationToken.None);
        var created = Assert.IsType<KnowledgeBaseDefinition>(Assert.IsType<CreatedAtActionResult>(createRes.Result).Value);

        var addConnRes = await _kbController.AddConnector(
            created.Id,
            new AddConnectorRequest("Folder", _tempDir, "*.md"),
            CancellationToken.None);
        var conn = Assert.IsType<ConnectorDefinition>(Assert.IsType<OkObjectResult>(addConnRes.Result).Value);

        // 2. 更新连接器通配符与路径
        var updateRes = await _kbController.UpdateConnector(
            created.Id,
            conn.Id,
            new UpdateConnectorRequest(TargetPath: _tempDir, FilePattern: "*.md,*.txt,*.pdf", Enabled: false),
            CancellationToken.None);

        var updated = Assert.IsType<ConnectorDefinition>(Assert.IsType<OkObjectResult>(updateRes.Result).Value);
        Assert.Equal("*.md,*.txt,*.pdf", updated.FilePattern);
        Assert.False(updated.Enabled);
        Assert.True(updated.ConfigRevision > conn.ConfigRevision);
    }

    [Fact]
    public async Task EmbeddingSpecs_CRUD_WorksCorrectly()
    {
        var spec = new EmbeddingSpec(
            Id: "custom-spec-test",
            ModelName: "BAAI/bge-m3",
            Dimension: 1024,
            Metric: "cosine",
            QueryPrefix: "query: ",
            MaxTokens: 8192
        );

        // Create
        var createRes = await _kbController.CreateEmbeddingSpec(spec, CancellationToken.None);
        var created = Assert.IsType<EmbeddingSpec>(Assert.IsType<OkObjectResult>(createRes.Result).Value);
        Assert.Equal("custom-spec-test", created.Id);

        // Update
        var updateRes = await _kbController.UpdateEmbeddingSpec(
            created.Id,
            created with { MaxTokens = 4096 },
            CancellationToken.None);
        var updated = Assert.IsType<EmbeddingSpec>(Assert.IsType<OkObjectResult>(updateRes.Result).Value);
        Assert.Equal(4096, updated.MaxTokens);

        // Delete
        var delRes = await _kbController.DeleteEmbeddingSpec(created.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(delRes);

        var retrieved = await _catalogStore.GetEmbeddingSpecAsync(created.Id);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task Chunk_HITL_CRUD_Workflow()
    {
        // 1. 创建知识库
        var createRes = await _kbController.CreateKnowledgeBase(
            new CreateKnowledgeBaseRequest("Chunk Test KB"),
            CancellationToken.None);
        var kb = Assert.IsType<KnowledgeBaseDefinition>(Assert.IsType<CreatedAtActionResult>(createRes.Result).Value);

        // 2. 手动创建切片
        var createChunkRes = await _kbController.CreateChunk(
            kb.Id,
            new CreateChunkRequest(DocumentId: null, Text: "乔峰是金庸小说天龙八部中的主角"),
            CancellationToken.None);
        var createdChunk = Assert.IsType<KnowledgeBaseChunkRecord>(Assert.IsType<CreatedAtActionResult>(createChunkRes.Result).Value);
        Assert.Equal("乔峰是金庸小说天龙八部中的主角", createdChunk.Text);
        Assert.True(createdChunk.Enabled);

        // 3. 查询切片列表
        var getChunksRes = await _kbController.GetChunks(kb.Id, null, page: 1, pageSize: 10, search: "乔峰", enabledOnly: null, CancellationToken.None);
        var okChunks = Assert.IsType<OkObjectResult>(getChunksRes);
        dynamic chunkData = okChunks.Value!;
        Assert.Equal(1, (int)chunkData.total);

        // 4. 编辑切片文本 (HITL 人工纠错)
        var updateChunkRes = await _kbController.UpdateChunk(
            kb.Id,
            createdChunk.Id,
            new UpdateChunkRequest(Text: "萧峰（乔峰）是丐帮帮主兼契丹南院大王", RecomputeEmbeddingImmediately: false),
            CancellationToken.None);
        var updatedChunk = Assert.IsType<KnowledgeBaseChunkRecord>(Assert.IsType<OkObjectResult>(updateChunkRes.Result).Value);
        Assert.Equal("萧峰（乔峰）是丐帮帮主兼契丹南院大王", updatedChunk.Text);

        // 5. 翻转切片启用状态
        var toggleRes = await _kbController.ToggleChunk(
            kb.Id,
            createdChunk.Id,
            new ToggleChunkRequest(false),
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(toggleRes);

        var getChunkRes = await _kbController.GetChunk(kb.Id, createdChunk.Id, CancellationToken.None);
        var retrievedChunk = Assert.IsType<KnowledgeBaseChunkRecord>(Assert.IsType<OkObjectResult>(getChunkRes.Result).Value);
        Assert.False(retrievedChunk.Enabled);

        // 6. 验证 enabledOnly 过滤生效
        var enabledFilterRes = await _kbController.GetChunks(kb.Id, null, page: 1, pageSize: 10, search: null, enabledOnly: true, CancellationToken.None);
        dynamic enabledData = ((OkObjectResult)enabledFilterRes).Value!;
        Assert.Equal(0, (int)enabledData.total);

        // 7. 删除切片
        var deleteRes = await _kbController.DeleteChunk(kb.Id, createdChunk.Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(deleteRes);

        var deletedChunkRes = await _kbController.GetChunk(kb.Id, createdChunk.Id, CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(deletedChunkRes.Result);
    }

    [Fact]
    public async Task Graph_ManualCuration_CRUD_Workflow()
    {
        // 1. 创建知识库
        var createRes = await _kbController.CreateKnowledgeBase(
            new CreateKnowledgeBaseRequest("Graph Curation KB"),
            CancellationToken.None);
        var kb = Assert.IsType<KnowledgeBaseDefinition>(Assert.IsType<CreatedAtActionResult>(createRes.Result).Value);

        // 2. 手动创建实体节点
        var createNodeRes = await _graphController.CreateOrUpdateNode(
            new CreateOrUpdateGraphNodeDto(Id: "entity_qiaofeng", Title: "乔峰", Type: "person", Metadata: new() { ["alias"] = "萧峰" }),
            kbId: kb.Id,
            CancellationToken.None);
        var node = Assert.IsType<GraphNode>(Assert.IsType<OkObjectResult>(createNodeRes).Value);
        Assert.Equal("乔峰", node.Title);

        // 3. 读取节点
        var getNodeRes = await _graphController.GetNode(nodeId: "entity_qiaofeng", kbId: kb.Id);
        var retrievedNode = Assert.IsType<GraphNode>(Assert.IsType<OkObjectResult>(getNodeRes.Result).Value);
        Assert.Equal("乔峰", retrievedNode.Title);

        // 4. 创建第二节点并建立关系边
        await _graphController.CreateOrUpdateNode(
            new CreateOrUpdateGraphNodeDto(Id: "entity_gaibang", Title: "丐帮", Type: "sect", Metadata: null),
            kbId: kb.Id,
            CancellationToken.None);

        var createEdgeRes = await _graphController.CreateEdge(
            new CreateOrDeleteGraphEdgeDto(FromId: "entity_qiaofeng", ToId: "entity_gaibang", Type: "leader_of"),
            kbId: kb.Id,
            CancellationToken.None);
        var edge = Assert.IsType<GraphEdge>(Assert.IsType<OkObjectResult>(createEdgeRes).Value);
        Assert.Equal("leader_of", edge.Type);

        // 5. 验证邻居查询返回边
        var neighborsRes = await _graphController.GetNeighbors(nodeId: "entity_qiaofeng", depth: 1, kbId: kb.Id);
        var traversal = Assert.IsType<GraphTraversalResult>(Assert.IsType<OkObjectResult>(neighborsRes.Result).Value);
        Assert.Contains(traversal.Edges, e => e.FromId == "entity_qiaofeng" && e.ToId == "entity_gaibang");

        // 6. 删除关系边
        var delEdgeRes = await _graphController.DeleteEdge(fromId: "entity_qiaofeng", toId: "entity_gaibang", type: "leader_of", kbId: kb.Id);
        Assert.IsType<OkObjectResult>(delEdgeRes);

        var neighborsAfterDel = await _graphController.GetNeighbors(nodeId: "entity_qiaofeng", depth: 1, kbId: kb.Id);
        var traversalAfter = Assert.IsType<GraphTraversalResult>(Assert.IsType<OkObjectResult>(neighborsAfterDel.Result).Value);
        Assert.DoesNotContain(traversalAfter.Edges, e => e.FromId == "entity_qiaofeng" && e.ToId == "entity_gaibang");

        // 7. 删除实体节点
        var delNodeRes = await _graphController.DeleteNode(nodeId: "entity_qiaofeng", kbId: kb.Id);
        Assert.IsType<NoContentResult>(delNodeRes);

        var getAfterDel = await _graphController.GetNode(nodeId: "entity_qiaofeng", kbId: kb.Id);
        Assert.IsType<NotFoundObjectResult>(getAfterDel.Result);
    }
}
