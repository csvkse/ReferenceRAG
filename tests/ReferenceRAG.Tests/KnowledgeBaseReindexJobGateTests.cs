using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Features.ModelManagement.Adapters;
using ReferenceRAG.Core.Features.Search.Fusion;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Tests;

/// <summary>
/// 重构索引作业化回归门禁（docs/vector-model-switch-reindex-timeout-root-cause-and-async-job-architecture.md §6）：
/// G3 维度/模拟模式硬门禁、G4 同库单飞 409、G5 运行中取消清理影子、G6 纯词法→混合真实非零向量 E2E、
/// G8 GGUF 滑窗全覆盖无静默截断、G9 旧代保留与回滚；另覆盖崩溃孤儿作业裁决与影子断点续跑。
/// G1/G2（入队时延与请求生命周期解耦）位于 Host.Tests 控制器层；G7（前端契约）位于 wwwroot/tests。
/// </summary>
public sealed class KnowledgeBaseReindexJobGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dataDir;
    private readonly CatalogStore _catalog;
    private readonly KnowledgeBaseStorageManager _storageManager;
    private readonly ConfigManager _configManager;

    public KnowledgeBaseReindexJobGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RefRAG_ReindexJobGate_" + Guid.NewGuid().ToString("N"));
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

    private (KnowledgeBaseService Service, FakeEmbeddingAdapterResolver Resolver) NewService(FakeEmbeddingAdapterOptions? options = null)
    {
        var resolver = new FakeEmbeddingAdapterResolver(options);
        var service = new KnowledgeBaseService(
            _catalog,
            _storageManager,
            logger: NullLogger<KnowledgeBaseService>.Instance,
            configManager: _configManager,
            adapterResolver: resolver);
        return (service, resolver);
    }

    private async Task<KnowledgeBaseDefinition> SeedLexicalKbAsync(KnowledgeBaseService service, string name, int documentCount = 2)
    {
        var kb = await service.CreateKnowledgeBaseAsync(
            name: name,
            description: "reindex job gate",
            embeddingSpecId: "spec_lexical_only");

        for (int i = 0; i < documentCount; i++)
        {
            await service.CreateDocumentAsync(
                kb.Id,
                connectorId: "manual",
                title: $"Gate Doc {i}",
                content: $"# 章节 {i}\n独孤九剑总诀式第{i}章：归妹趋无妄，无妄趋同人。\n\n## 小节 {i}\n风清扬传授令狐冲剑意，料敌机先，后发先至。段落编号 {i}。");
        }
        return kb;
    }

    private static EmbeddingSpec TargetSpec(string id, int dimension, string? documentPrefix = null) => new(
        Id: id,
        ModelName: "gate-target-model",
        Dimension: dimension,
        Metric: "cosine",
        DocumentPrefix: documentPrefix,
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

    private IReadOnlyList<(string ChunkId, int Dimension, float[] Vector)> ReadActiveVectors(string kbId)
    {
        var dbPath = Path.Combine(_dataDir, "kbs", kbId, "kb.db");
        var rows = new List<(string, int, float[])>();
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT chunk_id, dimension, vector_blob FROM chunk_vectors;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var blob = (byte[])reader.GetValue(2);
            var floats = new float[blob.Length / 4];
            Buffer.BlockCopy(blob, 0, floats, 0, blob.Length);
            rows.Add((reader.GetString(0), reader.GetInt32(1), floats));
        }
        return rows;
    }

    // ==================== G3：维度不匹配 / 模拟模式 → Failed，现役数据零污染 ====================

    [Fact]
    public async Task G3_DimensionMismatch_ShouldFailJob_WithoutTouchingActiveGeneration()
    {
        var (service, resolver) = NewService(new FakeEmbeddingAdapterOptions { DimensionOverride = 384 });
        var kb = await SeedLexicalKbAsync(service, "G3 Dim KB");
        var before = await service.GetReindexStatusAsync(kb.Id);

        await _catalog.SaveEmbeddingSpecAsync(TargetSpec("spec_g3_1024", 1024));
        var job = await service.TriggerModelReindexJobAsync(kb.Id, "spec_g3_1024");
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job.Id);

        Assert.Equal(JobStatus.Failed, terminal.Status);
        Assert.Contains("维度不匹配", terminal.ErrorMessage);
        Assert.Equal(0, resolver.Stats.BatchCalls);

        var after = await service.GetReindexStatusAsync(kb.Id);
        Assert.False(after.Generation.ShadowExists);
        Assert.Equal(before.Generation.ActiveVectorCount, after.Generation.ActiveVectorCount);
        Assert.Equal(before.Generation.ActiveSpecId, after.Generation.ActiveSpecId);
        var unchangedKb = await _catalog.GetKnowledgeBaseAsync(kb.Id);
        Assert.Equal(kb.EmbeddingSpecId, unchangedKb!.EmbeddingSpecId);
    }

    [Fact]
    public async Task G3_SimulationMode_ShouldFailJob_WithoutTouchingActiveGeneration()
    {
        var (service, resolver) = NewService(new FakeEmbeddingAdapterOptions { SimulationMode = true });
        var kb = await SeedLexicalKbAsync(service, "G3 Sim KB");

        await _catalog.SaveEmbeddingSpecAsync(TargetSpec("spec_g3_sim", 16));
        var job = await service.TriggerModelReindexJobAsync(kb.Id, "spec_g3_sim");
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job.Id);

        Assert.Equal(JobStatus.Failed, terminal.Status);
        Assert.Contains("模拟", terminal.ErrorMessage);
        Assert.Equal(0, resolver.Stats.CreateCalls); // 解析器在创建适配器前即拦截
        Assert.Equal(0, resolver.Stats.BatchCalls);

        var after = await service.GetReindexStatusAsync(kb.Id);
        Assert.False(after.Generation.ShadowExists);
        Assert.Equal(0, after.Generation.ActiveVectorCount);
        var unchangedKb = await _catalog.GetKnowledgeBaseAsync(kb.Id);
        Assert.Equal("spec_lexical_only", unchangedKb!.EmbeddingSpecId);
    }

    // ==================== G4：同库单飞 409，跨库互不影响 ====================

    [Fact]
    public async Task G4_SameKbConcurrentReindex_ShouldConflict_WhileDifferentKbProceedsIndependently()
    {
        var gate = new TaskCompletionSource();
        var (service, _) = NewService(new FakeEmbeddingAdapterOptions { FirstBatchGate = gate });
        var kbA = await SeedLexicalKbAsync(service, "G4 KB A");
        var kbB = await SeedLexicalKbAsync(service, "G4 KB B");

        await _catalog.SaveEmbeddingSpecAsync(TargetSpec("spec_g4", 16));
        var jobA = await service.TriggerModelReindexJobAsync(kbA.Id, "spec_g4");

        // 同库第二个作业：单飞占位 → 冲突异常（控制器映射 409）
        var conflict = await Assert.ThrowsAsync<KnowledgeBaseJobConflictException>(
            () => service.TriggerModelReindexJobAsync(kbA.Id, "spec_g4"));
        Assert.Contains(jobA.Id, conflict.Message);

        // 跨库作业不受影响
        var jobB = await service.TriggerModelReindexJobAsync(kbB.Id, "spec_g4");

        gate.SetResult();
        var terminalA = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, jobA.Id);
        var terminalB = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, jobB.Id);
        Assert.Equal(JobStatus.Succeeded, terminalA.Status);
        Assert.Equal(JobStatus.Succeeded, terminalB.Status);

        // 终态后单飞占位已释放：同库可再次入队
        var jobA2 = await service.TriggerModelReindexJobAsync(kbA.Id, "spec_g4");
        var terminalA2 = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, jobA2.Id);
        Assert.Equal(JobStatus.Succeeded, terminalA2.Status);
    }

    // ==================== G5：运行中取消 → Cancelled + 影子清理 + 现役不变 + 可重新入队 ====================

    [Fact]
    public async Task G5_CancelRunningJob_ShouldDropShadow_KeepActiveGeneration_AndAllowReenqueue()
    {
        var gate = new TaskCompletionSource();
        var (service, _) = NewService(new FakeEmbeddingAdapterOptions { FirstBatchGate = gate });
        var kb = await SeedLexicalKbAsync(service, "G5 KB", documentCount: 3);

        await _catalog.SaveEmbeddingSpecAsync(TargetSpec("spec_g5", 16));
        var job = await service.TriggerModelReindexJobAsync(kb.Id, "spec_g5");

        // 等待影子表已建立且首批编码被闸门阻塞（真实运行中状态）
        await WaitUntilAsync(async () => (await service.GetReindexStatusAsync(kb.Id)).Generation.ShadowExists);

        Assert.True(await service.CancelJobAsync(job.Id));
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job.Id);
        Assert.Equal(JobStatus.Cancelled, terminal.Status);

        var after = await service.GetReindexStatusAsync(kb.Id);
        Assert.False(after.Generation.ShadowExists);
        Assert.Equal(0, after.Generation.ActiveVectorCount);
        Assert.Null(after.ActiveJob);
        var unchangedKb = await _catalog.GetKnowledgeBaseAsync(kb.Id);
        Assert.Equal("spec_lexical_only", unchangedKb!.EmbeddingSpecId);

        // 释放闸门后重新入队：单飞占位已释放，作业正常完成
        gate.SetResult();
        var job2 = await service.TriggerModelReindexJobAsync(kb.Id, "spec_g5");
        var terminal2 = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job2.Id);
        Assert.Equal(JobStatus.Succeeded, terminal2.Status);
        var switchedKb = await _catalog.GetKnowledgeBaseAsync(kb.Id);
        Assert.Equal("spec_g5", switchedKb!.EmbeddingSpecId);
    }

    // ==================== G6：纯词法 → 混合 E2E（真实非零向量，维度==规格），解绑后词法完好 ====================

    [Fact]
    public async Task G6_LexicalToHybrid_ShouldPersistRealNonZeroVectors_AndUnbindKeepsLexicalIntact()
    {
        var (service, resolver) = NewService();
        var kb = await SeedLexicalKbAsync(service, "G6 KB", documentCount: 3);
        var spec = TargetSpec("spec_g6_16d", 16, documentPrefix: "passage: ");
        await _catalog.SaveEmbeddingSpecAsync(spec);

        var job = await service.TriggerModelReindexJobAsync(kb.Id, spec.Id);
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job.Id);
        Assert.Equal(JobStatus.Succeeded, terminal.Status);
        Assert.True(terminal.ProcessedUnits >= 3);

        var upgraded = await _catalog.GetKnowledgeBaseAsync(kb.Id);
        Assert.Equal(spec.Id, upgraded!.EmbeddingSpecId);
        Assert.False(upgraded.IsPureLexical);
        Assert.Equal(KnowledgeBaseMode.Hybrid, upgraded.Mode);

        // 物理验收：每个活跃切片都有维度==16 的非零单位向量，且与目标适配器（含 DocumentPrefix）输出逐位一致
        await using var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadOnly);
        var (chunks, chunkTotal) = await lease.Store.GetChunksAsync(pageSize: 500);
        Assert.True(chunkTotal >= 3);
        var vectors = ReadActiveVectors(kb.Id);
        Assert.Equal(chunks.Count(c => c.Enabled), vectors.Count);

        var chunkById = chunks.ToDictionary(c => c.Id);
        foreach (var (chunkId, dimension, vector) in vectors)
        {
            Assert.Equal(16, dimension);
            Assert.Equal(16, vector.Length);
            var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
            Assert.True(norm > 0.99 && norm < 1.01, $"向量必须为非零单位向量，实际范数 {norm}");

            var chunk = chunkById[chunkId];
            var expected = FakeEmbeddingAdapter.EncodeDeterministicUnitVector(
                "passage: " + (chunk.EmbeddingText ?? chunk.Text), 16);
            Assert.Equal(expected, vector);
        }
        Assert.True(resolver.Stats.EncodedTexts >= chunks.Count(c => c.Enabled));

        // 解绑降级：词法检索与切片数据完好无损
        var downgraded = await service.UnbindModelAsync(kb.Id);
        Assert.True(downgraded.IsPureLexical);
        Assert.Equal(KnowledgeBaseMode.PureLexical, downgraded.Mode);

        var searchService = new KnowledgeBaseSearchService(
            _catalog,
            _storageManager,
            configManager: _configManager,
            embeddingService: null,
            logger: NullLogger<KnowledgeBaseSearchService>.Instance);
        var response = await searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "独孤九剑",
            KnowledgeBaseIds: new[] { kb.Id },
            TopK: 5,
            UseLexicalTokenizer: true));
        Assert.NotEmpty(response.Items);
        Assert.Contains("独孤九剑", response.Items[0].Text);

        var (chunksAfter, totalAfter) = await lease.Store.GetChunksAsync(pageSize: 500);
        Assert.Equal(chunkTotal, totalAfter);
    }

    // ==================== G8：GGUF 超长文本滑窗切分 —— 全覆盖、无越界、无静默截断 ====================

    [Fact]
    public void G8_SplitTokenWindows_LongSequence_ShouldCoverEveryTokenWithoutTruncation()
    {
        const int capacity = 512;
        const int overlap = 64;
        var tokens = Enumerable.Range(0, 2000).ToArray();

        var windows = LlamaEmbeddingAdapter.SplitTokenWindows(tokens, capacity, overlap);

        Assert.True(windows.Count > 1);
        Assert.All(windows, w => Assert.True(w.Length <= capacity));

        // 按步长去重叠重建：必须与原始序列逐位一致（零丢失、零越界）
        var stride = capacity - overlap;
        var rebuilt = new List<int>();
        for (int i = 0; i < windows.Count; i++)
        {
            var skip = i == 0 ? 0 : overlap;
            rebuilt.AddRange(windows[i].Skip(skip));
        }
        Assert.Equal(tokens, rebuilt);
        Assert.Equal(tokens[^1], windows[^1][^1]);
    }

    [Fact]
    public void G8_SplitTokenWindows_ShortOrEmpty_ShouldReturnSingleWindow()
    {
        var shortTokens = Enumerable.Range(0, 100).ToArray();
        var windows = LlamaEmbeddingAdapter.SplitTokenWindows(shortTokens, 512);
        Assert.Single(windows);
        Assert.Equal(shortTokens, windows[0]);

        var exact = Enumerable.Range(0, 512).ToArray();
        Assert.Single(LlamaEmbeddingAdapter.SplitTokenWindows(exact, 512));

        var empty = LlamaEmbeddingAdapter.SplitTokenWindows(Array.Empty<int>(), 512);
        Assert.Single(empty);
        Assert.Empty(empty[0]);
    }

    [Fact]
    public void G8_SplitTokenWindows_ExcessiveOverlap_ShouldClampAndStayProgressive()
    {
        var tokens = Enumerable.Range(0, 1500).ToArray();
        var windows = LlamaEmbeddingAdapter.SplitTokenWindows(tokens, 512, overlap: 5000);

        // overlap 被钳制到 capacity/2，步长恒正 → 必然收敛覆盖全序列
        var stride = 512 - 256;
        var rebuilt = new List<int>();
        for (int i = 0; i < windows.Count; i++)
        {
            rebuilt.AddRange(windows[i].Skip(i == 0 ? 0 : 256));
        }
        Assert.Equal(tokens, rebuilt);
        Assert.Equal((int)Math.Ceiling((1500 - 512) / (double)stride) + 1, windows.Count);

        Assert.Throws<ArgumentOutOfRangeException>(() => LlamaEmbeddingAdapter.SplitTokenWindows(tokens, 0));
    }

    // ==================== G9：旧代保留 + 回滚恢复上一代检索 ====================

    [Fact]
    public async Task G9_SecondReindex_ShouldRetainOldGeneration_AndRollbackRestoresIt()
    {
        var (service, _) = NewService();
        var kb = await SeedLexicalKbAsync(service, "G9 KB", documentCount: 2);

        var specA = TargetSpec("spec_g9_a8", 8);
        var specB = TargetSpec("spec_g9_b12", 12);
        await _catalog.SaveEmbeddingSpecAsync(specA);
        await _catalog.SaveEmbeddingSpecAsync(specB);

        // 从未重构的库：无旧代 → 回滚必须被拒绝
        var neverReindexed = await SeedLexicalKbAsync(service, "G9 Never KB", documentCount: 1);
        var noOldEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RollbackModelReindexAsync(neverReindexed.Id));
        Assert.Contains("没有可回滚", noOldEx.Message);

        var jobA = await service.TriggerModelReindexJobAsync(kb.Id, specA.Id);
        Assert.Equal(JobStatus.Succeeded, (await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, jobA.Id)).Status);

        // 首次升级：升级前的空向量代（纯词法态）也被保留，可一键退回词法
        var genAfterA = await service.GetReindexStatusAsync(kb.Id);
        Assert.True(genAfterA.Generation.CanRollback);
        Assert.Null(genAfterA.Generation.PreviousSpecId);

        // 第二次升级：上一代（A）必须被保留
        var jobB = await service.TriggerModelReindexJobAsync(kb.Id, specB.Id);
        Assert.Equal(JobStatus.Succeeded, (await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, jobB.Id)).Status);

        var genAfterB = await service.GetReindexStatusAsync(kb.Id);
        Assert.True(genAfterB.Generation.CanRollback);
        Assert.Equal(specA.Id, genAfterB.Generation.PreviousSpecId);
        Assert.Equal(specB.Id, genAfterB.Generation.ActiveSpecId);
        Assert.All(ReadActiveVectors(kb.Id), row => Assert.Equal(12, row.Dimension));

        // 回滚：恢复 A 代向量与规格绑定
        var rolledBack = await service.RollbackModelReindexAsync(kb.Id);
        Assert.Equal(specA.Id, rolledBack.EmbeddingSpecId);

        var genAfterRollback = await service.GetReindexStatusAsync(kb.Id);
        Assert.Equal(specA.Id, genAfterRollback.Generation.ActiveSpecId);
        Assert.False(genAfterRollback.Generation.CanRollback); // 回滚消耗旧代（一次性）
        Assert.All(ReadActiveVectors(kb.Id), row => Assert.Equal(8, row.Dimension));

        // 回滚后检索正常（存储完整性）
        var searchService = new KnowledgeBaseSearchService(
            _catalog,
            _storageManager,
            fusionStrategy: new RrfSearchFusionStrategy(),
            configManager: _configManager);
        var resp = await searchService.SearchAsync(new CrossKnowledgeBaseSearchRequest(
            Query: "独孤九剑",
            KnowledgeBaseIds: new[] { kb.Id },
            TopK: 5,
            UseLexicalTokenizer: true));
        Assert.NotEmpty(resp.Items);
        Assert.Contains(kb.Id, resp.SuccessfulKnowledgeBaseIds);
    }

    [Fact]
    public async Task G9_FirstUpgradeRollback_ShouldRestorePureLexicalState()
    {
        var (service, _) = NewService();
        var kb = await SeedLexicalKbAsync(service, "G9 First KB", documentCount: 2);
        var spec = TargetSpec("spec_g9_first", 16);
        await _catalog.SaveEmbeddingSpecAsync(spec);

        var job = await service.TriggerModelReindexJobAsync(kb.Id, spec.Id);
        Assert.Equal(JobStatus.Succeeded, (await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job.Id)).Status);
        Assert.False((await _catalog.GetKnowledgeBaseAsync(kb.Id))!.IsPureLexical);

        // 回滚首次绑定：恢复到升级前的纯词法态（空向量代 + spec_lexical_only 兜底）
        var rolledBack = await service.RollbackModelReindexAsync(kb.Id);
        Assert.Equal("spec_lexical_only", rolledBack.EmbeddingSpecId);
        Assert.True(rolledBack.IsPureLexical);
        Assert.Equal(KnowledgeBaseMode.PureLexical, rolledBack.Mode);

        var gen = await service.GetReindexStatusAsync(kb.Id);
        Assert.Equal(0, gen.Generation.ActiveVectorCount);
        Assert.False(gen.Generation.CanRollback);
    }

    // ==================== 崩溃恢复：孤儿 Running 作业裁决 + 影子断点续跑 ====================

    [Fact]
    public async Task CrashRecovery_OrphanRunningJob_ShouldBeMarkedFailed_WithResumeHint()
    {
        var (service, _) = NewService();
        var kb = await SeedLexicalKbAsync(service, "Orphan KB");

        // 模拟进程重启前残留的 Running 作业（当前协调器无占位）
        var orphan = new JobDefinition(
            Id: "orphan_job_01",
            Operation: JobOperation.Reindex,
            KnowledgeBaseId: kb.Id,
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Running,
            Phase: OperationPhase.Embedding,
            TotalUnits: 10,
            ProcessedUnits: 4,
            IdempotencyKey: $"{kb.Id}:reindex:spec_x",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow.AddMinutes(-5),
            FinishedAt: null);
        await _catalog.SaveJobAsync(orphan);

        var status = await service.GetReindexStatusAsync(kb.Id);
        Assert.Null(status.ActiveJob);

        var persisted = await _catalog.GetJobAsync(orphan.Id);
        Assert.Equal(JobStatus.Failed, persisted!.Status);
        Assert.Contains("断点续跑", persisted.ErrorMessage);
    }

    [Fact]
    public async Task CrashRecovery_MatchingShadowResidue_ShouldResumeAndOnlyEncodePendingChunks()
    {
        var (service, resolver) = NewService();
        var kb = await SeedLexicalKbAsync(service, "Resume KB", documentCount: 2);
        var spec = TargetSpec("spec_resume_16", 16);
        await _catalog.SaveEmbeddingSpecAsync(spec);

        await using var lease = await _storageManager.AcquireLeaseAsync(kb.Id, StorageAccess.ReadWrite);
        var (chunks, _) = await lease.Store.GetChunksAsync(pageSize: 500);
        var enabledChunks = chunks.Where(c => c.Enabled).ToList();
        Assert.True(enabledChunks.Count >= 2);

        // 预置「崩溃残留」：第一个切片已按目标 cohort/维度/source_version 完成编码
        var dbPath = Path.Combine(_dataDir, "kbs", kb.Id, "kb.db");
        var first = enabledChunks[0];
        var doneVector = FakeEmbeddingAdapter.EncodeDeterministicUnitVector(first.EmbeddingText ?? first.Text, 16);
        var blob = new byte[doneVector.Length * 4];
        Buffer.BlockCopy(doneVector, 0, blob, 0, blob.Length);

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE chunk_vectors_shadow (
                    chunk_id TEXT PRIMARY KEY,
                    document_id TEXT NOT NULL,
                    dimension INTEGER NOT NULL,
                    vector_blob BLOB NOT NULL,
                    producer_cohort TEXT,
                    source_version INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL
                );
                INSERT INTO chunk_vectors_shadow (chunk_id, document_id, dimension, vector_blob, producer_cohort, source_version, created_at)
                VALUES (@chunk, @doc, 16, @blob, @cohort, @version, @now);";
            cmd.Parameters.AddWithValue("@chunk", first.Id);
            cmd.Parameters.AddWithValue("@doc", first.DocumentId);
            cmd.Parameters.AddWithValue("@blob", blob);
            cmd.Parameters.AddWithValue("@cohort", spec.Id);
            cmd.Parameters.AddWithValue("@version", first.DocumentVersion);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        var job = await service.TriggerModelReindexJobAsync(kb.Id, spec.Id);
        var terminal = await ReindexJobTestHelpers.AwaitTerminalAsync(_catalog, job.Id);
        Assert.Equal(JobStatus.Succeeded, terminal.Status);

        // 续跑命中：已完成切片不再重编码，仅补齐剩余 N-1 个
        Assert.Equal(enabledChunks.Count - 1, resolver.Stats.EncodedTexts);
        var vectors = ReadActiveVectors(kb.Id);
        Assert.Equal(enabledChunks.Count, vectors.Count);
        Assert.Contains(vectors, v => v.ChunkId == first.Id && v.Vector.SequenceEqual(doneVector));
    }
}