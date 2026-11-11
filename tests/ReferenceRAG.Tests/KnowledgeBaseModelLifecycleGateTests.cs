using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using ReferenceRAG.Infrastructure.Features.Resilience;
using ReferenceRAG.Storage;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// 目标 3, 4, 5 质量门禁：原型固化快照、配置偏离诊断、蓝绿影子重构索引与四态断路器高可用降级
/// </summary>
public class KnowledgeBaseModelLifecycleGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _catalogPath;
    private readonly CatalogStore _catalog;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly ConfigManager _configManager;

    public KnowledgeBaseModelLifecycleGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RefRAG_LifecycleGate_" + Guid.NewGuid().ToString("N"));
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
                ModelName = "bge-large-zh-v1.5",
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
    public async Task Gate1_PrototypeSnapshotOnInit_ShouldFreezeGlobalSpecsIntoDedicatedImmutableSpecs()
    {
        var service = new KnowledgeBaseService(
            catalog: _catalog,
            storageManager: _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        // 创建知识库，不指定任何特定模型或分块规格（由全局默认继承）
        var kb = await service.CreateKnowledgeBaseAsync(
            name: "FrozenDefaultKB",
            description: "KB created with initial snapshot"
        );

        Assert.NotNull(kb);
        Assert.StartsWith("spec_emb_", kb.EmbeddingSpecId);
        Assert.StartsWith("spec_chunk_", kb.ChunkingSpecId);

        // 验证已固化在 catalog.db 中的不可变规格
        var embSpec = await _catalog.GetEmbeddingSpecAsync(kb.EmbeddingSpecId);
        Assert.NotNull(embSpec);
        Assert.Equal("bge-large-zh-v1.5", embSpec.ModelName);

        var chunkSpec = await _catalog.GetChunkingSpecAsync(kb.ChunkingSpecId);
        Assert.NotNull(chunkSpec);
        Assert.Equal("Balanced", chunkSpec.StrategyType);

        // 变更全局配置（模拟全局模型升级）
        var updatedCfg = _configManager.Load();
        updatedCfg.Embedding.ModelName = "text-embedding-3-small";
        updatedCfg.Chunking.AdaptiveStrategyType = "PrecisionDense";
        _configManager.Save(updatedCfg);

        // 再次获取已创建知识库的规格，确认完全被固化隔离，未被全局变更破坏
        var kbAfterGlobalChange = await service.GetKnowledgeBaseAsync(kb.Id);
        Assert.NotNull(kbAfterGlobalChange);
        var embAfter = await _catalog.GetEmbeddingSpecAsync(kbAfterGlobalChange.EmbeddingSpecId);
        Assert.Equal("bge-large-zh-v1.5", embAfter!.ModelName);
    }

    [Fact]
    public async Task Gate2_ConfigDriftDetection_ShouldReportAlignedAndDriftedAccurately()
    {
        var service = new KnowledgeBaseService(
            catalog: _catalog,
            storageManager: _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        var kb = await service.CreateKnowledgeBaseAsync("DriftTestKB", "Test drift");

        // 1. 刚创建时与全局对齐
        var drift1 = await service.CheckConfigDriftAsync(kb.Id);
        Assert.Equal(DriftStatus.Aligned, drift1.Status);
        Assert.False(drift1.CanUpgrade);

        // 2. 修改全局配置，触发模型偏离
        var cfg = _configManager.Load();
        cfg.Embedding.ModelName = "qwen-embedding-v2";
        _configManager.Save(cfg);

        var drift2 = await service.CheckConfigDriftAsync(kb.Id);
        Assert.Equal(DriftStatus.Drifted, drift2.Status);
        Assert.True(drift2.CanUpgrade);
        Assert.Equal("bge-large-zh-v1.5", drift2.CurrentEmbeddingModel);
        Assert.Equal("qwen-embedding-v2", drift2.GlobalDefaultEmbeddingModel);
        Assert.NotNull(drift2.UpgradeRecommendation);
    }

    [Fact]
    public async Task Gate3_BlueGreenShadowReindexing_ShouldRecomputeAndPromoteWithoutDowntime()
    {
        var service = new KnowledgeBaseService(
            catalog: _catalog,
            storageManager: _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager
        );

        var kb = await service.CreateKnowledgeBaseAsync("BlueGreenKB", "Reindex Test");

        // 写入测试文档
        await service.CreateDocumentAsync(
            kb.Id,
            connectorId: "manual",
            title: "Reindex Test Doc",
            content: "# Section 1\nLine 1\nLine 2\n\n# Section 2\nLine 3\nLine 4"
        );

        // 注册目标新模型规格
        var targetSpec = new EmbeddingSpec(
            Id: "spec_target_dim8",
            ModelName: "new-target-model-8d",
            Dimension: 8
        );
        await service.SaveEmbeddingSpecAsync(targetSpec);

        // 执行蓝绿影子重构索引
        var result = await service.TriggerModelReindexAsync(kb.Id, targetSpec.Id);

        Assert.True(result.Success);
        Assert.True(result.ProcessedChunks >= 1);
        Assert.Equal(targetSpec.Id, result.TargetEmbeddingSpecId);

        // 验证知识库规格已切换
        var updatedKb = await service.GetKnowledgeBaseAsync(kb.Id);
        Assert.NotNull(updatedKb);
        Assert.Equal(targetSpec.Id, updatedKb.EmbeddingSpecId);

        // 验证检索在重构后正常运行
        var searchService = new KnowledgeBaseSearchService(
            catalog: _catalog,
            storageManager: _storageManager,
            fusionStrategy: new RrfSearchFusionStrategy(),
            configManager: _configManager
        );

        var searchResp = await searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "Section 1",
            KnowledgeBaseIds: new[] { kb.Id },
            TopK: 5,
            UseLexicalTokenizer: true
        ));

        Assert.NotEmpty(searchResp.Items);
        Assert.Equal(kb.Id, searchResp.SuccessfulKnowledgeBaseIds[0]);
    }

    [Fact]
    public async Task Gate4_CircuitBreaker_ShouldShortCircuitInZeroMs_AndFallbackToLexical()
    {
        var mockEmbedding = new Mock<IEmbeddingService>();
        var breaker = new ResilienceCircuitBreaker(failureThreshold: 3, breakDuration: TimeSpan.FromSeconds(10));

        var service = new KnowledgeBaseService(
            catalog: _catalog,
            storageManager: _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager,
            embeddingService: mockEmbedding.Object
        );

        var kb = await service.CreateKnowledgeBaseAsync("CircuitKB", "Breaker Test");
        await service.CreateDocumentAsync(kb.Id, "manual", "Doc 1", "Rust memory safety guarantees without GC.");

        var searchService = new KnowledgeBaseSearchService(
            catalog: _catalog,
            storageManager: _storageManager,
            fusionStrategy: new RrfSearchFusionStrategy(),
            logger: NullLogger<KnowledgeBaseSearchService>.Instance,
            configManager: _configManager,
            embeddingService: mockEmbedding.Object,
            circuitBreaker: breaker
        );

        // 模拟外部模型持续抛出异常（如 500 / Timeout）
        mockEmbedding.Setup(e => e.EncodeAsync(It.IsAny<string>(), It.IsAny<EmbeddingMode>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("SiliconFlow 503 Service Unavailable"));

        // 前 2 次调用：失败并记录
        for (int i = 0; i < 2; i++)
        {
            var r = await searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
                Query: "Rust safety",
                KnowledgeBaseIds: new[] { kb.Id },
                TopK: 5,
                UseLexicalTokenizer: false,
            AllowDegrade: true
            ));
            Assert.True(r.Degraded);
            Assert.NotEmpty(r.Items);
        }
        Assert.Equal(CircuitState.Closed, breaker.State);

        // 第 3 次调用：达到失败阈值，断路器开启
        var r3 = await searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "Rust safety",
            KnowledgeBaseIds: new[] { kb.Id },
            TopK: 5,
            UseLexicalTokenizer: false,
            AllowDegrade: true
        ));
        Assert.True(r3.Degraded);
        Assert.Equal(CircuitState.Open, breaker.State);

        // 重置 Mock 调用计数，验证开启后是否短路（0 次调用外部服务）
        mockEmbedding.Invocations.Clear();

        var shortCircuitResp = await searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "Rust safety",
            KnowledgeBaseIds: new[] { kb.Id },
            TopK: 5,
            UseLexicalTokenizer: false,
            AllowDegrade: true
        ));

        // 验证外部模型被 0 次调用（直接 0ms 短路降级）
        mockEmbedding.Verify(e => e.EncodeAsync(It.IsAny<string>(), It.IsAny<EmbeddingMode>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(shortCircuitResp.Degraded);
        Assert.NotEmpty(shortCircuitResp.Items);

        // 恢复模型健康：试探性自愈
        breaker.Reset();
        mockEmbedding.Setup(e => e.EncodeAsync(It.IsAny<string>(), It.IsAny<EmbeddingMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new float[1024]);

        var recoveredResp = await searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "Rust safety",
            KnowledgeBaseIds: new[] { kb.Id },
            TopK: 5,
            UseLexicalTokenizer: false,
            AllowDegrade: true
        ));

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.False(recoveredResp.Degraded);
    }
}
