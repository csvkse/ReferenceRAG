using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
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
/// System One 在线认知服务、连接器级联物理清理、文档录入闭环与历史作业清理门禁测试
/// </summary>
public class SystemOneAndConnectorPurgeGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalogStore;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly KnowledgeBaseService _kbService;
    private readonly KnowledgeBasesController _kbController;
    private readonly JobsController _jobsController;

    public SystemOneAndConnectorPurgeGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ReferenceRAG_SysOneAndPurge_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var catalogDb = Path.Combine(_tempDir, "system", "catalog.db");
        _catalogStore = new CatalogStore(catalogDb);
        _catalogStore.InitializeAsync().GetAwaiter().GetResult();

        _storageManager = new KnowledgeBaseStorageManager(_tempDir);
        _kbService = new KnowledgeBaseService(_catalogStore, _storageManager, modelRouter: null, logger: NullLogger<KnowledgeBaseService>.Instance);
        _kbController = new KnowledgeBasesController(_kbService, NullLogger<KnowledgeBasesController>.Instance);
        _jobsController = new JobsController(_kbService);
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
    public async Task SettingsController_SystemOne_ApiKeyMaskingAndTestEndpoint_WorksCorrectly()
    {
        // 1. 初始化配置并包含真实的 SystemOne API Key
        var config = new ObsidianRagConfig
        {
            SystemOne = new SystemOneConfig
            {
                Enabled = true,
                BaseUrl = "https://mock.systemone.ai",
                ApiKey = "sk-super-secret-system-one-key",
                Model = "jev-test"
            }
        };

        var configManager = new ConfigManager(config, Path.Combine(_tempDir, "appsettings.json"));
        var settingsController = new SettingsController(
            configManager,
            Mock.Of<IModelManager>(),
            NullLogger<SettingsController>.Instance
        );

        // 2. 调用 Get 获取配置，必须被脱敏保护且标记 ApiKeyConfigured
        var getRes = settingsController.Get();
        var okGet = Assert.IsType<OkObjectResult>(getRes.Result);
        var maskedConfig = Assert.IsType<ObsidianRagConfig>(okGet.Value);

        Assert.Contains("****", maskedConfig.SystemOne.ApiKey);
        Assert.True(maskedConfig.SystemOne.ApiKeyConfigured);

        // 3. 测试连通性探测接口（探测异常时优雅返回非 500 的 JSON 诊断对象）
        var testRes = await settingsController.TestSystemOne(new TestSystemOneRequest
        {
            BaseUrl = "http://127.0.0.1:59999/unreachable",
            ApiKey = "sk-test",
            Model = "jev-v1",
            Query = "你好"
        }, CancellationToken.None);

        var okTest = Assert.IsType<OkObjectResult>(testRes);
        dynamic testResult = okTest.Value!;
        Assert.False((bool)testResult.success);
        Assert.NotNull(testResult.error);
    }

    [Fact]
    public async Task Connector_CascadePurge_RemovesDocumentsChunksAndResetsEpoch()
    {
        // 1. 创建知识库
        var createRes = await _kbController.CreateKnowledgeBase(
            new CreateKnowledgeBaseRequest("Cascade Purge KB", "Test KB"),
            CancellationToken.None);
        var kb = Assert.IsType<KnowledgeBaseDefinition>(Assert.IsType<CreatedAtActionResult>(createRes.Result).Value);

        // 2. 挂载真实目录连接器
        var connectorDir = Path.Combine(_tempDir, "docs_to_sync");
        Directory.CreateDirectory(connectorDir);
        File.WriteAllText(Path.Combine(connectorDir, "test.md"), "Hello connector purge");

        var addConnRes = await _kbController.AddConnector(
            kb.Id,
            new AddConnectorRequest("Directory", connectorDir, "*.md"),
            CancellationToken.None);
        var addedConn = Assert.IsType<ConnectorDefinition>(Assert.IsType<OkObjectResult>(addConnRes.Result).Value);

        // 3. 模拟同步作业写入切片与文档
        using (var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadWrite, CancellationToken.None))
        {
            var store = lease.Store;
            var batch = new IndexBatch(
                KnowledgeBaseId: kb.Id,
                ExpectedIndexEpoch: 1,
                ExpectedDocumentVersion: 1,
                ConnectorId: addedConn.Id,
                SourceKey: "test.md",
                Locator: Path.Combine(connectorDir, "test.md"),
                SourceRevision: "rev-1",
                ContentHash: "hash-1",
                Chunks: new List<ChunkData>
                {
                    new ChunkData("chk-1", 0, "Hello connector purge chunk 1", 30),
                    new ChunkData("chk-2", 1, "Hello connector purge chunk 2", 30)
                }
            );
            await store.CommitDocumentAsync(batch);
        }

        // 4. 查询连接器统计，验证包含真实的 DocumentCount 与 ChunkCount
        var connsAfterSyncRes = await _kbController.GetConnectors(kb.Id, CancellationToken.None);
        var connsList = Assert.IsAssignableFrom<IReadOnlyList<ConnectorDetailDto>>(Assert.IsType<OkObjectResult>(connsAfterSyncRes.Result).Value);
        var connDto = connsList.First(c => c.Id == addedConn.Id);

        Assert.Equal(1, connDto.DocumentCount);
        Assert.Equal(2, connDto.ChunkCount);
        Assert.True(connDto.IsReachable);
        Assert.Equal(ConnectorReachabilityState.Reachable, connDto.Reachability);

        // 5. 级联物理删除连接器
        var delRes = await _kbController.DeleteConnector(kb.Id, addedConn.Id, cascade: true, CancellationToken.None);
        Assert.IsType<NoContentResult>(delRes);

        // 6. 验证知识库内部存储完全被清空且 index_epoch 递增
        using (var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadOnly, CancellationToken.None))
        {
            var store = lease.Store;
            var (docs, _) = await store.GetDocumentsAsync(page: 1, pageSize: 100);
            Assert.Empty(docs);

            var (chunks, _) = await store.GetChunksAsync(documentId: null, page: 1, pageSize: 100);
            Assert.Empty(chunks);

            var manifest = await store.GetManifestAsync();
            Assert.True(manifest.IndexEpoch > 1, "IndexEpoch 应该递增以使缓存失效");
        }
    }

    [Fact]
    public async Task ManualDocumentCreation_InHitlDrawer_ChunksAndIndexesDocument()
    {
        // 1. 创建知识库
        var createRes = await _kbController.CreateKnowledgeBase(
            new CreateKnowledgeBaseRequest("Manual Doc KB", "Test KB"),
            CancellationToken.None);
        var kb = Assert.IsType<KnowledgeBaseDefinition>(Assert.IsType<CreatedAtActionResult>(createRes.Result).Value);

        // 2. 手动单篇录入文档 (两段正文，触发多段分块)
        var content = "ReferenceRAG 是一款现代化企业级知识检索与大模型推理中枢。\n\n它具备完善的多模态与混合检索加速架构。";
        var createDocRes = await _kbController.CreateDocument(
            kb.Id,
            new CreateDocumentRequest(ConnectorId: null, Title: "QuickStart", Content: content, SourceKey: "manual/quickstart.md"),
            CancellationToken.None);

        var docCreated = Assert.IsType<CreatedResult>(createDocRes.Result);
        var docData = Assert.IsType<KnowledgeBaseDocumentRecord>(docCreated.Value);
        Assert.NotNull(docData.Id);
        Assert.Equal(2, docData.ChunkCount);

        // 3. 校验知识库中切片与词法搜索
        using var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadOnly, CancellationToken.None);
        var store = lease.Store;
        var (chunks, total) = await store.GetChunksAsync(documentId: null, page: 1, pageSize: 10);
        Assert.Equal(2, total);
        Assert.Contains("ReferenceRAG", chunks[0].Text);

        // 4. 校验词法全文检索能够召回刚录入的文档
        await store.EnsureLexicalIndexAsync(CancellationToken.None);
        var recallSnapshot = await store.RecallAsync(new RecallRequest(Query: "知识检索", TopK: 5, UseLexicalTokenizer: true));
        Assert.NotEmpty(recallSnapshot.Items);
    }

    [Fact]
    public async Task JobsController_DeleteAndPrune_CleansHistory()
    {
        // 1. 插入作业记录
        var kbId = "kb_job_test";
        var job1Id = "job_completed_old";
        var job2Id = "job_failed_old";
        var job3Id = "job_running_active";

        await _catalogStore.SaveJobAsync(new JobDefinition(
            Id: job1Id,
            Operation: "Sync",
            KnowledgeBaseId: kbId,
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Succeeded,
            Phase: OperationPhase.Publishing,
            TotalUnits: 10,
            ProcessedUnits: 10,
            IdempotencyKey: null,
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow.AddDays(-2),
            FinishedAt: DateTime.UtcNow.AddDays(-2)
        ));

        await _catalogStore.SaveJobAsync(new JobDefinition(
            Id: job2Id,
            Operation: "Sync",
            KnowledgeBaseId: kbId,
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Failed,
            Phase: OperationPhase.None,
            TotalUnits: 10,
            ProcessedUnits: 5,
            IdempotencyKey: null,
            ErrorMessage: "Timeout",
            CreatedAt: DateTime.UtcNow.AddDays(-2),
            FinishedAt: DateTime.UtcNow.AddDays(-2)
        ));

        await _catalogStore.SaveJobAsync(new JobDefinition(
            Id: job3Id,
            Operation: "Sync",
            KnowledgeBaseId: kbId,
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Running,
            Phase: OperationPhase.Scanning,
            TotalUnits: 10,
            ProcessedUnits: 0,
            IdempotencyKey: null,
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow,
            FinishedAt: null
        ));

        // 2. 单个删除测试
        var delRes = await _jobsController.DeleteJob(job2Id, CancellationToken.None);
        Assert.IsType<NoContentResult>(delRes);
        var job2AfterDel = await _catalogStore.GetJobAsync(job2Id);
        Assert.Null(job2AfterDel);

        // 3. 批量 Prune 清理测试 (清理 1 天前的已完成/失败作业)
        var pruneRes = await _jobsController.PruneJobs(kbId, daysOlderThan: 1, CancellationToken.None);
        var okPrune = Assert.IsType<OkObjectResult>(pruneRes);
        dynamic pruneData = okPrune.Value!;
        Assert.Equal(1, (int)pruneData.prunedCount);

        // 4. 验证 Running 作业不受影响
        var runningJob = await _catalogStore.GetJobAsync(job3Id);
        Assert.NotNull(runningJob);
        Assert.Equal(JobStatus.Running, runningJob.Status);
    }
}
