using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.DocumentExtraction.Domain;
using ReferenceRAG.Core.Features.DocumentExtraction.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public sealed class DocumentRechunkAndBackgroundTaskTests : IDisposable
{
    private readonly string _tempDir;

    public DocumentRechunkAndBackgroundTaskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "referencerag_rechunk_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task RechunkDocumentAsync_CorruptedScannedDoc_ClearsCorruptedChunksAtomically()
    {
        // 1. 准备测试知识库与文档
        var kbId = "kb_test_rechunk";
        var dbPath = Path.Combine(_tempDir, "kb.db");
        var store = new SqliteKnowledgeBaseStore(kbId, dbPath, NullLogger<SqliteKnowledgeBaseStore>.Instance);
        await store.InitializeAsync();

        // 写入初始受污染文档与历史切片
        var corruptFile = Path.Combine(_tempDir, "scanned.pdf");
        await File.WriteAllBytesAsync(corruptFile, new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 });

        var initialChunks = new List<ChunkData>
        {
            new ChunkData("chunk_bad_1", 0, "0000002075 00000 n\ntrailer\n<< /Size 1504 >>", 10, null, 1, 5, null),
            new ChunkData("chunk_bad_2", 1, "1503 0 obj <</Filter/FlateDecode>>stream\n??x??z??", 10, null, 6, 10, null)
        };

        var initialBatch = new IndexBatch(
            KnowledgeBaseId: kbId,
            ExpectedIndexEpoch: 0,
            ExpectedDocumentVersion: 0,
            ConnectorId: "conn_1",
            SourceKey: "scanned.pdf",
            Locator: corruptFile,
            SourceRevision: "v1",
            ContentHash: "hash_old",
            Chunks: initialChunks,
            Vectors: null,
            ProducerCohort: "spec_lexical_only"
        );
        var commitRes = await store.CommitDocumentAsync(initialBatch);
        Assert.True(commitRes.Success);

        var (docs, _) = await store.GetDocumentsAsync();
        var doc = Assert.Single(docs);
        Assert.Equal(2, doc.ChunkCount);

        // 2. 模拟依赖并构建 IKnowledgeBaseService
        var catalogMock = new Mock<ICatalogStore>();
        catalogMock.Setup(c => c.GetKnowledgeBaseAsync(kbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeBaseDefinition(
                Id: kbId,
                Name: "测试库",
                Description: null,
                Enabled: true,
                DesiredConfigRevision: 1,
                EffectiveConfigRevision: 1,
                EmbeddingSpecId: "spec_lexical_only",
                CreatedAt: DateTime.UtcNow,
                UpdatedAt: DateTime.UtcNow,
                ChunkingSpecId: "spec_chunk_default"));
        catalogMock.Setup(c => c.GetChunkingSpecAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChunkingSpec("spec_chunk_default"));

        var storageMgrMock = new Mock<IKnowledgeBaseStorageManager>();
        var leaseMock = new Mock<IStorageLease>();
        leaseMock.Setup(l => l.Store).Returns(store);
        leaseMock.Setup(l => l.IndexEpochSnapshot).Returns(1);
        storageMgrMock.Setup(s => s.AcquireLeaseAsync(kbId, StorageAccess.ReadWrite, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IStorageLease>(leaseMock.Object));

        // 模拟解析器检测为扫描件 ScannedRequiresOcr
        var extractorRouterMock = new Mock<IDocumentExtractorRouter>();
        extractorRouterMock.Setup(r => r.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentExtractResult(
                Status: DocumentParseStatus.ScannedRequiresOcr,
                ExtractedText: "",
                DiagnosticMessage: "检测到纯图片扫描件，无文字层",
                IsScannedPdf: true));

        var kbService = new KnowledgeBaseService(
            catalogMock.Object,
            storageMgrMock.Object,
            extractorRouter: extractorRouterMock.Object);

        // 3. 执行单文档重新切片
        var result = await kbService.RechunkDocumentAsync(kbId, doc.Id);

        // 4. 断言结果
        Assert.Equal(0, result.ChunkCount);
        Assert.Equal("ScannedRequiresOcr", result.Status);
        Assert.Contains("彻底清理历史残留乱码切片", result.Message);

        // 5. 直查数据库验证物理切片与倒排索引彻底清空
        var (chunksAfter, totalCount) = await store.GetChunksAsync(documentId: doc.Id);
        Assert.Empty(chunksAfter);
        Assert.Equal(0, totalCount);

        var (docsAfter, _) = await store.GetDocumentsAsync();
        var docAfter = Assert.Single(docsAfter);
        Assert.Equal(0, docAfter.ChunkCount);
        Assert.Equal(2, docAfter.DocumentVersion);
    }

    [Fact]
    public async Task RechunkDocumentAsync_ValidMarkdownDoc_GeneratesCleanChunks()
    {
        // 1. 准备测试知识库与有效 Markdown 文档
        var kbId = "kb_test_md";
        var dbPath = Path.Combine(_tempDir, "kb_md.db");
        var store = new SqliteKnowledgeBaseStore(kbId, dbPath, NullLogger<SqliteKnowledgeBaseStore>.Instance);
        await store.InitializeAsync();

        var mdFile = Path.Combine(_tempDir, "guide.md");
        var mdContent = @"# 引言
这是 ReferenceRAG 系统的引言章节。

## 核心架构
系统采用物理单库隔离与双轨检索架构。

## 运维治理
支持单文档重新切片与长时任务监控。";
        await File.WriteAllTextAsync(mdFile, mdContent);

        var initialBatch = new IndexBatch(
            KnowledgeBaseId: kbId,
            ExpectedIndexEpoch: 0,
            ExpectedDocumentVersion: 0,
            ConnectorId: "conn_1",
            SourceKey: "guide.md",
            Locator: mdFile,
            SourceRevision: "v1",
            ContentHash: "hash_old",
            Chunks: Array.Empty<ChunkData>(),
            Vectors: null,
            ProducerCohort: "spec_lexical_only"
        );
        await store.CommitDocumentAsync(initialBatch);
        var (docs, _) = await store.GetDocumentsAsync();
        var doc = Assert.Single(docs);

        // 2. 模拟依赖
        var catalogMock = new Mock<ICatalogStore>();
        catalogMock.Setup(c => c.GetKnowledgeBaseAsync(kbId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeBaseDefinition(
                Id: kbId,
                Name: "测试库",
                Description: null,
                Enabled: true,
                DesiredConfigRevision: 1,
                EffectiveConfigRevision: 1,
                EmbeddingSpecId: "spec_lexical_only",
                CreatedAt: DateTime.UtcNow,
                UpdatedAt: DateTime.UtcNow,
                ChunkingSpecId: "spec_chunk_default"));
        catalogMock.Setup(c => c.GetChunkingSpecAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChunkingSpec("spec_chunk_default") { MinTokens = 10, MaxTokens = 100 });

        var storageMgrMock = new Mock<IKnowledgeBaseStorageManager>();
        var leaseMock = new Mock<IStorageLease>();
        leaseMock.Setup(l => l.Store).Returns(store);
        leaseMock.Setup(l => l.IndexEpochSnapshot).Returns(1);
        storageMgrMock.Setup(s => s.AcquireLeaseAsync(kbId, StorageAccess.ReadWrite, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<IStorageLease>(leaseMock.Object));

        var extractorRouterMock = new Mock<IDocumentExtractorRouter>();
        extractorRouterMock.Setup(r => r.ExtractAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DocumentExtractResult(
                Status: DocumentParseStatus.Success,
                ExtractedText: mdContent));

        var kbService = new KnowledgeBaseService(
            catalogMock.Object,
            storageMgrMock.Object,
            extractorRouter: extractorRouterMock.Object);

        // 3. 执行重新切片
        var result = await kbService.RechunkDocumentAsync(kbId, doc.Id);

        // 4. 断言
        Assert.True(result.ChunkCount > 0);
        Assert.Equal("Success", result.Status);

        var (chunksAfter, totalCount) = await store.GetChunksAsync(documentId: doc.Id);
        Assert.True(totalCount > 0);
        Assert.All(chunksAfter, c => Assert.False(string.IsNullOrWhiteSpace(c.Text)));
    }

    [Fact]
    public async Task IndexJobsController_GetActive_AggregatesLegacyAndCatalogJobs()
    {
        // 1. Mock IndexService
        var config = new ObsidianRagConfig();
        var manager = new ConfigManager(config);
        var services = new ServiceCollection();
        var vectorStoreMock = new Mock<IVectorStore>();
        vectorStoreMock.Setup(s => s.GetAllFilesAsync(It.IsAny<CancellationToken>()))
            .Returns(async () => { await Task.Delay(5000); return new List<FileRecord>(); });
        services.AddSingleton(vectorStoreMock.Object);
        services.AddSingleton(new Mock<IEmbeddingService>().Object);
        using var serviceProvider = services.BuildServiceProvider();

        var indexService = new IndexService(
            serviceProvider,
            new Mock<IIndexEventPublisher>().Object,
            manager,
            new Mock<IFileIndexPipeline>().Object,
            new Mock<IFileProcessingGuard>().Object,
            NullLogger<IndexService>.Instance);

        var legacyJob = await indexService.StartIndexAsync(new IndexRequest { Sources = new List<string> { "source_1" } });

        // 2. Mock CatalogStore (KB background tasks)
        var catalogMock = new Mock<ICatalogStore>();
        var kbJob = new JobDefinition(
            Id: "job_kb_bm25",
            Operation: "bm25_rebuild",
            KnowledgeBaseId: "kb_notes",
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Running,
            Phase: OperationPhase.Building,
            TotalUnits: 50,
            ProcessedUnits: 25,
            IdempotencyKey: "k1",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow.AddMinutes(-2),
            FinishedAt: null
        );
        catalogMock.Setup(c => c.ListJobsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobDefinition> { kbJob });

        var controller = new IndexJobsController(
            indexService,
            NullLogger<IndexJobsController>.Instance,
            catalogStore: catalogMock.Object);

        // 3. 执行查询活跃任务
        var actionResult = await controller.GetActive();
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var jobs = Assert.IsAssignableFrom<List<IndexJobResponse>>(okResult.Value);

        // 4. 验证两个不同架构源的任务被无缝统一汇聚
        Assert.Contains(jobs, j => j.JobId == legacyJob.Id);
        var aggregatedKbJob = Assert.Single(jobs, j => j.JobId == "job_kb_bm25");
        Assert.Equal("BM25 倒排", aggregatedKbJob.JobType);
        Assert.Equal(25, aggregatedKbJob.ProcessedFiles);
        Assert.Equal(50, aggregatedKbJob.TotalFiles);
        Assert.Equal(50, aggregatedKbJob.ProgressPercent);
        Assert.Equal("Running", aggregatedKbJob.Status);

        await indexService.StopIndexAsync(legacyJob.Id);
    }

    [Fact]
    public async Task IndexJobsController_CancelJob_CancelsCatalogJob()
    {
        var config = new ObsidianRagConfig();
        var manager = new ConfigManager(config);
        using var services = new ServiceCollection().BuildServiceProvider();
        var indexService = new IndexService(
            services,
            new Mock<IIndexEventPublisher>().Object,
            manager,
            new Mock<IFileIndexPipeline>().Object,
            new Mock<IFileProcessingGuard>().Object,
            NullLogger<IndexService>.Instance);

        var coordinator = new KnowledgeBaseJobCoordinator();
        var cts = new CancellationTokenSource();
        coordinator.Register("job_to_cancel", cts);

        var kbServiceMock = new Mock<IKnowledgeBaseService>();
        kbServiceMock.Setup(s => s.CancelJobAsync("job_to_cancel", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var controller = new IndexJobsController(
            indexService,
            NullLogger<IndexJobsController>.Instance,
            kbService: kbServiceMock.Object,
            coordinator: coordinator);

        var result = await controller.CancelJob("job_to_cancel");
        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("任务已取消", okResult.Value?.ToString());
        kbServiceMock.Verify(s => s.CancelJobAsync("job_to_cancel", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JobsController_GetActiveAndHistory_FiltersCorrectly()
    {
        var activeJob = new JobDefinition(
            Id: "job_active_1",
            Operation: "sync",
            KnowledgeBaseId: "kb1",
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Running,
            Phase: OperationPhase.Chunking,
            TotalUnits: 10,
            ProcessedUnits: 5,
            IdempotencyKey: "k1",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow,
            FinishedAt: null
        );

        var completedJob = new JobDefinition(
            Id: "job_done_1",
            Operation: "reindex",
            KnowledgeBaseId: "kb1",
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Succeeded,
            Phase: OperationPhase.None,
            TotalUnits: 20,
            ProcessedUnits: 20,
            IdempotencyKey: "k2",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow.AddMinutes(-10),
            FinishedAt: DateTime.UtcNow.AddMinutes(-5)
        );

        var kbServiceMock = new Mock<IKnowledgeBaseService>();
        kbServiceMock.Setup(s => s.ListJobsAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<JobDefinition> { activeJob, completedJob });

        var controller = new JobsController(kbServiceMock.Object);

        // 活跃接口
        var activeRes = await controller.GetActiveJobs(null, CancellationToken.None);
        var okActive = Assert.IsType<OkObjectResult>(activeRes.Result);
        var activeList = Assert.IsAssignableFrom<IReadOnlyList<JobDefinition>>(okActive.Value);
        Assert.Single(activeList);
        Assert.Equal("job_active_1", activeList[0].Id);

        // 历史接口
        var histRes = await controller.GetJobHistory(null, CancellationToken.None);
        var okHist = Assert.IsType<OkObjectResult>(histRes.Result);
        var histList = Assert.IsAssignableFrom<IReadOnlyList<JobDefinition>>(okHist.Value);
        Assert.Single(histList);
        Assert.Equal("job_done_1", histList[0].Id);
    }
}
