using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Connectors;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Jobs;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// 知识库分段配置调整与嵌入模型强关联约束门禁测试集
/// </summary>
public class KnowledgeBaseChunkingModelCouplingGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _catalogPath;
    private readonly CatalogStore _catalog;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly ConfigManager _configManager;

    public KnowledgeBaseChunkingModelCouplingGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RefRAG_ChunkModelGate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _catalogPath = Path.Combine(_tempDir, "catalog.db");
        _catalog = new CatalogStore(_catalogPath);

        var dataDir = Path.Combine(_tempDir, "data");
        _storageManager = new KnowledgeBaseStorageManager(dataDir);

        var configPath = Path.Combine(_tempDir, "config.json");
        var initialCfg = new ObsidianRagConfig
        {
            Embedding = new EmbeddingConfig
            {
                ModelName = "bge-small-zh-v1.5",
                ActiveBackend = "LocalOnnx"
            },
            Chunking = new ChunkingConfig
            {
                Mode = "Adaptive",
                AdaptiveStrategyType = "Balanced",
                MaxTokens = 512
            }
        };
        _configManager = new ConfigManager(initialConfig: initialCfg, customConfigPath: configPath);
        _configManager.Save(initialCfg);
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

    [Fact]
    public async Task Gate1_ChunkingSpec_ExceedingModelLimit_ShouldBeRejectedOnCreateAndUpdate()
    {
        // 1. 注册 512 上限小模型 spec
        var smallSpec = new EmbeddingSpec(
            Id: "spec_small_512",
            ModelName: "bge-small-zh-v1.5",
            Dimension: 512,
            Metric: "cosine",
            MaxTokens: 512
        );
        await _catalog.SaveEmbeddingSpecAsync(smallSpec);

        // 2. 注册 1024 超限分块 spec
        var oversizedChunkSpec = new ChunkingSpec(
            Id: "spec_chunk_oversized",
            MaxTokens: 1024,
            MinTokens: 50
        );
        await _catalog.SaveChunkingSpecAsync(oversizedChunkSpec);

        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        // 3. 校验创建期硬拦截
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await kbService.CreateKnowledgeBaseAsync(
                name: "Oversized KB",
                description: "Test",
                embeddingSpecId: "spec_small_512",
                chunkingSpecId: "spec_chunk_oversized"
            );
        });
        Assert.Contains("超过了当前绑定模型", ex.Message);
        Assert.Contains("1024", ex.Message);
        Assert.Contains("512", ex.Message);

        // 4. 合法创建后，更新期尝试切换到超限 spec 同样应被拦截
        var validChunkSpec = new ChunkingSpec(
            Id: "spec_chunk_valid_384",
            MaxTokens: 384,
            MinTokens: 50
        );
        await _catalog.SaveChunkingSpecAsync(validChunkSpec);

        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "Valid KB",
            description: "Test",
            embeddingSpecId: "spec_small_512",
            chunkingSpecId: "spec_chunk_valid_384"
        );

        var updateEx = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await kbService.UpdateKnowledgeBaseAsync(kb.Id, new UpdateKnowledgeBaseRequest(
                ChunkingSpecId: "spec_chunk_oversized"
            ));
        });
        Assert.Contains("超过了当前绑定模型", updateEx.Message);
    }

    [Fact]
    public async Task Gate2_ChunkingSpec_WithinModelLimit_ShouldSucceed()
    {
        // 注册 8192 上限大模型 spec
        var largeSpec = new EmbeddingSpec(
            Id: "spec_large_8192",
            ModelName: "BAAI/bge-m3",
            Dimension: 1024,
            Metric: "cosine",
            MaxTokens: 8192
        );
        await _catalog.SaveEmbeddingSpecAsync(largeSpec);

        var chunkSpec = new ChunkingSpec(
            Id: "spec_chunk_1500",
            MaxTokens: 1500,
            MinTokens: 100
        );
        await _catalog.SaveChunkingSpecAsync(chunkSpec);

        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "Large Model KB",
            description: "Test",
            embeddingSpecId: "spec_large_8192",
            chunkingSpecId: "spec_chunk_1500"
        );

        Assert.NotNull(kb);
        Assert.Equal("spec_chunk_1500", kb.ChunkingSpecId);
        Assert.Equal("spec_large_8192", kb.EmbeddingSpecId);
    }

    [Fact]
    public async Task Gate3_ModelSwitch_ExceedingCurrentChunkTokens_ShouldTriggerReverseCompatibilityGate()
    {
        // 1. 初始化 8192 大模型和 1500 tokens 分块知识库
        var largeSpec = new EmbeddingSpec(
            Id: "spec_large_m3",
            ModelName: "BAAI/bge-m3",
            Dimension: 1024,
            Metric: "cosine",
            MaxTokens: 8192
        );
        await _catalog.SaveEmbeddingSpecAsync(largeSpec);

        var smallSpec = new EmbeddingSpec(
            Id: "spec_small_down",
            ModelName: "bge-small-zh-v1.5",
            Dimension: 512,
            Metric: "cosine",
            MaxTokens: 512
        );
        await _catalog.SaveEmbeddingSpecAsync(smallSpec);

        var chunkSpec = new ChunkingSpec(
            Id: "spec_chunk_large_1500",
            MaxTokens: 1500,
            MinTokens: 100
        );
        await _catalog.SaveChunkingSpecAsync(chunkSpec);

        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "M3 KB",
            description: "Test",
            embeddingSpecId: "spec_large_m3",
            chunkingSpecId: "spec_chunk_large_1500"
        );

        // 2. 尝试将模型切换为 512 上限的 small 模型（未重分段前，旧分块 1500 tokens 会溢出）
        // 作业化语义：入队阶段同步抛异常拦截（HTTP 400），不产生任何后台作业
        var gateEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => kbService.TriggerModelReindexJobAsync(kb.Id, "spec_small_down"));

        // 3. 断言反向相容性门禁阻断生效
        Assert.Contains("小于当前知识库分段规格", gateEx.Message);
        Assert.Contains("1500", gateEx.Message);
        Assert.Contains("512", gateEx.Message);

        var jobs = await _catalog.ListJobsAsync(kb.Id);
        Assert.DoesNotContain(jobs, j => j.Operation == JobOperation.Reindex);
    }

    [Fact]
    public async Task Gate4_KnowledgeBaseSyncWorker_ShouldUseChunkingSpecAndRetainLines()
    {
        // 1. 准备测试知识库与连接器目录
        var sourceDir = Path.Combine(_tempDir, "source_docs");
        Directory.CreateDirectory(sourceDir);

        var docContent = @"# 第一章 武林秘籍
这是关于内功心法的第一段说明。

## 第二节 招式进阶
```csharp
public void CastSpell()
{
    Console.WriteLine(""Exodia"");
}
```
这是招式进阶的代码后补充说明。
";
        var docFile = Path.Combine(sourceDir, "manual.md");
        await File.WriteAllTextAsync(docFile, docContent);

        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "Worker Test KB",
            description: "Testing worker chunking",
            embeddingSpecId: "default-spec",
            chunkingSpecId: "spec_chunk_default"
        );

        var connDef = await kbService.AddConnectorAsync(
            kb.Id,
            kind: "Folder",
            targetPath: sourceDir,
            filePattern: "*.md"
        );

        // 2. 实例化执行器（传入真实的 MarkdownChunker）
        var chunker = new MarkdownChunker();
        var worker = new KnowledgeBaseSyncWorker(
            _catalog,
            _storageManager,
            enableEmbedding: false,
            chunker: chunker
        );

        var job = new JobDefinition(
            Id: "job_test_001",
            Operation: "sync",
            KnowledgeBaseId: kb.Id,
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Queued,
            Phase: OperationPhase.None,
            TotalUnits: 0,
            ProcessedUnits: 0,
            IdempotencyKey: "test:job:001",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow,
            FinishedAt: null
        );

        var connectorInstance = new LocalFolderConnector(connDef.Id, kb.Id, sourceDir, "*.md");

        // 3. 执行同步
        var finalJob = await worker.ExecuteSyncAsync(job, connectorInstance, CancellationToken.None);
        Assert.Equal(JobStatus.Succeeded, finalJob.Status);

        // 4. 验证数据库中分块的行号和标题元数据
        await using var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadOnly);
        var (chunks, totalCount) = await lease.Store.GetChunksAsync(pageSize: 50);

        Assert.True(totalCount > 0);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.StartLine > 0, $"Chunk {chunk.Id} StartLine should be > 0, got {chunk.StartLine}");
            Assert.True(chunk.EndLine >= chunk.StartLine, $"Chunk {chunk.Id} EndLine should be >= StartLine");
            Assert.False(string.IsNullOrWhiteSpace(chunk.HeadingPath), $"Chunk {chunk.Id} should retain HeadingPath");
        }
    }

    [Fact]
    public async Task Gate5_TriggerRechunk_ShouldUpdateSpecAndTriggerSync()
    {
        var sourceDir = Path.Combine(_tempDir, "rechunk_source");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "test.md"), "# Title\n\nContent paragraph 1.\n\nContent paragraph 2.");

        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "Rechunk KB",
            description: "Testing rechunk",
            embeddingSpecId: "bge-m3-default",
            chunkingSpecId: "spec_chunk_default"
        );

        await kbService.AddConnectorAsync(
            kb.Id,
            kind: "Folder",
            targetPath: sourceDir,
            filePattern: "*.md"
        );

        // 触发重新分段切换至 code_first (1024 tokens, bge-m3-default 上限 8192)
        var result = await kbService.TriggerRechunkAsync(kb.Id, "spec_chunk_code_first");
        Assert.True(result.Success);
        Assert.Equal("spec_chunk_code_first", result.TargetChunkingSpecId);

        var updatedKb = await _catalog.GetKnowledgeBaseAsync(kb.Id);
        Assert.NotNull(updatedKb);
        Assert.Equal("spec_chunk_code_first", updatedKb.ChunkingSpecId);
    }

    [Fact]
    public async Task Gate6_TriggerRechunk_ExceedingModelLimit_ShouldBeRejected()
    {
        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "Small Model KB",
            description: "Testing rechunk gate",
            embeddingSpecId: "bge-small-zh-default",
            chunkingSpecId: "spec_chunk_default"
        );

        // 尝试重新分段切换至 code_first (1024 tokens，但小模型上限 512)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            kbService.TriggerRechunkAsync(kb.Id, "spec_chunk_code_first"));

        Assert.Contains("分段最大Token数 (1024) 超过了当前绑定模型", ex.Message);
        Assert.Contains("512", ex.Message);
    }
}
