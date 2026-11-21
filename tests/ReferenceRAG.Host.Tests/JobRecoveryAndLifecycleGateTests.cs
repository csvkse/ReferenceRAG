using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReferenceRAG.Business.Features.KnowledgeBase.Services;
using ReferenceRAG.Core.Features.KnowledgeBase.Domain;
using ReferenceRAG.Core.Features.KnowledgeBase.Storage;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Models;
using ReferenceRAG.Core.Services;
using ReferenceRAG.Infrastructure.Features.KnowledgeBase.Storage;
using Xunit;

namespace ReferenceRAG.Host.Tests;

/// <summary>
/// 针对作业中断恢复、单飞互斥、CAS 状态机防线与生命周期的质量门禁测试
/// 对应 docs/rechunk-stuck-54-percent-investigation-and-recovery-design.md 第 7 节验收规格
/// </summary>
public sealed class JobRecoveryAndLifecycleGateTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _catalogDbPath;
    private readonly CatalogStore _catalogStore;

    public JobRecoveryAndLifecycleGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "referencerag_lifecycle_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _catalogDbPath = Path.Combine(_tempDir, "catalog.db");
        _catalogStore = new CatalogStore(_catalogDbPath, NullLogger<CatalogStore>.Instance);
    }

    public void Dispose()
    {
        _catalogStore.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    /// <summary>
    /// G-JOB-RECOVERY-01: 启动恢复门禁
    /// 当宿主进程启动时，执行 JobRecoveryService 扫描，历史遗留的 Running 与 Cancelling 孤儿作业
    /// 必须在启动期安全收口为终止状态（Failed/Cancelled），且保留原始进度单位与诊断信息，绝不无限挂起在 54%。
    /// </summary>
    [Fact]
    public async Task G_JOB_RECOVERY_01_Startup_OrphanRunningJob_MustBeMarkedFailedWithInterruptedError()
    {
        // 1. 初始化数据库并写入前一进程遗留的孤儿作业（模拟 54% 停滞场景）
        await _catalogStore.InitializeAsync();
        var orphanJobId = "324b7e62450e4d3b";
        var orphanJob = new JobDefinition(
            Id: orphanJobId,
            Operation: JobOperation.Sync,
            KnowledgeBaseId: "kb-notes",
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Running,
            Phase: OperationPhase.Chunking,
            TotalUnits: 6765,
            ProcessedUnits: 3677,
            IdempotencyKey: "test:orphan:1",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow.AddHours(-15),
            FinishedAt: null
        );
        await _catalogStore.SaveJobAsync(orphanJob);

        var cancellingJobId = "cancelling_orphan_01";
        var cancellingJob = new JobDefinition(
            Id: cancellingJobId,
            Operation: JobOperation.Reindex,
            KnowledgeBaseId: "kb-notes",
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Cancelling,
            Phase: OperationPhase.Rendering,
            TotalUnits: 100,
            ProcessedUnits: 50,
            IdempotencyKey: "test:orphan:2",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow.AddHours(-10),
            FinishedAt: null
        );
        await _catalogStore.SaveJobAsync(cancellingJob);

        // 2. 模拟新宿主启动：协调器为空，执行 JobRecoveryService
        var coordinator = new KnowledgeBaseJobCoordinator();
        var services = new ServiceCollection();
        services.AddSingleton<ICatalogStore>(_catalogStore);
        var serviceProvider = services.BuildServiceProvider();

        var recoveryService = new JobRecoveryService(
            serviceProvider,
            coordinator,
            NullLogger<JobRecoveryService>.Instance);

        await recoveryService.StartAsync(CancellationToken.None);

        // 3. 验证对账结果
        var recoveredRunning = await _catalogStore.GetJobAsync(orphanJobId);
        Assert.NotNull(recoveredRunning);
        Assert.Equal(JobStatus.Failed, recoveredRunning.Status);
        Assert.NotNull(recoveredRunning.FinishedAt);
        Assert.Contains("重启或异常中断", recoveredRunning.ErrorMessage);
        Assert.Equal(3677, recoveredRunning.ProcessedUnits);
        Assert.Equal(6765, recoveredRunning.TotalUnits);

        var recoveredCancelling = await _catalogStore.GetJobAsync(cancellingJobId);
        Assert.NotNull(recoveredCancelling);
        Assert.Equal(JobStatus.Cancelled, recoveredCancelling.Status);
        Assert.NotNull(recoveredCancelling.FinishedAt);

        // 4. 再次查询活跃作业，数量必须为 0
        var activeJobs = await _catalogStore.GetActiveJobsAsync();
        Assert.Empty(activeJobs);
    }

    /// <summary>
    /// G-JOB-CANCEL-01: 跨进程或无本地 CTS 取消门禁
    /// 当用户在 UI 请求取消一个旧作业时，即使当前进程内存中没有对应的 CTS（跨重启孤儿），
    /// CancelJobAsync 必须能安全且确定性地将其在 Catalog 中收口为 Cancelled 并返回 true，杜绝返回 404。
    /// </summary>
    [Fact]
    public async Task G_JOB_CANCEL_01_CancelJob_WithoutInMemoryCts_MustTransitionToCancelled()
    {
        await _catalogStore.InitializeAsync();
        var jobId = "stuck_job_no_cts";
        var stuckJob = new JobDefinition(
            Id: jobId,
            Operation: JobOperation.Sync,
            KnowledgeBaseId: "kb-wiki",
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Running,
            Phase: OperationPhase.Chunking,
            TotalUnits: 1000,
            ProcessedUnits: 400,
            IdempotencyKey: "test:no_cts:1",
            ErrorMessage: null,
            CreatedAt: DateTime.UtcNow.AddHours(-2),
            FinishedAt: null
        );
        await _catalogStore.SaveJobAsync(stuckJob);

        var coordinator = new KnowledgeBaseJobCoordinator(); // 没有注册 jobId 的 CTS
        var storageManagerMock = new Mock<IKnowledgeBaseStorageManager>();

        var kbService = new KnowledgeBaseService(
            catalog: _catalogStore,
            storageManager: storageManagerMock.Object,
            jobCoordinator: coordinator,
            logger: NullLogger<KnowledgeBaseService>.Instance);

        // 执行取消
        var cancelSuccess = await kbService.CancelJobAsync(jobId);
        Assert.True(cancelSuccess);

        var updatedJob = await _catalogStore.GetJobAsync(jobId);
        Assert.NotNull(updatedJob);
        Assert.Equal(JobStatus.Cancelled, updatedJob.Status);
        Assert.NotNull(updatedJob.FinishedAt);
        Assert.Contains("取消", updatedJob.ErrorMessage);
    }

    /// <summary>
    /// G-JOB-STATE-01: CAS 终态防线门禁
    /// 无论状态机或晚到的 worker 线程何时发起更新，一旦数据库中作业已进入终态（Succeeded, PartiallySucceeded, Failed, Cancelled），
    /// 迟到的 Running/进度更新绝对不能覆盖终态，FinishedAt 绝对不能倒退为 null。
    /// </summary>
    [Fact]
    public async Task G_JOB_STATE_01_JobState_TerminalState_CannotBeOverwrittenByLateRunningProgress()
    {
        await _catalogStore.InitializeAsync();
        var jobId = "cas_terminal_job_01";
        var initialJob = new JobDefinition(
            Id: jobId,
            Operation: JobOperation.Sync,
            KnowledgeBaseId: "kb-test",
            ExpectedIndexEpoch: 1,
            Status: JobStatus.Cancelled,
            Phase: OperationPhase.None,
            TotalUnits: 500,
            ProcessedUnits: 250,
            IdempotencyKey: "test:cas:1",
            ErrorMessage: "用户手动取消任务",
            CreatedAt: DateTime.UtcNow.AddHours(-1),
            FinishedAt: DateTime.UtcNow
        );
        await _catalogStore.SaveJobAsync(initialJob);

        // 模拟迟到的 worker 线程试图写回 Running 和新进度
        var staleUpdate = initialJob with
        {
            Status = JobStatus.Running,
            Phase = OperationPhase.Chunking,
            ProcessedUnits = 499,
            FinishedAt = null,
            ErrorMessage = null
        };

        var committed = await _catalogStore.SaveJobAsync(staleUpdate);

        // 断言返回记录与数据库内真实记录均保持 Cancelled 终态
        Assert.Equal(JobStatus.Cancelled, committed.Status);
        Assert.NotNull(committed.FinishedAt);

        var reloaded = await _catalogStore.GetJobAsync(jobId);
        Assert.NotNull(reloaded);
        Assert.Equal(JobStatus.Cancelled, reloaded.Status);
        Assert.NotNull(reloaded.FinishedAt);
        Assert.Equal(250, reloaded.ProcessedUnits); // 迟到的进度也被拒绝
    }

    /// <summary>
    /// G-KB-MUTATION-01: 同库长时写操作单飞互斥门禁
    /// 同一知识库在已有进行中长时任务（sync、rechunk、reindex）时，后续并发写操作必须被互斥门禁阻断，
    /// 抛出 KnowledgeBaseJobConflictException（对应 HTTP 409 Conflict），任务完成后互斥位释放。
    /// </summary>
    [Fact]
    public async Task G_KB_MUTATION_01_KnowledgeBase_ConcurrentSync_MustBeBlockedByMutex()
    {
        await _catalogStore.InitializeAsync();
        var kbId = "kb-concurrency-test";
        await _catalogStore.SaveKnowledgeBaseAsync(new KnowledgeBaseDefinition(
            Id: kbId,
            Name: "Concurrency Test KB",
            Description: null,
            Enabled: true,
            DesiredConfigRevision: 1,
            EffectiveConfigRevision: 1,
            EmbeddingSpecId: "spec_lexical_only",
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow
        ));

        var conn = await _catalogStore.SaveConnectorAsync(new ConnectorDefinition(
            Id: "conn-1",
            KnowledgeBaseId: kbId,
            Kind: "localfolder",
            TargetPath: _tempDir,
            FilePattern: "*.md",
            ConfigRevision: 1,
            Enabled: true,
            CreatedAt: DateTime.UtcNow,
            UpdatedAt: DateTime.UtcNow
        ));

        var coordinator = new KnowledgeBaseJobCoordinator();
        var storageManagerMock = new Mock<IKnowledgeBaseStorageManager>();
        var queueMock = new Mock<IKnowledgeBaseJobQueue>();

        var kbService = new KnowledgeBaseService(
            catalog: _catalogStore,
            storageManager: storageManagerMock.Object,
            jobCoordinator: coordinator,
            jobQueue: queueMock.Object,
            logger: NullLogger<KnowledgeBaseService>.Instance);

        // 1. 第一个同步作业正常启动并占据互斥位
        var firstJob = await kbService.TriggerSyncJobAsync(kbId, conn.Id);
        Assert.NotNull(firstJob);
        Assert.Equal(JobStatus.Queued, firstJob.Status);

        // 2. 第二个并发同步请求应当直接抛出 KnowledgeBaseJobConflictException (HTTP 409)
        var ex = await Assert.ThrowsAsync<KnowledgeBaseJobConflictException>(() =>
            kbService.TriggerSyncJobAsync(kbId, conn.Id));
        Assert.Contains(firstJob.Id, ex.Message);

        // 3. 重新分段请求也必须被互斥门禁阻断
        var rechunkEx = await Assert.ThrowsAsync<KnowledgeBaseJobConflictException>(() =>
            kbService.TriggerRechunkAsync(kbId));
        Assert.Contains(firstJob.Id, rechunkEx.Message);

        // 4. 当第一个作业结束并释放互斥位后，后续操作应能再次正常启动
        coordinator.EndJob(kbId, firstJob.Id);

        var secondJob = await kbService.TriggerSyncJobAsync(kbId, conn.Id);
        Assert.NotNull(secondJob);
        Assert.NotEqual(firstJob.Id, secondJob.Id);
    }
}
