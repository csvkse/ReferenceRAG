using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Service.Services;
using ReferenceRAG.Storage;
using Moq;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// BM25 控制器概览统计与防回归门禁测试
/// </summary>
public class BM25SummaryControllerGateTests : IDisposable
{
    private readonly string _tempDir;

    public BM25SummaryControllerGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ReferenceRAG_BM25SummaryTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Fact]
    public async Task GetSummary_WhenKnowledgeBaseHasChunks_ReturnsRealChunkAndFileCounts_NotZero()
    {
        // Arrange
        var kbDbPath = Path.Combine(_tempDir, "kb.db");
        var store = new SqliteKnowledgeBaseStore("kb_real_stats", kbDbPath);
        await store.InitializeAsync();

        var batch = new IndexBatch(
            KnowledgeBaseId: "kb_real_stats",
            ExpectedIndexEpoch: 1,
            ExpectedDocumentVersion: 1,
            ConnectorId: "c1",
            SourceKey: "三联版/神雕侠侣.md",
            Locator: "loc",
            SourceRevision: "rev1",
            ContentHash: "hash_1",
            Chunks: new List<ChunkData>
            {
                new ChunkData("c1", 0, "小龙女白衣胜雪", 10),
                new ChunkData("c2", 1, "杨过在古墓中练功", 10),
                new ChunkData("c3", 2, "神雕展翅高飞", 10)
            }
        );
        await store.CommitDocumentAsync(batch);

        var mockLease = new Mock<IStorageLease>();
        mockLease.Setup(l => l.Store).Returns(store);

        var mockStorageManager = new Mock<IKnowledgeBaseStorageManager>();
        mockStorageManager.Setup(m => m.AcquireLeaseAsync("kb_real_stats", It.IsAny<StorageAccess>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockLease.Object);

        var mockCatalog = new Mock<ICatalogStore>();
        mockCatalog.Setup(c => c.ListKnowledgeBasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<KnowledgeBaseDefinition>
            {
                new KnowledgeBaseDefinition("kb_real_stats", "金庸全集", null, true, 1, 1, "spec", DateTime.UtcNow, DateTime.UtcNow)
            });

        var controller = new BM25IndexController(
            Mock.Of<IBM25Store>(),
            Mock.Of<IVectorStore>(),
            new ConfigManager(new ObsidianRagConfig()),
            null!,
            NullLogger<BM25IndexController>.Instance,
            null,
            mockStorageManager.Object,
            mockCatalog.Object
        );

        // Act
        var result = await controller.GetSummary("kb_real_stats");

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var summary = Assert.IsType<BM25Summary>(okResult.Value);

        Assert.Equal(3, summary.TotalChunks);
        Assert.Equal(1, summary.TotalFiles);
        Assert.Equal(3, summary.TotalIndexedDocuments);
        Assert.True(summary.TotalVocabularySize > 0);
    }
}
