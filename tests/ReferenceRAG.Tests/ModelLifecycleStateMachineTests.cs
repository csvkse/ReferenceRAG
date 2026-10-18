using System;
using System.Threading.Tasks;
using Moq;
using ReferenceRAG.Core.Features.ModelManagement.StateMachine;
using Xunit;

namespace ReferenceRAG.Tests;

public class ModelLifecycleStateMachineTests
{
    [Fact]
    public async Task IdleTimeout_Unloads_WhenIdlePeriodExpires()
    {
        var mockAdapter = new Mock<IDisposable>();
        var machine = new ModelLifecycleStateMachine<IDisposable>(
            "TestModel",
            memoryManager: null,
            idleTimeout: TimeSpan.FromMilliseconds(100));

        await using (machine)
        {
            var loaded = await machine.TwoPhaseReloadAsync(() => Task.FromResult(mockAdapter.Object));
            Assert.True(loaded);
            Assert.Equal(ModelState.Ready, machine.State);

            // 等待超过空闲超时时间 (100ms)
            await Task.Delay(250);

            Assert.Equal(ModelState.Unloaded, machine.State);
            mockAdapter.Verify(m => m.Dispose(), Times.Once);
        }
    }

    [Fact]
    public async Task IdleTimeout_WithActiveLease_CompensatesWatchdog_AndUnloadsOnlyAfterLeaseReleased()
    {
        var mockAdapter = new Mock<IDisposable>();
        var machine = new ModelLifecycleStateMachine<IDisposable>(
            "TestModel",
            memoryManager: null,
            idleTimeout: TimeSpan.FromMilliseconds(150));

        await using (machine)
        {
            var loaded = await machine.TwoPhaseReloadAsync(() => Task.FromResult(mockAdapter.Object));
            Assert.True(loaded);
            Assert.Equal(ModelState.Ready, machine.State);

            // 占用借用模拟在途推理
            var lease = machine.AcquireLease();
            Assert.Equal(ModelState.Inferring, machine.State);
            Assert.Equal(1, machine.ActiveLeases);

            // 等待超过第一次 150ms 触发点：因在途借用，看门狗应补偿延后，不能发生卸载
            await Task.Delay(250);
            Assert.Equal(ModelState.Inferring, machine.State);
            Assert.Equal(1, machine.ActiveLeases);
            mockAdapter.Verify(m => m.Dispose(), Times.Never);

            // 归还借用
            lease.Dispose();
            Assert.Equal(0, machine.ActiveLeases);
            Assert.Equal(ModelState.Ready, machine.State);

            // 归还后看门狗/滑动定时器到期，自动卸载
            await Task.Delay(250);
            Assert.Equal(ModelState.Unloaded, machine.State);
            mockAdapter.Verify(m => m.Dispose(), Times.Once);
        }
    }

    [Fact]
    public async Task PermanentResidentModel_DoesNotUnload_WhenIdleTimeoutIsNull()
    {
        var mockAdapter = new Mock<IDisposable>();
        var machine = new ModelLifecycleStateMachine<IDisposable>(
            "PermanentModel",
            memoryManager: null,
            idleTimeout: null);

        await using (machine)
        {
            var loaded = await machine.TwoPhaseReloadAsync(() => Task.FromResult(mockAdapter.Object));
            Assert.True(loaded);
            Assert.Equal(ModelState.Ready, machine.State);

            await Task.Delay(200);

            Assert.Equal(ModelState.Ready, machine.State);
            mockAdapter.Verify(m => m.Dispose(), Times.Never);
        }
    }
}
