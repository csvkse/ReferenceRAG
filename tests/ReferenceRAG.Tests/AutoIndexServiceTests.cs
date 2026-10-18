using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class AutoIndexServiceTests
{
    private readonly Mock<IFileMonitorService> _fileMonitor = new();
    private readonly Mock<IFileIndexPipeline> _pipeline = new();
    private readonly Mock<IFileProcessingGuard> _guard = new();
    private readonly ConfigManager _configManager;

    public AutoIndexServiceTests()
    {
        _configManager = new ConfigManager(new ObsidianRagConfig
        {
            Indexing = new IndexingConfig { AutoIndexEnabled = true },
            Sources = new List<SourceFolder>
            {
                new() { Name = "Vault", Path = "C:/Vault", Enabled = true }
            }
        });

        _guard.Setup(g => g.TryAcquire(It.IsAny<string>())).Returns(true);
    }

    [Fact]
    public void OnFileChanged_DebouncesRapidModifications_DeduplicatesPendingQueue()
    {
        using var service = new AutoIndexService(
            _fileMonitor.Object,
            _pipeline.Object,
            _guard.Object,
            _configManager);

        var filePath = @"C:\Vault\Note.md";

        // 模拟用户在 Obsidian 中打字，每 500ms 触发一次自动保存
        service.OnFileChanged(this, new FileChangeEventArgs { FilePath = filePath, ChangeType = ChangeType.Modified });
        service.OnFileChanged(this, new FileChangeEventArgs { FilePath = filePath, ChangeType = ChangeType.Modified });
        service.OnFileChanged(this, new FileChangeEventArgs { FilePath = filePath, ChangeType = ChangeType.Modified });

        // 验证去重合并为 1 项待处理任务
        Assert.Equal(1, service.PendingCount);
    }

    [Fact]
    public void OnFileChanged_CreatedThenDeletedImmediately_CancelsWork()
    {
        using var service = new AutoIndexService(
            _fileMonitor.Object,
            _pipeline.Object,
            _guard.Object,
            _configManager);

        var filePath = @"C:\Vault\TempDraft.md";

        service.OnFileChanged(this, new FileChangeEventArgs { FilePath = filePath, ChangeType = ChangeType.Created });
        Assert.Equal(1, service.PendingCount);

        // 尚未索引即被删除，对冲消除
        service.OnFileChanged(this, new FileChangeEventArgs { FilePath = filePath, ChangeType = ChangeType.Deleted });
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task ProcessReadyChanges_HonorsQuietPeriod_AndIndexesFileOnce()
    {
        using var service = new AutoIndexService(
            _fileMonitor.Object,
            _pipeline.Object,
            _guard.Object,
            _configManager)
        {
            QuietPeriod = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(10)
        };

        var filePath = @"C:\Vault\Article.md";
        var t0 = DateTime.UtcNow;

        service.OnFileChanged(this, new FileChangeEventArgs { FilePath = filePath, ChangeType = ChangeType.Modified });

        // 1 秒后检查：尚未达到 2 秒静默窗口，不触发
        var processed1 = await service.ProcessReadyChangesInternalAsync(t0.AddSeconds(1));
        Assert.Equal(0, processed1);
        Assert.Equal(1, service.PendingCount);
        _pipeline.Verify(p => p.IndexSingleAsync(filePath, It.IsAny<IReadOnlyList<SourceFolder>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        // 2.5 秒后检查：已静默超过 2 秒，触发索引
        var processed2 = await service.ProcessReadyChangesInternalAsync(t0.AddSeconds(2.5));
        Assert.Equal(1, processed2);
        Assert.Equal(0, service.PendingCount);
        _pipeline.Verify(p => p.IndexSingleAsync(filePath, It.IsAny<IReadOnlyList<SourceFolder>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessReadyChanges_BatchesMultipleFiles_InSinglePass()
    {
        using var service = new AutoIndexService(
            _fileMonitor.Object,
            _pipeline.Object,
            _guard.Object,
            _configManager)
        {
            QuietPeriod = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(10)
        };

        var t0 = DateTime.UtcNow;
        service.OnFileChanged(this, new FileChangeEventArgs { FilePath = @"C:\Vault\A.md", ChangeType = ChangeType.Modified });
        service.OnFileChanged(this, new FileChangeEventArgs { FilePath = @"C:\Vault\B.md", ChangeType = ChangeType.Created });
        service.OnFileChanged(this, new FileChangeEventArgs { FilePath = @"C:\Vault\C.md", ChangeType = ChangeType.Modified });

        Assert.Equal(3, service.PendingCount);

        // 静默期过后，单次批处理直接消化全部 3 个文件
        var processed = await service.ProcessReadyChangesInternalAsync(t0.AddSeconds(2.5));
        Assert.Equal(3, processed);
        Assert.Equal(0, service.PendingCount);

        _pipeline.Verify(p => p.IndexSingleAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<SourceFolder>>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
