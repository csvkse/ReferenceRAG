using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Connectors;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Jobs;
using ReferenceRAG.Core.Features.KnowledgeBase.Profiles;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Runtime;
using ReferenceRAG.Core.Features.Search.Fusion;
using Moq;
using Xunit;

namespace ReferenceRAG.Tests;

public class KnowledgeBaseArchitectureGateTests : IDisposable
{
    private readonly string _tempDir;

    public KnowledgeBaseArchitectureGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ReferenceRAG_ArchTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    #region Gate G0: 核心存储契约、单库事务与代次栅栏

    [Fact]
    public async Task GateG0_CatalogStore_ShouldPersistDefinitions()
    {
        var dbPath = Path.Combine(_tempDir, "catalog.db");
        using var catalog = new CatalogStore(dbPath);
        await catalog.InitializeAsync();

        var kb = new KnowledgeBaseDefinition(
            Id: "kb_test_1",
            Name: "Test KB",
            Description: "Unit Test KnowledgeBase",
            Enabled: true,
            DesiredConfigRevision: 1,
            EffectiveConfigRevision: 1,
            EmbeddingSpecId: "bge-m3-v1",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow
        );

        await catalog.SaveKnowledgeBaseAsync(kb);
        var fetched = await catalog.GetKnowledgeBaseAsync("kb_test_1");
        Assert.NotNull(fetched);
        Assert.Equal("Test KB", fetched.Name);

        var connector = new ConnectorDefinition(
            Id: "conn_1",
            KnowledgeBaseId: "kb_test_1",
            Kind: "Folder",
            TargetPath: "C:/docs",
            FilePattern: "*.md",
            ConfigRevision: 1,
            Enabled: true,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow
        );

        await catalog.SaveConnectorAsync(connector);
        var connectors = await catalog.GetConnectorsByKnowledgeBaseAsync("kb_test_1");
        Assert.Single(connectors);
        Assert.Equal("conn_1", connectors[0].Id);
    }

    [Fact]
    public async Task GateG0_SqliteKnowledgeBaseStore_ShouldIncrementReadRevisionAndRejectExpiredFence()
    {
        var dbPath = Path.Combine(_tempDir, "kb_test", "kb.db");
        using var store = new SqliteKnowledgeBaseStore("kb_test", dbPath);
        await store.InitializeAsync();

        var manifest1 = await store.GetManifestAsync();
        Assert.Equal(1, manifest1.ReadRevision);
        Assert.Equal(1, manifest1.IndexEpoch);

        // 提交第一个文档
        var batch1 = new IndexBatch(
            KnowledgeBaseId: "kb_test",
            ExpectedIndexEpoch: 1,
            ExpectedDocumentVersion: 0,
            ConnectorId: "c1",
            SourceKey: "doc1.md",
            Locator: "/path/doc1.md",
            SourceRevision: "rev1",
            ContentHash: "hash1",
            Chunks: new[]
            {
                new ChunkData("chunk_1", 0, "ReferenceRAG is a high-performance local RAG engine", 12)
            }
        );

        var result1 = await store.CommitDocumentAsync(batch1);
        Assert.True(result1.Success);
        Assert.Equal(1, result1.PublishedDocumentVersion);
        Assert.Equal(2, result1.PublishedReadRevision);

        // 检索验证 FTS BM25
        var recall = await store.RecallAsync(new RecallRequest("ReferenceRAG high-performance", TopK: 5));
        Assert.Single(recall.Items);
        Assert.Equal("chunk_1", recall.Items[0].ChunkId);
        Assert.Equal(ScoreKind.BM25, recall.Items[0].ScoreKind);

        // 验证写入栅栏越界防护: 传入过期的 ExpectedIndexEpoch (999 != 1)
        var invalidBatch = batch1 with { ExpectedIndexEpoch = 999, SourceKey = "doc2.md" };
        var invalidResult = await store.CommitDocumentAsync(invalidBatch);
        Assert.False(invalidResult.Success);
        Assert.Contains("写入栅栏过期", invalidResult.ErrorMessage);
    }

    #endregion

    #region Gate G1: 知识库隔离租约、数据连接器、状态机与跨库扇出

