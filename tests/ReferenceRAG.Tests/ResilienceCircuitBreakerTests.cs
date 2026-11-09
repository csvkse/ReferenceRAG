using System;
using System.Threading;
using ReferenceRAG.Infrastructure.Features.Resilience;
using Xunit;

namespace ReferenceRAG.Tests;

public class ResilienceCircuitBreakerTests
{
    [Fact]
    public void CircuitBreaker_StartsClosed_AndOpensAfterThreshold()
    {
        var cb = new ResilienceCircuitBreaker(failureThreshold: 3, breakDuration: TimeSpan.FromMilliseconds(200));

        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);

        cb.RecordFailure();
        Assert.Equal(CircuitState.Closed, cb.State);

        cb.RecordFailure();
        Assert.Equal(CircuitState.Closed, cb.State);

        // 第 3 次失败触发熔断
        cb.RecordFailure();
        Assert.Equal(CircuitState.Open, cb.State);
        Assert.True(cb.IsOpen);

        // 成功调用应重置
        cb.Reset();
        Assert.Equal(CircuitState.Closed, cb.State);
        Assert.False(cb.IsOpen);
    }

    [Fact]
    public void CircuitBreaker_TransitionsToHalfOpenAfterDuration()
    {
        var cb = new ResilienceCircuitBreaker(failureThreshold: 2, breakDuration: TimeSpan.FromMilliseconds(50));

        cb.RecordFailure();
        cb.RecordFailure();
        Assert.True(cb.IsOpen);

        Thread.Sleep(80);

        Assert.Equal(CircuitState.HalfOpen, cb.State);
        Assert.False(cb.IsOpen);

        // 半开探测成功，闭合断路器
        cb.RecordSuccess();
        Assert.Equal(CircuitState.Closed, cb.State);
    }
}
