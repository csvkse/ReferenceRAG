using System;
using System.Threading;

namespace ReferenceRAG.Infrastructure.Features.Resilience;

public enum CircuitState
{
    Closed,     // 正常闭合：所有请求正常放行
    Open,       // 故障熔断：请求直接短路降级
    HalfOpen    // 半开探测：允许单个请求试探恢复
}

/// <summary>
/// 线程安全三态断路器 (Closed / Open / HalfOpen)
/// 针对外部云端 API (SiliconFlow / OpenAI) 或模型端点，提供故障快速熔断与自动自愈试探。
/// </summary>
public sealed class ResilienceCircuitBreaker
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _breakDuration;
    private readonly object _syncRoot = new();

    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures = 0;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private DateTime _openedTime = DateTime.MinValue;

    public ResilienceCircuitBreaker(int failureThreshold = 3, TimeSpan? breakDuration = null)
    {
        _failureThreshold = Math.Max(1, failureThreshold);
        _breakDuration = breakDuration ?? TimeSpan.FromSeconds(30);
    }

    public CircuitState State
    {
        get
        {
            lock (_syncRoot)
            {
                if (_state == CircuitState.Open)
                {
                    if (DateTime.UtcNow - _openedTime >= _breakDuration)
                    {
                        _state = CircuitState.HalfOpen;
                    }
                }
                return _state;
            }
        }
    }

    /// <summary>
    /// 当前是否处于熔断开启（或非半开）状态，若是则应直接短路执行本地降级策略
    /// </summary>
    public bool IsOpen => State == CircuitState.Open;

    /// <summary>
    /// 记录一次调用成功
    /// </summary>
    public void RecordSuccess()
    {
        lock (_syncRoot)
        {
            _consecutiveFailures = 0;
            _state = CircuitState.Closed;
        }
    }

    /// <summary>
    /// 记录一次调用失败（如超时、网络断开、429 等）
    /// </summary>
    public void RecordFailure()
    {
        lock (_syncRoot)
        {
            _lastFailureTime = DateTime.UtcNow;
            _consecutiveFailures++;

            if (_state == CircuitState.HalfOpen || _consecutiveFailures >= _failureThreshold)
            {
                _state = CircuitState.Open;
                _openedTime = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// 重置断路器为闭合状态
    /// </summary>
    public void Reset()
    {
        lock (_syncRoot)
        {
            _state = CircuitState.Closed;
            _consecutiveFailures = 0;
        }
    }
}