    [Fact]
    public async Task GateG1_StorageManager_LeaseTracking_And_DeletionGuard()
    {
        using var storageManager = new KnowledgeBaseStorageManager(_tempDir);

        // 借用 Lease
        var lease1 = await storageManager.AcquireLeaseAsync("kb_isolated_1", StorageAccess.ReadOnly);
        Assert.NotNull(lease1);
        Assert.Equal("kb_isolated_1", lease1.KnowledgeBaseId);

        // 试图删除被借用的库，应被守卫拦截
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await storageManager.DeleteKnowledgeBaseAsync("kb_isolated_1");
        });

        // 归还 Lease
        await lease1.DisposeAsync();

        // 归还后可以安全删除
        await storageManager.DeleteKnowledgeBaseAsync("kb_isolated_1");
        var exists = await storageManager.ExistsAsync("kb_isolated_1");
        Assert.False(exists);
    }

    [Fact]
    public async Task GateG1_KnowledgeBaseSyncWorker_EndToEndSync()
    {
        var testDocsDir = Path.Combine(_tempDir, "source_docs");
        Directory.CreateDirectory(testDocsDir);
        File.WriteAllText(Path.Combine(testDocsDir, "guide.md"), "# Architecture Guide\nThis is a test guide for ReferenceRAG.");
        File.WriteAllText(Path.Combine(testDocsDir, "notes.txt"), "Important notes about multi-model lifecycle.");

        var catalogDb = Path.Combine(_tempDir, "catalog.db");
        using var catalog = new CatalogStore(catalogDb);
        await catalog.InitializeAsync();

        using var storageManager = new KnowledgeBaseStorageManager(_tempDir);

        var kb = new KnowledgeBaseDefinition(
            Id: "kb_sync_test",
            Name: "Sync Test KB",
            Description: null,
            Enabled: true,
            DesiredConfigRevision: 1,
            EffectiveConfigRevision: 1,
            EmbeddingSpecId: "default",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow
        );
        await catalog.SaveKnowledgeBaseAsync(kb);

        using var connector = new LocalFolderConnector("conn_folder", "kb_sync_test", testDocsDir, "*.md;*.txt");

        var job = new JobDefinition(
            Id: "job_sync_1",
            Operation: "sync",
            KnowledgeBaseId: "kb_sync_test",
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Queued,
            Phase: OperationPhase.None,
            TotalUnits: 0,
            ProcessedUnits: 0,
            IdempotencyKey: null,
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow,
            FinishedAt: null
        );

        var worker = new KnowledgeBaseSyncWorker(catalog, storageManager);
        var finalJob = await worker.ExecuteSyncAsync(job, connector);

        Assert.Equal(JobStatus.Succeeded, finalJob.Status);
        Assert.Equal(2, finalJob.ProcessedUnits);

        // 验证检索
        await using var lease = await storageManager.AcquireLeaseAsync("kb_sync_test", StorageAccess.ReadOnly);
        var recall = await lease.Store.RecallAsync(new RecallRequest("Architecture Guide"));
        Assert.NotEmpty(recall.Items);
        Assert.Contains(recall.Items, i => i.Text.Contains("Architecture Guide"));
    }

    [Fact]
    public async Task GateG1_CrossKnowledgeBaseSearch_WithRrfFusion()
    {
        var catalogDb = Path.Combine(_tempDir, "catalog.db");
        using var catalog = new CatalogStore(catalogDb);
        await catalog.InitializeAsync();

        using var storageManager = new KnowledgeBaseStorageManager(_tempDir);

        // 创建两个独立的知识库
        await catalog.SaveKnowledgeBaseAsync(new KnowledgeBaseDefinition(
            Id: "kb_a", Name: "KB A", Description: null, Enabled: true,
            DesiredConfigRevision: 1, EffectiveConfigRevision: 1, EmbeddingSpecId: "spec",
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow));

        await catalog.SaveKnowledgeBaseAsync(new KnowledgeBaseDefinition(
            Id: "kb_b", Name: "KB B", Description: null, Enabled: true,
            DesiredConfigRevision: 1, EffectiveConfigRevision: 1, EmbeddingSpecId: "spec",
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow));

        // 写入 KB A
        await using (var leaseA = await storageManager.AcquireLeaseAsync("kb_a", StorageAccess.ReadWrite))
        {
            await leaseA.Store.CommitDocumentAsync(new IndexBatch(
                KnowledgeBaseId: "kb_a", ExpectedIndexEpoch: 1, ExpectedDocumentVersion: 0,
                ConnectorId: "c_a", SourceKey: "a1.md", Locator: null, SourceRevision: null, ContentHash: "h_a",
                Chunks: new[] { new ChunkData("chunk_a1", 0, "Artificial intelligence model lifecycle and scheduling", 10) }));
        }

        // 写入 KB B
        await using (var leaseB = await storageManager.AcquireLeaseAsync("kb_b", StorageAccess.ReadWrite))
        {
            await leaseB.Store.CommitDocumentAsync(new IndexBatch(
                KnowledgeBaseId: "kb_b", ExpectedIndexEpoch: 1, ExpectedDocumentVersion: 0,
                ConnectorId: "c_b", SourceKey: "b1.md", Locator: null, SourceRevision: null, ContentHash: "h_b",
                Chunks: new[] { new ChunkData("chunk_b1", 0, "Artificial intelligence vector database isolation", 10) }));
        }

        var searchService = new KnowledgeBaseSearchService(catalog, storageManager, new RrfSearchFusionStrategy());

        var response = await searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "Artificial intelligence",
            KnowledgeBaseIds: new[] { "kb_a", "kb_b" },
            TopK: 5
        ));

        Assert.Equal(2, response.Items.Count);
        Assert.Equal(ScoreKind.RRF, response.ScoreKind);
        Assert.Equal(2, response.SuccessfulKnowledgeBaseIds.Count);
        Assert.Empty(response.FailedKnowledgeBaseIds);
    }

    #endregion

    #region Gate G2: 资源调度器、模型生命周期排空安全与路由租约

    [Fact]
    public async Task GateG2_ResourceScheduler_ShouldEnforceVramLedger()
    {
        // 模拟 4GB 总显存设备，512MB 安全余量 => 可用 3584 MB
        var scheduler = new ResourceScheduler(
            totalVramBytes: 4096L * 1024 * 1024,
            safetyMarginBytes: 512L * 1024 * 1024);

        var demand1 = new ResourceDemand(
            PeakLoadVramBytes: 2500L * 1024 * 1024,
            SteadyVramBytes: 2000L * 1024 * 1024,
            SystemRamBytes: 1024L * 1024 * 1024);

        // 首次预留 2500MB 应成功
        var reservation1 = await scheduler.ReserveAsync(demand1, new WorkloadContext("req_1"));
        Assert.NotNull(reservation1);
        Assert.Equal(2500L * 1024 * 1024, scheduler.AllocatedVramBytes);

        // 再次请求 2000MB (总需求 4500MB > 3584MB 可用) 应被预算账本拒绝
        var demand2 = new ResourceDemand(
            PeakLoadVramBytes: 2000L * 1024 * 1024,
            SteadyVramBytes: 1500L * 1024 * 1024,
            SystemRamBytes: 512L * 1024 * 1024);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await scheduler.ReserveAsync(demand2, new WorkloadContext("req_2"));
        });

        // 释放第一个预留
        await reservation1.DisposeAsync();
        Assert.Equal(0, scheduler.AllocatedVramBytes);

        // 释放后重新申请应成功
        var reservation2 = await scheduler.ReserveAsync(demand2, new WorkloadContext("req_3"));
        Assert.NotNull(reservation2);
        await reservation2.DisposeAsync();
    }

    [Fact]
    public async Task GateG2_SqliteKnowledgeBaseStore_VectorAndHybridSearch()
    {
        var dbPath = Path.Combine(_tempDir, "kb_vec_test", "kb.db");
        using var store = new SqliteKnowledgeBaseStore("kb_vec_test", dbPath);
        await store.InitializeAsync();

        // 构造正交与相似向量样本 (3维方便验证)
        var vecA = new float[] { 1.0f, 0.0f, 0.0f };
        var vecB = new float[] { 0.9f, 0.1f, 0.0f }; // 与 A 高度相似
        var vecC = new float[] { 0.0f, 1.0f, 0.0f }; // 与 A 正交

        var batch = new IndexBatch(
            KnowledgeBaseId: "kb_vec_test",
            ExpectedIndexEpoch: 1,
            ExpectedDocumentVersion: 0,
            ConnectorId: "conn_vec",
            SourceKey: "doc_vec.md",
            Locator: "/path/doc_vec.md",
            SourceRevision: "v1",
            ContentHash: "hash_vec",
            Chunks: new[]
            {
                new ChunkData("chunk_vec_1", 0, "Document chunk related to database clustering", 10),
                new ChunkData("chunk_vec_2", 1, "Document chunk related to database replication", 10),
                new ChunkData("chunk_vec_3", 2, "Document chunk related to frontend graphic design", 10)
            },
            Vectors: new[] { vecA, vecB, vecC },
            ProducerCohort: "cohort_test_v1"
        );

        var result = await store.CommitDocumentAsync(batch);
        Assert.True(result.Success);

        // 1. 纯向量检索: 查询与 vecA 相似的分块
        var queryVec = new float[] { 0.95f, 0.05f, 0.0f };
        var vecRecall = await store.RecallAsync(new RecallRequest(Query: "", TopK: 2, QueryVector: queryVec));
        Assert.Equal(2, vecRecall.Items.Count);
        Assert.Equal(ScoreKind.Cosine, vecRecall.Items[0].ScoreKind);
        Assert.Equal("chunk_vec_1", vecRecall.Items[0].ChunkId); // 最相似
        Assert.Equal("chunk_vec_2", vecRecall.Items[1].ChunkId); // 次相似

        // 2. 混合检索: 同时传入文本查询与向量查询 => 触发单库内部 RRF 混合融合
        var hybridRecall = await store.RecallAsync(new RecallRequest(
            Query: "database clustering",
            TopK: 2,
            QueryVector: queryVec));

        Assert.Equal(2, hybridRecall.Items.Count);
        Assert.Equal(ScoreKind.RRF, hybridRecall.Items[0].ScoreKind);
        Assert.Equal("chunk_vec_1", hybridRecall.Items[0].ChunkId); // 文本和向量均排名第 1
    }

    private sealed class MockDisposableAdapter : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public async Task GateG2_ModelRuntimeStateMachine_DrainTimeout_MustNotDisposeActiveAdapter()
    {
        var identity = new ModelExecutionIdentity(
            ModelId: "test_model",
            RemoteModelName: "test_model",
            ProviderId: "local",
            Backend: "test",
            Device: "cpu"
        );

        using var sm = new ModelRuntimeStateMachine<MockDisposableAdapter>("test_model", identity);

        var mockAdapter = new MockDisposableAdapter();
        await sm.EnsureLoadedAsync(() => Task.FromResult(mockAdapter));

        Assert.Equal(ModelRuntimeState.Ready, sm.State);

        // 借用租约
        var lease = sm.AcquireLease();
        Assert.Equal(1, sm.ActiveLeases);
        Assert.Equal(ModelRuntimeState.Inferring, sm.State);

        // 在有活跃借用未释放时请求卸载（超时设为 100ms）
        var unloadSuccess = await sm.UnloadAsync(TimeSpan.FromMilliseconds(100));

        // 关键安全验证:
        // 1. 卸载必须返回 false (排空超时中止)
        Assert.False(unloadSuccess);
        // 2. 状态恢复为 Ready
        Assert.Equal(ModelRuntimeState.Ready, sm.State);
        // 3. 绝不销毁仍在借用中的原生适配器指针！
        Assert.False(mockAdapter.IsDisposed);

        // 归还借用
        await lease.DisposeAsync();
        Assert.Equal(0, sm.ActiveLeases);

        // 借用归还后再卸载，必须成功且完成 Dispose
        var unloadSuccess2 = await sm.UnloadAsync(TimeSpan.FromMilliseconds(100));
        Assert.True(unloadSuccess2);
        Assert.Equal(ModelRuntimeState.Unloaded, sm.State);
        Assert.True(mockAdapter.IsDisposed);
    }

    [Fact]
    public async Task GateG2_ModelRouter_AcquireLease_WithResourceCoordination()
    {
        var scheduler = new ResourceScheduler();
        using var router = new ModelRouter(scheduler);

        router.RegisterAdapterFactory("test_chat_model", () => Task.FromResult(new MockDisposableAdapter()));

        var selection = new ModelSelection(ModelId: "test_chat_model", Purpose: "chat");
        var workload = new WorkloadContext(RequestId: "req_router_1");

        await using var lease = await router.AcquireLeaseAsync<MockDisposableAdapter>(selection, workload);

        Assert.NotNull(lease);
        Assert.NotNull(lease.Client);
        Assert.Equal("test_chat_model", lease.Identity.ModelId);

        // 检查调度器显存账本已被预留
        Assert.True(scheduler.AllocatedVramBytes > 0);

        // 释放租约后账本归还
        await lease.DisposeAsync();
        Assert.Equal(0, scheduler.AllocatedVramBytes);
    }

    [Fact]
    public async Task GateG5_ChatSession_ToolCall_LeaseRelease_PreventsDeadlock()
    {
        // 验证 Gate G5 关键规范：在低显存/Agent 工具调用场景下，
        // 执行工具前必须显式释放 LLM 租约，避免与下游嵌入模型产生显存争用死锁环。
        var scheduler = new ResourceScheduler();
        using var router = new ModelRouter(scheduler);
        router.RegisterAdapterFactory("agent_llm", () => Task.FromResult(new MockDisposableAdapter()));

        var searchMock = new Mock<IKnowledgeBaseSearchService>();
        var toolExecutedWhileLlmLeaseActive = false;

        searchMock.Setup(s => s.SearchAsync(It.IsAny<CrossKnowledgeBaseSearchRequest>(), It.IsAny<CancellationToken>()))
            .Returns<CrossKnowledgeBaseSearchRequest, CancellationToken>((req, ct) =>
            {
                // 工具执行中检查：调度器中的显存是否已被释放为 0（即 LLM 租约已归还，未在工具执行期间持有）
                if (scheduler.AllocatedVramBytes > 0)
                {
                    toolExecutedWhileLlmLeaseActive = true;
                }

                return Task.FromResult(new CrossKnowledgeBaseSearchResponse(
                    Query: req.Query,
                    Items: new List<RecallItem>
                    {
                        new("c1", "d1", "source.md", "Relevant fact for answering", 0.9, ScoreKind.RRF, 1)
                    },
                    SuccessfulKnowledgeBaseIds: new[] { "kb_agent" },
                    FailedKnowledgeBaseIds: Array.Empty<string>(),
                    PartialSuccess: false,
                    ScoreKind: ScoreKind.RRF,
                    Elapsed: TimeSpan.FromMilliseconds(2)
                ));
            });

        var chatService = new KnowledgeBaseChatService(router, searchMock.Object);

        var request = new KnowledgeBaseChatTurnRequest(
            SessionId: "session_anti_deadlock_1",
            UserMessage: "What is the relevant fact?",
            BoundKnowledgeBaseIds: new[] { "kb_agent" },
            ModelSelection: new ModelSelection("agent_llm", "chat"),
            EnableTools: true
        );

        var response = await chatService.ExecuteTurnAsync(request);

        // 验证：
        // 1. 工具调用已执行
        Assert.Equal(1, response.ToolCallsExecuted);
        // 2. 工具执行期间，LLM 租约被主动释放，因此 AllocatedVramBytes 为 0，toolExecutedWhileLlmLeaseActive 为 false！
        Assert.False(toolExecutedWhileLlmLeaseActive, "工具调用期间不得持有 LLM 模型租约，以防 GPU 显存死锁环");
        // 3. 租约释放次数至少为 2 次（阶段1判决前释放 + 阶段3合成后释放）
        Assert.True(response.LeaseReleasesCount >= 2);
        // 4. 引用正确注入
        Assert.Single(response.Citations);
        Assert.Contains("Relevant fact for answering", response.Answer);
    }

    [Fact]
    public void GateG3_ContentProfiles_ContractAndFiction_SplitAccurately()
    {
        var registry = new ContentProfileRegistry();

        // 1. 合同画像条款切分验证
        var contractProfile = registry.GetProfile("contract");
        Assert.Equal("contract", contractProfile.ProfileId);

        var contractText = @"
第一条 双方权利与义务
甲方应按期支付款项，乙方应保质交付成果。

第二条 保密协议
双方对项目涉及的技术秘密负有终身保密责任。

第三条 违约责任与仲裁
如有争议，由北京仲裁委员会裁决。
";
        var contractChunks = contractProfile.Split(contractText, "contract_doc_1");
        Assert.Equal(3, contractChunks.Count);
        Assert.Contains("第一条", contractChunks[0].Text);
        Assert.Contains("第二条", contractChunks[1].Text);
        Assert.Contains("第三条", contractChunks[2].Text);

        // 2. 小说章节画像验证
        var fictionProfile = registry.GetProfile("fiction");
        Assert.Equal("fiction", fictionProfile.ProfileId);

        var fictionText = @"
第一回 宴桃园豪杰三结义 斩黄巾英雄首立功
滚滚长江东逝水，浪花淘尽英雄。是非成败转头空。

第二回 张翼德怒鞭督邮 何国舅谋诛宦竖
且说董卓字仲颖，陇西临洮人也，官拜河东太守。
";
        var fictionChunks = fictionProfile.Split(fictionText, "fiction_doc_1");
        Assert.Equal(2, fictionChunks.Count);
        Assert.Contains("第一回", fictionChunks[0].Text);
        Assert.Contains("第二回", fictionChunks[1].Text);

        // 3. 未知画像默认回退到通用画像
        var unknownProfile = registry.GetProfile("unknown_custom_type");
        Assert.Equal("general", unknownProfile.ProfileId);
    }

    [Fact]
    public async Task GateG3_SqliteKnowledgeBaseStore_RebuildFtsIndex_AdvancesReadRevision()
    {
        var dbPath = Path.Combine(_tempDir, "kb_g3_rebuild.db");
        using var store = new SqliteKnowledgeBaseStore("kb_g3", dbPath);
        await store.InitializeAsync();

        var batch = new IndexBatch(
            KnowledgeBaseId: "kb_g3",
            ExpectedIndexEpoch: 1,
            ExpectedDocumentVersion: 1,
            ConnectorId: "conn_g3",
            SourceKey: "doc_g3.md",
            Locator: "/path/doc_g3.md",
            SourceRevision: "v1",
            ContentHash: "hash_g3",
            Chunks: new[]
            {
                new ChunkData("chunk_g3_1", 0, "Autonomous AI agent architecture with SQLite storage", 10),
                new ChunkData("chunk_g3_2", 1, "Microservice orchestration and resource scheduler", 10)
            }
        );
        var commitRes = await store.CommitDocumentAsync(batch);
        Assert.True(commitRes.Success);
        var oldRevision = commitRes.PublishedReadRevision;

        // 执行局部全文索引重建
        var newRevision = await store.RebuildFtsIndexAsync();

        // 验证 ReadRevision 单调递增
        Assert.True(newRevision > oldRevision);

        // 重建后执行全文检索验证数据完好
        var recall = await store.RecallAsync(new RecallRequest(Query: "Autonomous agent", TopK: 1));
        Assert.Single(recall.Items);
        Assert.Equal("chunk_g3_1", recall.Items[0].ChunkId);
    }

    [Fact]
    public async Task GateG9_StorageManager_OnlineBackupAndRestore_PreservesDataIntegrity()
    {
        var dataRoot = Path.Combine(_tempDir, "storage_root_g9");
        using var manager = new KnowledgeBaseStorageManager(dataRoot);

        // 1. 写入初始数据
        await using (var lease = await manager.AcquireLeaseAsync("kb_backup_test", StorageAccess.ReadWrite))
        {
            var batch = new IndexBatch(
                KnowledgeBaseId: "kb_backup_test",
                ExpectedIndexEpoch: 1,
                ExpectedDocumentVersion: 1,
                ConnectorId: "conn_1",
                SourceKey: "critical_asset.txt",
                Locator: "/critical_asset.txt",
                SourceRevision: "v1",
                ContentHash: "hash_123",
                Chunks: new[]
                {
                    new ChunkData("chk_crit_1", 0, "Critical production knowledge that must survive disaster", 10)
                }
            );
            var result = await lease.Store.CommitDocumentAsync(batch);
            Assert.True(result.Success);
        }

        // 2. 执行零停机热备份
        var backupPath = Path.Combine(_tempDir, "backups", "kb_backup_test.bak");
        await manager.BackupKnowledgeBaseAsync("kb_backup_test", backupPath);
        Assert.True(File.Exists(backupPath));
        Assert.True(new FileInfo(backupPath).Length > 0);

        // 3. 模拟灾难：删除当前知识库
        await manager.DeleteKnowledgeBaseAsync("kb_backup_test");
        var existsAfterDelete = await manager.ExistsAsync("kb_backup_test");
        Assert.False(existsAfterDelete);

        // 4. 从备份文件执行还原
        await manager.RestoreKnowledgeBaseAsync("kb_backup_test", backupPath);
        var existsAfterRestore = await manager.ExistsAsync("kb_backup_test");
        Assert.True(existsAfterRestore);

        // 5. 校验还原后的数据完整性与检索能力
        await using (var readLease = await manager.AcquireLeaseAsync("kb_backup_test", StorageAccess.ReadOnly))
        {
            var manifest = await readLease.Store.GetManifestAsync();
            Assert.Equal("kb_backup_test", manifest.KnowledgeBaseId);

            var recall = await readLease.Store.RecallAsync(new RecallRequest(Query: "Critical production knowledge", TopK: 1));
            Assert.Single(recall.Items);
            Assert.Equal("chk_crit_1", recall.Items[0].ChunkId);
            Assert.Contains("Critical production knowledge", recall.Items[0].Text);
        }
    }

    #endregion
}
