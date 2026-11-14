using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Service.Controllers;
using ReferenceRAG.Tests;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// G1/G2 控制器层门禁（docs/vector-model-switch-reindex-timeout-root-cause-and-async-job-architecture.md §6）：
/// G1 入队 &lt; 2s 且响应前 0 次真实编码（慢速假提供者 + 闸门），409/400/404 契约；
/// G2 入队后取消请求令牌（模拟客户端断开 / RequestAborted）→ 后台作业照常完成并切换规格。
/// 根治「同步全量重嵌入跑在 HTTP 请求内 → axios 60s 超时 → RequestAborted 级联取消」。
/// </summary>
public sealed class KnowledgeBaseReindexJobControllerGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CatalogStore _catalogStore;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly ConfigManager _configManager;

    public KnowledgeBaseReindexJobControllerGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RefRAG_ReindexCtrlGate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _catalogStore = new CatalogStore(Path.Combine(_tempDir, "system", "catalog.db"));
        _catalogStore.InitializeAsync().GetAwaiter().GetResult();
        _storageManager = new KnowledgeBaseStorageManager(_tempDir);

        var initialCfg = new ObsidianRagConfig
        {
            Embedding = new EmbeddingConfig { ModelName = "bge-large-zh-v1.5", ActiveBackend = "LocalOnnx" },
            Chunking = new ChunkingConfig { Mode = "Adaptive", AdaptiveStrategyType = "Balanced", MaxTokens = 512 }
        };
        _configManager = new ConfigManager(initialConfig: initialCfg, customConfigPath: Path.Combine(_tempDir, "config.json"));
        _configManager.Save(initialCfg);
    }

    public void Dispose()
    {
        _storageManager.Dispose();
        _catalogStore.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private (KnowledgeBaseService Service, FakeEmbeddingAdapterResolver Resolver) NewService(FakeEmbeddingAdapterOptions options)
    {
        var resolver = new FakeEmbeddingAdapterResolver(options);
        var service = new KnowledgeBaseService(
            _catalogStore,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager,
            adapterResolver: resolver);
        return (service, resolver);
    }

    private static async Task<KnowledgeBaseDefinition> SeedKbAsync(KnowledgeBaseService service, string name, int documentCount)
    {
        var kb = await service.CreateKnowledgeBaseAsync(name, "controller gate", embeddingSpecId: "spec_lexical_only");
        for (int i = 0; i < documentCount; i++)
        {
            await service.CreateDocumentAsync(
                kb.Id,
                connectorId: "manual",
                title: $"Doc {i}",
                content: $"# 标题 {i}\n重索引门禁语料第{i}篇：客户端超时不得取消后台作业。\n\n## 小节\n请求生命周期与作业生命周期彻底解耦，编号 {i}。");
        }
        return kb;
    }

    private static EmbeddingSpec Spec(string id) => new(id, "gate-controller-model", 16, MaxTokens: 8192);

    [Fact]
    public async Task G1_Enqueue_ShouldReturn202Under2Seconds_WithZeroEncodingBeforeResponse()
    {
        var gate = new TaskCompletionSource();
        var (service, resolver) = NewService(new FakeEmbeddingAdapterOptions
        {
            FirstBatchGate = gate,
            BatchDelay = TimeSpan.FromMilliseconds(30)
        });
        var kb = await SeedKbAsync(service, "G1 KB", documentCount: 12);
        var spec = Spec("spec_g1_ctrl");
        await _catalogStore.SaveEmbeddingSpecAsync(spec);
        var controller = new KnowledgeBasesController(service, NullLogger<KnowledgeBasesController>.Instance);

        var sw = Stopwatch.StartNew();
        var result = await controller.TriggerModelReindex(kb.Id, new TriggerReindexRequest(spec.Id), CancellationToken.None);
        sw.Stop();

        // 入队必须秒回（远小于 axios 60s 超时），且此刻尚未发生任何真实编码
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"入队耗时 {sw.ElapsedMilliseconds}ms，必须 < 2000ms");
        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var job = Assert.IsType<JobDefinition>(accepted.Value);
        Assert.Equal(JobOperation.Reindex, job.Operation);
        Assert.Equal(JobStatus.Queued, job.Status);
        Assert.Equal($"/api/jobs/{job.Id}", accepted.Location);
        Assert.Equal(0, resolver.Stats.BatchCalls);
        Assert.Equal(0, resolver.Stats.EncodedTexts);

        // 放行后台编码：作业独立完成，规格切换生效
        gate.SetResult();
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalogStore, job.Id);
        Assert.Equal(JobStatus.Succeeded, terminal.Status);
        Assert.True(resolver.Stats.EncodedTexts > 0);

        var switched = await _catalogStore.GetKnowledgeBaseAsync(kb.Id);
        Assert.Equal(spec.Id, switched!.EmbeddingSpecId);
        var status = await service.GetReindexStatusAsync(kb.Id);
        Assert.Null(status.ActiveJob);
        Assert.False(status.Generation.ShadowExists);
        Assert.True(status.Generation.ActiveVectorCount > 0);
    }

    [Fact]
    public async Task G1_ConflictAndValidation_ShouldMapTo409_400_404()
    {
        var gate = new TaskCompletionSource();
        var (service, _) = NewService(new FakeEmbeddingAdapterOptions { FirstBatchGate = gate });
        var kb = await SeedKbAsync(service, "G1 Conflict KB", documentCount: 2);
        var spec = Spec("spec_g1_conflict");
        await _catalogStore.SaveEmbeddingSpecAsync(spec);
        var controller = new KnowledgeBasesController(service, NullLogger<KnowledgeBasesController>.Instance);

        var first = await controller.TriggerModelReindex(kb.Id, new TriggerReindexRequest(spec.Id), CancellationToken.None);
        var job = Assert.IsType<JobDefinition>(Assert.IsType<AcceptedResult>(first.Result).Value);

        // 同库进行中 → 409（KnowledgeBaseJobConflictException 必须先于 InvalidOperationException 捕获）
        var conflict = await controller.TriggerModelReindex(kb.Id, new TriggerReindexRequest(spec.Id), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(conflict.Result);

        // 未显式指定目标规格 → 400（禁止静默回退全局模型）
        var missingSpec = await controller.TriggerModelReindex(kb.Id, new TriggerReindexRequest(null), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingSpec.Result);

        // 未知规格 → 400
        var unknownSpec = await controller.TriggerModelReindex(kb.Id, new TriggerReindexRequest("spec_not_exists"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(unknownSpec.Result);

        // 未知知识库 → 404
        var unknownKb = await controller.TriggerModelReindex("kb_not_exists", new TriggerReindexRequest(spec.Id), CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(unknownKb.Result);

        gate.SetResult();
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalogStore, job.Id);
        Assert.Equal(JobStatus.Succeeded, terminal.Status);
    }

    [Fact]
    public async Task G2_RequestTokenCancelledAfterEnqueue_ShouldNotAffectBackgroundJob()
    {
        var (service, resolver) = NewService(new FakeEmbeddingAdapterOptions
        {
            BatchDelay = TimeSpan.FromMilliseconds(40)
        });
        var kb = await SeedKbAsync(service, "G2 KB", documentCount: 6);
        var spec = Spec("spec_g2_ctrl");
        await _catalogStore.SaveEmbeddingSpecAsync(spec);
        var controller = new KnowledgeBasesController(service, NullLogger<KnowledgeBasesController>.Instance);

        using var requestCts = new CancellationTokenSource();
        var result = await controller.TriggerModelReindex(kb.Id, new TriggerReindexRequest(spec.Id), requestCts.Token);
        var job = Assert.IsType<JobDefinition>(Assert.IsType<AcceptedResult>(result.Result).Value);

        // 模拟客户端断开：HTTP 请求令牌在入队后立即取消（旧架构在此会级联杀死整个重嵌入循环）
        requestCts.Cancel();

        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalogStore, job.Id);
        Assert.Equal(JobStatus.Succeeded, terminal.Status);
        Assert.True(resolver.Stats.EncodedTexts > 0);

        var switched = await _catalogStore.GetKnowledgeBaseAsync(kb.Id);
        Assert.Equal(spec.Id, switched!.EmbeddingSpecId);
        var status = await service.GetReindexStatusAsync(kb.Id);
        Assert.False(status.Generation.ShadowExists);
        Assert.True(status.Generation.ActiveVectorCount > 0);
    }
}