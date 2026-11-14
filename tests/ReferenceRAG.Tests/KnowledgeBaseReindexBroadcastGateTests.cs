using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.Indexing.Contracts;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// G-M6 后端广播门禁（docs/mobile-adaptation-and-frontend-job-ux-optimization-feasibility.md §6 P1-1）：
/// reindex worker 每次阶段跃迁/进度批次必广播 ReindexJobStarted/Progress/Completed；
/// 载荷契约（jobId/kbId/operation/字符串枚举 status·phase/processed·total/percent/error/createdAt·finishedAt）；
/// 取消路径广播 Cancelling→Cancelled；发布异常绝不破坏作业主流程。
/// </summary>
public sealed class KnowledgeBaseReindexBroadcastGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dataDir;
    private readonly CatalogStore _catalog;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly ConfigManager _configManager;

    public KnowledgeBaseReindexBroadcastGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RefRAG_ReindexBroadcastGate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _catalog = new CatalogStore(Path.Combine(_tempDir, "catalog.db"));
        _dataDir = Path.Combine(_tempDir, "data");
        _storageManager = new KnowledgeBaseStorageManager(_dataDir);

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
        _catalog.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    private KnowledgeBaseService NewService(FakeIndexEventPublisher publisher, FakeEmbeddingAdapterOptions? options = null)
    {
        var resolver = new FakeEmbeddingAdapterResolver(options);
        return new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager,
            adapterResolver: resolver,
            eventPublisher: publisher);
    }

    private async Task<KnowledgeBaseDefinition> SeedLexicalKbAsync(KnowledgeBaseService service, string name, int documentCount = 2)
    {
        var kb = await service.CreateKnowledgeBaseAsync(
            name: name,
            description: "reindex broadcast gate",
            embeddingSpecId: "spec_lexical_only");

        for (int i = 0; i < documentCount; i++)
        {
            await service.CreateDocumentAsync(
                kb.Id,
                connectorId: "manual",
                title: $"Broadcast Doc {i}",
                content: $"# 章节 {i}\n独孤九剑总诀式第{i}章：归妹趋无妄，无妄趋同人。\n\n## 小节 {i}\n风清扬传授令狐冲剑意，料敌机先，后发先至。段落编号 {i}。");
        }
        return kb;
    }

    private static EmbeddingSpec TargetSpec(string id, int dimension) => new(
        Id: id,
        ModelName: "gate-target-model",
        Dimension: dimension,
        Metric: "cosine",
        DocumentPrefix: null,
        MaxTokens: 8192);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("条件未在超时内满足");
    }

    // ==================== G-M6：Started/Progress/Completed 全链路 + 载荷契约 ====================

    [Fact]
    public async Task GM6_ReindexWorker_ShouldBroadcastStartedProgressCompleted_WithContractPayload()
    {
        var publisher = new FakeIndexEventPublisher();
        var service = NewService(publisher);
        var kb = await SeedLexicalKbAsync(service, "GM6 KB", documentCount: 3);
        var spec = TargetSpec("spec_gm6_16", 16);
        await _catalog.SaveEmbeddingSpecAsync(spec);

        var job = await service.TriggerModelReindexJobAsync(kb.Id, spec.Id);
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job.Id);
        Assert.Equal(JobStatus.Succeeded, terminal.Status);

        var events = publisher.Events;
        var started = events.Where(e => e.Name == "ReindexJobStarted").ToArray();
        var progress = events.Where(e => e.Name == "ReindexJobProgress").ToArray();
        var completed = events.Where(e => e.Name == "ReindexJobCompleted").ToArray();

        // 入队即广播 Started（先于任何 worker 跃迁）；终态恰好一次 Completed；至少一次 Progress
        Assert.Single(started);
        Assert.NotEmpty(progress);
        Assert.Single(completed);
        Assert.Equal("ReindexJobStarted", events[0].Name);

        // 所有事件共享同一作业/知识库标识与操作类型
        Assert.All(events, e =>
        {
            Assert.Equal(job.Id, e.Payload.GetProperty("jobId").GetString());
            Assert.Equal(kb.Id, e.Payload.GetProperty("knowledgeBaseId").GetString());
            Assert.Equal("reindex", e.Payload.GetProperty("operation").GetString());
        });

        // Started 载荷：Queued/Planning，进度归零
        var s = started[0].Payload;
        Assert.Equal("Queued", s.GetProperty("status").GetString());
        Assert.Equal("Planning", s.GetProperty("phase").GetString());
        Assert.Equal(0, s.GetProperty("processedUnits").GetInt32());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("finishedAt").ValueKind);

        // Completed 载荷：Succeeded/None，percent=100，processed==total，finishedAt 非空
        var c = completed[0].Payload;
        Assert.Equal("Succeeded", c.GetProperty("status").GetString());
        Assert.Equal("None", c.GetProperty("phase").GetString());
        Assert.Equal(100, c.GetProperty("percent").GetDouble());
        Assert.True(c.GetProperty("totalUnits").GetInt32() >= 3);
        Assert.Equal(c.GetProperty("totalUnits").GetInt32(), c.GetProperty("processedUnits").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, c.GetProperty("finishedAt").ValueKind);

        // 每条 Progress：status/phase 必为字符串枚举（SignalR 与 REST 一致），percent ∈ [0,100]
        Assert.All(progress, e =>
        {
            Assert.Equal(JsonValueKind.String, e.Payload.GetProperty("status").ValueKind);
            Assert.Equal(JsonValueKind.String, e.Payload.GetProperty("phase").ValueKind);
            Assert.InRange(e.Payload.GetProperty("percent").GetDouble(), 0, 100);
        });

        // 阶段跃迁被广播：Running（Planning）与 Embedding 阶段至少各出现一次
        Assert.Contains(progress, e => e.Payload.GetProperty("status").GetString() == "Running");
        Assert.Contains(progress, e => e.Payload.GetProperty("phase").GetString() == "Embedding");
    }

    // ==================== G-M6：取消路径 Cancelling → Cancelled ====================

    [Fact]
    public async Task GM6_CancelRunningJob_ShouldBroadcastCancellingThenCancelled()
    {
        var gate = new TaskCompletionSource();
        var publisher = new FakeIndexEventPublisher();
        var service = NewService(publisher, new FakeEmbeddingAdapterOptions { FirstBatchGate = gate });
        var kb = await SeedLexicalKbAsync(service, "GM6 Cancel KB", documentCount: 3);
        await _catalog.SaveEmbeddingSpecAsync(TargetSpec("spec_gm6_cancel", 16));

        var job = await service.TriggerModelReindexJobAsync(kb.Id, "spec_gm6_cancel");
        await WaitUntilAsync(async () => (await service.GetReindexStatusAsync(kb.Id)).Generation.ShadowExists);

        Assert.True(await service.CancelJobAsync(job.Id));
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job.Id);
        Assert.Equal(JobStatus.Cancelled, terminal.Status);
        gate.SetResult();

        var events = publisher.Events;
        // 取消请求广播 Cancelling（非终态 → Progress）
        Assert.Contains(events, e =>
            e.Name == "ReindexJobProgress" && e.Payload.GetProperty("status").GetString() == "Cancelling");

        // worker 收敛广播 Cancelled（终态 → Completed），恰好一次
        var completed = events.Where(e => e.Name == "ReindexJobCompleted").ToArray();
        Assert.Single(completed);
        Assert.Equal("Cancelled", completed[0].Payload.GetProperty("status").GetString());
        Assert.Equal("用户取消了重构索引作业", completed[0].Payload.GetProperty("errorMessage").GetString());
    }

    // ==================== G-M6：发布异常绝不破坏作业主流程 ====================

    [Fact]
    public async Task GM6_PublisherThrows_ShouldNotBreakReindexJob()
    {
        var publisher = new FakeIndexEventPublisher { ThrowOnPublish = new InvalidOperationException("hub down") };
        var service = NewService(publisher);
        var kb = await SeedLexicalKbAsync(service, "GM6 Resilient KB", documentCount: 2);
        await _catalog.SaveEmbeddingSpecAsync(TargetSpec("spec_gm6_resilient", 16));

        var job = await service.TriggerModelReindexJobAsync(kb.Id, "spec_gm6_resilient");
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job.Id);

        // 广播全程抛异常，作业仍正常完成；事件在抛出前已被记录（证明每次都尝试广播）
        Assert.Equal(JobStatus.Succeeded, terminal.Status);
        Assert.Contains(publisher.Events, e => e.Name == "ReindexJobStarted");
        Assert.Contains(publisher.Events, e => e.Name == "ReindexJobCompleted");
        var switched = await _catalog.GetKnowledgeBaseAsync(kb.Id);
        Assert.Equal("spec_gm6_resilient", switched!.EmbeddingSpecId);
    }

    // ==================== G-M6：失败路径广播 Failed Completed ====================

    [Fact]
    public async Task GM6_DimensionMismatch_ShouldBroadcastFailedCompleted_WithError()
    {
        var publisher = new FakeIndexEventPublisher();
        var service = NewService(publisher, new FakeEmbeddingAdapterOptions { DimensionOverride = 384 });
        var kb = await SeedLexicalKbAsync(service, "GM6 Fail KB");
        await _catalog.SaveEmbeddingSpecAsync(TargetSpec("spec_gm6_fail", 1024));

        var job = await service.TriggerModelReindexJobAsync(kb.Id, "spec_gm6_fail");
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job.Id);
        Assert.Equal(JobStatus.Failed, terminal.Status);

        var completed = publisher.Events.Where(e => e.Name == "ReindexJobCompleted").ToArray();
        Assert.Single(completed);
        var c = completed[0].Payload;
        Assert.Equal("Failed", c.GetProperty("status").GetString());
        Assert.Contains("维度不匹配", c.GetProperty("errorMessage").GetString());
        Assert.NotEqual(JsonValueKind.Null, c.GetProperty("finishedAt").ValueKind);
    }
}