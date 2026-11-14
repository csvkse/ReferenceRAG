using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
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
/// 无模型纯词法知识库架构门禁测试套件
/// 覆盖：创建与自动降级、双轨分段门禁校验、无向量极速摄入、纯词法检索、蓝绿影子模型升级与解绑降级
/// </summary>
public sealed class KnowledgeBasePureLexicalModeGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _catalogPath;
    private readonly CatalogStore _catalog;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly ConfigManager _configManager;

    public KnowledgeBasePureLexicalModeGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RefRAG_LexicalGate_" + Guid.NewGuid().ToString("N"));
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
    public async Task Gate1_CreateKnowledgeBase_WithLexicalOnly_ShouldAssignPureLexicalModeAndLexicalChunkSpec()
    {
        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "Pure Lexical KB",
            description: "No model, FTS5 only",
            embeddingSpecId: "spec_lexical_only"
        );

        Assert.NotNull(kb);
        Assert.Equal("spec_lexical_only", kb.EmbeddingSpecId);
        Assert.Equal("spec_chunk_lexical_paragraph", kb.ChunkingSpecId);
        Assert.True(kb.IsPureLexical);
        Assert.Equal(KnowledgeBaseMode.PureLexical, kb.Mode);

        // 验证配置偏离状态：应判定为 Aligned
        var drift = await kbService.CheckConfigDriftAsync(kb.Id);
        Assert.Equal(DriftStatus.Aligned, drift.Status);
        Assert.Contains("纯词法模式", drift.UpgradeRecommendation);
    }

    [Fact]
    public async Task Gate2_CreateKnowledgeBase_WithoutModel_ShouldGracefullyFallbackToPureLexical()
    {
        var noModelCfg = new ObsidianRagConfig
        {
            Embedding = new EmbeddingConfig
            {
                ModelName = "none",
                ActiveBackend = "none"
            }
        };
        var noModelConfigPath = Path.Combine(_tempDir, "no_model_config.json");
        var noModelConfigManager = new ConfigManager(initialConfig: noModelCfg, customConfigPath: noModelConfigPath);
        noModelConfigManager.Save(noModelCfg);

        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: noModelConfigManager
        );

        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "Auto Fallback KB",
            description: "No model available in environment"
        );

        Assert.NotNull(kb);
        Assert.Equal("spec_lexical_only", kb.EmbeddingSpecId);
        Assert.Equal("spec_chunk_lexical_paragraph", kb.ChunkingSpecId);
        Assert.True(kb.IsPureLexical);
    }

    [Fact]
    public async Task Gate3_PureLexical_ChunkingSpec_ValidationGate()
    {
        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        // 1. 合法 800 Tokens、1200 Tokens 与 2048 Tokens 应该放行
        var validSpec800 = new ChunkingSpec(Id: "spec_test_800", MaxTokens: 800, MinTokens: 100);
        await _catalog.SaveChunkingSpecAsync(validSpec800);
        var kb800 = await kbService.CreateKnowledgeBaseAsync(
            name: "Lexical 800",
            description: "Valid",
            embeddingSpecId: "spec_lexical_only",
            chunkingSpecId: "spec_test_800"
        );
        Assert.Equal("spec_test_800", kb800.ChunkingSpecId);

        var validSpec1200 = new ChunkingSpec(Id: "spec_test_1200", MaxTokens: 1200, MinTokens: 100);
        await _catalog.SaveChunkingSpecAsync(validSpec1200);
        var kb1200 = await kbService.CreateKnowledgeBaseAsync(
            name: "Lexical 1200",
            description: "Valid",
            embeddingSpecId: "spec_lexical_only",
            chunkingSpecId: "spec_test_1200"
        );
        Assert.Equal("spec_test_1200", kb1200.ChunkingSpecId);

        // 2. 超过 2048 认知安全天花板的分段必须被拦截
        var oversizedSpec = new ChunkingSpec(Id: "spec_test_3000", MaxTokens: 3000, MinTokens: 100);
        await _catalog.SaveChunkingSpecAsync(oversizedSpec);
        var exOversized = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            kbService.CreateKnowledgeBaseAsync(
                name: "Lexical 3000",
                description: "Oversized",
                embeddingSpecId: "spec_lexical_only",
                chunkingSpecId: "spec_test_3000"
            ));
        Assert.Contains("超过了认知检索与下游 Agent 上下文建议安全上限 (2048)", exOversized.Message);

        // 3. 小于 100 词频不足的切片必须被拦截
        var tinySpec = new ChunkingSpec(Id: "spec_test_50", MaxTokens: 50, MinTokens: 10);
        await _catalog.SaveChunkingSpecAsync(tinySpec);
        var exTiny = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            kbService.CreateKnowledgeBaseAsync(
                name: "Lexical 50",
                description: "Tiny",
                embeddingSpecId: "spec_lexical_only",
                chunkingSpecId: "spec_test_50"
            ));
        Assert.Contains("过小，会导致词汇共现密度不足", exTiny.Message);
    }

    [Fact]
    public async Task Gate4_KnowledgeBaseSyncWorker_PureLexical_FastIngestion_NoVectorTable()
    {
        var sourceDir = Path.Combine(_tempDir, "lexical_source");
        Directory.CreateDirectory(sourceDir);

        var docContent = @"# 企业核心技术架构手册
本文档介绍 ReferenceRAG 的纯词法引擎。

## 第一节 快速入门与安装
```bash
dotnet run --project Host/ReferenceRAG.WebHost
```
通过上述命令即可在纯 CPU 环境下零模型极速启动。

## 第二节 FTS5 倒排索引原理解析
SQLite FTS5 支持多字段加权与 BM25F 算法，毫秒级召回。
";
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "manual.md"), docContent);

        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "Sync Test KB",
            description: "Testing worker with pure lexical",
            embeddingSpecId: "spec_lexical_only",
            chunkingSpecId: "spec_chunk_lexical_paragraph"
        );

        var connDef = await kbService.AddConnectorAsync(
            kb.Id,
            kind: "Folder",
            targetPath: sourceDir,
            filePattern: "*.md"
        );

        var chunker = new MarkdownChunker();
        var worker = new KnowledgeBaseSyncWorker(
            _catalog,
            _storageManager,
            enableEmbedding: true, // 即使传入 true，worker 也应依据纯词法规格自动旁路跳过向量生成
            chunker: chunker
        );

        var job = new JobDefinition(
            Id: "job_lex_001",
            Operation: "sync",
            KnowledgeBaseId: kb.Id,
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Queued,
            Phase: OperationPhase.None,
            TotalUnits: 0,
            ProcessedUnits: 0,
            IdempotencyKey: "test:job:lex:001",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow,
            FinishedAt: null
        );

        var connectorInstance = new LocalFolderConnector(connDef.Id, kb.Id, sourceDir, "*.md");
        var finalJob = await worker.ExecuteSyncAsync(job, connectorInstance, CancellationToken.None);

        Assert.Equal(JobStatus.Succeeded, finalJob.Status);

        // 验证存储：切片存在、具有行号与标题层级
        await using var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadOnly);
        var (chunks, totalCount) = await lease.Store.GetChunksAsync(pageSize: 50);

        Assert.True(totalCount > 0);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.StartLine > 0);
            Assert.True(chunk.EndLine >= chunk.StartLine);
            Assert.False(string.IsNullOrWhiteSpace(chunk.HeadingPath));
        }

        // 验证向量数据：纯词法模式下向量数为 0
        var manifest = await lease.Store.GetManifestAsync();
        Assert.Equal(0, manifest.ActiveVectorCount);
    }

    [Fact]
    public async Task Gate5_PureLexical_Search_DirectBM25F()
    {
        var sourceDir = Path.Combine(_tempDir, "search_source");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "guide.md"), "# 独孤九剑破剑式\n\n天下武学无所不包，顺其自然，神妙绝伦。");

        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "Lexical Search KB",
            description: "Search test",
            embeddingSpecId: "spec_lexical_only",
            chunkingSpecId: "spec_chunk_lexical_paragraph"
        );

        var connDef = await kbService.AddConnectorAsync(
            kb.Id,
            kind: "Folder",
            targetPath: sourceDir,
            filePattern: "*.md"
        );

        var worker = new KnowledgeBaseSyncWorker(_catalog, _storageManager, enableEmbedding: false, chunker: new MarkdownChunker());
        var job = new JobDefinition("job_search_01", "sync", kb.Id, 1, JobStatus.Queued, OperationPhase.None, 0, 0, "key1", null, DateTime.UtcNow, null);
        await worker.ExecuteSyncAsync(job, new LocalFolderConnector(connDef.Id, kb.Id, sourceDir, "*.md"), CancellationToken.None);

        // 实例化检索服务（不传入 embeddingService，模拟零模型纯 CPU 纯词法检索）
        var searchService = new KnowledgeBaseSearchService(
            _catalog,
            _storageManager,
            configManager: _configManager,
            embeddingService: null,
            logger: NullLogger<KnowledgeBaseSearchService>.Instance
        );

        var response = await searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "独孤九剑",
            KnowledgeBaseIds: new[] { kb.Id },
            TopK: 5,
            UseLexicalTokenizer: true
        ));

        Assert.NotNull(response);
        Assert.True(response.Items.Count > 0);
        Assert.True(response.ScoreKind == ScoreKind.RRF || response.ScoreKind == ScoreKind.BM25);
        Assert.Contains("独孤九剑", response.Items[0].Text);
        Assert.True(response.Items[0].StartLine > 0);
    }

    [Fact]
    public async Task Gate6_UpgradePureLexical_ToModel_ReverseCompatibilityGate()
    {
        var fakeResolver = new FakeEmbeddingAdapterResolver();
        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager,
            adapterResolver: fakeResolver
        );

        // 1. 创建使用 1200 Tokens 切片的纯词法库
        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "Large Lexical KB",
            description: "CodeFirst 1200",
            embeddingSpecId: "spec_lexical_only",
            chunkingSpecId: "spec_chunk_lexical_code"
        );

        // 2. 尝试绑定 512 上限的小模型：必须触发反向兼容门禁拦截（入队即抛，不产生作业）！
        var smallSpec = new EmbeddingSpec(
            Id: "spec_small_limit512",
            ModelName: "bge-small-zh-v1.5",
            Dimension: 512,
            Metric: "cosine",
            MaxTokens: 512
        );
        await _catalog.SaveEmbeddingSpecAsync(smallSpec);

        var gateEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => kbService.TriggerModelReindexJobAsync(kb.Id, "spec_small_limit512"));
        Assert.Contains("小于当前知识库分段规格", gateEx.Message);
        Assert.Contains("1200", gateEx.Message);
        Assert.Contains("512", gateEx.Message);

        // 3. 尝试绑定 8192 上限的大模型（bge-m3）：应顺利放行（作业化：202 入队 → 后台完成）
        var largeSpec = new EmbeddingSpec(
            Id: "spec_large_limit8192",
            ModelName: "BAAI/bge-m3",
            Dimension: 1024,
            Metric: "cosine",
            MaxTokens: 8192
        );
        await _catalog.SaveEmbeddingSpecAsync(largeSpec);

        var job = await kbService.TriggerModelReindexJobAsync(kb.Id, "spec_large_limit8192");
        Assert.Equal(JobOperation.Reindex, job.Operation);
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job.Id);
        Assert.Equal(JobStatus.Succeeded, terminal.Status);

        var updatedKb = await _catalog.GetKnowledgeBaseAsync(kb.Id);
        Assert.NotNull(updatedKb);
        Assert.Equal("spec_large_limit8192", updatedKb.EmbeddingSpecId);
        Assert.False(updatedKb.IsPureLexical);
        Assert.Equal(KnowledgeBaseMode.Hybrid, updatedKb.Mode);
    }

    [Fact]
    public async Task Gate7_UnbindModel_ShouldDowngradeToPureLexical()
    {
        var kbService = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        // 1. 创建混合模型库
        var kb = await kbService.CreateKnowledgeBaseAsync(
            name: "Hybrid KB",
            description: "Bound to default",
            embeddingSpecId: "bge-m3-default",
            chunkingSpecId: "spec_chunk_default"
        );
        Assert.False(kb.IsPureLexical);

        // 2. 解绑模型转为纯词法
        var downgradedKb = await kbService.UnbindModelAsync(kb.Id);
        Assert.Equal("spec_lexical_only", downgradedKb.EmbeddingSpecId);
        Assert.True(downgradedKb.IsPureLexical);
        Assert.Equal(KnowledgeBaseMode.PureLexical, downgradedKb.Mode);
    }
}
