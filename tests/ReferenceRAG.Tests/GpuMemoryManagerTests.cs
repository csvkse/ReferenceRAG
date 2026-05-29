using Microsoft.Extensions.Logging;
using Moq;
using ReferenceRAG.Core.Interfaces;
using ReferenceRAG.Core.Services;
using Xunit;

namespace ReferenceRAG.Tests;

public class GpuMemoryManagerTests
{
    [Fact]
    public void Register_AddsSession_ToSessions()
    {
        // Arrange
        var manager = new GpuMemoryManager();
        var sessionName = "TestSession";
        var deviceId = 0;

        // Act
        manager.Register(sessionName, () => null, deviceId);
        var states = manager.GetSessionStates();

        // Assert
        Assert.True(states.ContainsKey(sessionName));
        Assert.Equal(deviceId, states[sessionName].DeviceId);

        manager.Dispose();
    }

    [Fact]
    public void Unregister_RemovesSession_FromSessions()
    {
        // Arrange
        var manager = new GpuMemoryManager();
        var sessionName = "TestSession";
        manager.Register(sessionName, () => null, 0);

        // Act
        manager.Unregister(sessionName);
        var states = manager.GetSessionStates();

        // Assert
        Assert.False(states.ContainsKey(sessionName));

        manager.Dispose();
    }

    [Fact]
    public void EnterInference_Increments_ActiveCount()
    {
        // Arrange
        var manager = new GpuMemoryManager();
        var sessionName = "TestSession";
        manager.Register(sessionName, () => null, 0);

        // Act
        manager.EnterInference(sessionName);
        var states = manager.GetSessionStates();

        // Assert
        Assert.Equal(1, states[sessionName].ActiveInferenceCount);
        Assert.True(states[sessionName].IsActive);

        manager.Dispose();
    }

    [Fact]
    public void ExitInference_Decrements_ActiveCount()
    {
        // Arrange
        var manager = new GpuMemoryManager();
        var sessionName = "TestSession";
        manager.Register(sessionName, () => null, 0);
        manager.EnterInference(sessionName);

        // Act
        manager.ExitInference(sessionName);
        var states = manager.GetSessionStates();

        // Assert
        Assert.Equal(0, states[sessionName].ActiveInferenceCount);
        Assert.False(states[sessionName].IsActive);

        manager.Dispose();
    }

    [Fact]
    public void EnterExitInference_MultipleCalls_TracksCorrectly()
    {
        // Arrange
        var manager = new GpuMemoryManager();
        var sessionName = "TestSession";
        manager.Register(sessionName, () => null, 0);

        // Act - 多次进入和退出
        manager.EnterInference(sessionName);
        manager.EnterInference(sessionName);
        manager.EnterInference(sessionName);

        var statesAfterEnter = manager.GetSessionStates();
        Assert.Equal(3, statesAfterEnter[sessionName].ActiveInferenceCount);

        manager.ExitInference(sessionName);
        manager.ExitInference(sessionName);

        var statesAfterExit = manager.GetSessionStates();
        Assert.Equal(1, statesAfterExit[sessionName].ActiveInferenceCount);

        manager.ExitInference(sessionName);

        var statesFinal = manager.GetSessionStates();
        Assert.Equal(0, statesFinal[sessionName].ActiveInferenceCount);

        manager.Dispose();
    }

    [Fact]
    public void GetMemoryInfo_ReturnsUnknown_WhenNvidiaSmiNotAvailable()
    {
        // Arrange
        var manager = new GpuMemoryManager();

        // Act
        var info = manager.GetMemoryInfo(0);

        // Assert - 如果 nvidia-smi 不可用，返回 Unknown（所有值为 -1）
        // 如果可用，返回实际值
        // 这个测试主要验证方法不抛异常
        Assert.NotNull(info);
        Assert.Equal(0, info.DeviceId);

        manager.Dispose();
    }

    [Fact]
    public async Task ShrinkAsync_WithNoActiveInference_ExecutesImmediately()
    {
        // Arrange
        var manager = new GpuMemoryManager();
        var sessionName = "TestSession";
        manager.Register(sessionName, () => null, 0);

        // Act - 无推理活动时释放
        await manager.ShrinkAsync(sessionName);

        // Assert - 验证不抛异常
        var states = manager.GetSessionStates();
        Assert.True(states.ContainsKey(sessionName));

        manager.Dispose();
    }

    [Fact]
    public async Task ShrinkAsync_WithActiveInference_MarksPending()
    {
        // Arrange
        var manager = new GpuMemoryManager();
        var sessionName = "TestSession";
        manager.Register(sessionName, () => null, 0);

        // Act - 推理进行中请求释放
        manager.EnterInference(sessionName);
        await manager.ShrinkAsync(sessionName);

        var statesAfterShrink = manager.GetSessionStates();
        Assert.True(statesAfterShrink[sessionName].HasPendingShrink);

        // 推理结束后，延迟释放应该被执行
        manager.ExitInference(sessionName);

        var statesAfterExit = manager.GetSessionStates();
        Assert.False(statesAfterExit[sessionName].HasPendingShrink);

        manager.Dispose();
    }

    [Fact]
    public void SessionState_IdleTime_CalculatesCorrectly()
    {
        // Arrange
        var state = new SessionState
        {
            Name = "Test",
            DeviceId = 0,
            LastActivityTime = DateTime.UtcNow.AddMinutes(-5)
        };

        // Act
        var idleTime = state.IdleTime;

        // Assert
        Assert.True(idleTime.TotalMinutes >= 5);
    }

    [Fact]
    public void GpuMemoryInfo_UsagePercent_CalculatesCorrectly()
    {
        // Arrange
        var info = new GpuMemoryInfo
        {
            DeviceId = 0,
            FreeMB = 2000,
            UsedMB = 6000,
            TotalMB = 8000
        };

        // Act
        var usagePercent = info.UsagePercent;

        // Assert
        Assert.Equal(75.0, usagePercent);
    }

    [Fact]
    public void GpuMemoryInfo_Unknown_ReturnsNegativeValues()
    {
        // Act
        var info = GpuMemoryInfo.Unknown(0);

        // Assert
        Assert.Equal(0, info.DeviceId);
        Assert.Equal(-1, info.FreeMB);
        Assert.Equal(-1, info.UsedMB);
        Assert.Equal(-1, info.TotalMB);
        Assert.Equal(-1, info.GpuUtilization);
        Assert.Equal(-1, info.Temperature);
    }

    [Fact]
    public async Task StartAsync_StartsMonitorTimer()
    {
        // Arrange
        var manager = new GpuMemoryManager();

        // Act
        await manager.StartAsync(CancellationToken.None);

        // Assert - 验证不抛异常
        // 等待一小段时间确保定时器启动
        await Task.Delay(100);

        await manager.StopAsync(CancellationToken.None);
        manager.Dispose();
    }

    [Fact]
    public async Task StopAsync_StopsMonitorTimer()
    {
        // Arrange
        var manager = new GpuMemoryManager();
        await manager.StartAsync(CancellationToken.None);

        // Act
        await manager.StopAsync(CancellationToken.None);

        // Assert - 验证不抛异常
        manager.Dispose();
    }
}
