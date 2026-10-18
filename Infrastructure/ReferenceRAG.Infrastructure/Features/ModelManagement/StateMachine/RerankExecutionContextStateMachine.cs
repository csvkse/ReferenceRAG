using Microsoft.Extensions.Logging;
using ReferenceRAG.Core.Helpers;
using ReferenceRAG.Core.Interfaces;

namespace ReferenceRAG.Core.Features.ModelManagement.StateMachine;

/// <summary>
/// 重排推理执行上下文状态机（管理单次重排任务中前缀缓存、批量解码、打分与熔断降级流转）
/// </summary>
public sealed class RerankExecutionContextStateMachine
{
    private static readonly ILogger _logger = StaticLogger.GetLogger("RerankExecutionContextStateMachine");

    private readonly object _stateLock = new();
    private RerankExecutionState _currentState = RerankExecutionState.Idle;
    private int _consecutiveNanCount;
    private int _degradedExecutions;
    private Exception? _lastException;

    /// <summary>
    /// 触发熔断降级的执行次数
    /// </summary>
    public int DegradedExecutions
    {
        get
        {
            lock (_stateLock) return _degradedExecutions;
        }
    }

    /// <summary>
    /// 当前执行状态
    /// </summary>
    public RerankExecutionState CurrentState
    {
        get
        {
            lock (_stateLock) return _currentState;
        }
    }

    /// <summary>
    /// 连续遭遇的 NaN/算子异常计数
    /// </summary>
    public int ConsecutiveNanCount
    {
        get
        {
            lock (_stateLock) return _consecutiveNanCount;
        }
    }

    /// <summary>
    /// 最近一次发生的异常
    /// </summary>
    public Exception? LastException
    {
        get
        {
            lock (_stateLock) return _lastException;
        }
    }

    /// <summary>
    /// 状态流转事件
    /// </summary>
    public event EventHandler<(RerankExecutionState From, RerankExecutionState To, string? Reason)>? StateChanged;

    /// <summary>
    /// 当触发自适应熔断降级时触发
    /// </summary>
    public event EventHandler<string>? DegradedTriggered;

    /// <summary>
    /// 尝试流转到目标状态
    /// </summary>
    public bool TransitionTo(RerankExecutionState newState, string? reason = null)
    {
        RerankExecutionState from;
        lock (_stateLock)
        {
            if (_currentState == newState) return true;
            from = _currentState;
            _currentState = newState;

            if (newState == RerankExecutionState.Idle)
            {
                _consecutiveNanCount = 0;
            }
            else if (newState == RerankExecutionState.Degraded)
            {
                _degradedExecutions++;
            }
        }

        _logger.LogDebug("[RerankStateMachine] 状态流转: {From} -> {To} (Reason: {Reason})", from, newState, reason ?? "Normal");
        StateChanged?.Invoke(this, (from, newState, reason));

        if (newState == RerankExecutionState.Degraded)
        {
            DegradedTriggered?.Invoke(this, reason ?? "Automatic degradation triggered");
        }

        return true;
    }

    /// <summary>
    /// 记录遇到有效分数（重置连续 NaN 计数器）
    /// </summary>
    public void RecordValidScore()
    {
        lock (_stateLock)
        {
            _consecutiveNanCount = 0;
        }
    }

    /// <summary>
    /// 记录遇到 NaN/异常分数。若连续达到阈值（默认 2 次），自动熔断流转至 Degraded 状态
    /// </summary>
    public bool RecordAnomalyScore(string? details = null, int threshold = 2)
    {
        bool shouldDegrade = false;
        lock (_stateLock)
        {
            _consecutiveNanCount++;
            if (_consecutiveNanCount >= threshold && _currentState != RerankExecutionState.Degraded)
            {
                shouldDegrade = true;
            }
        }

        if (shouldDegrade)
        {
            TransitionTo(RerankExecutionState.Degraded, details ?? $"连续 {_consecutiveNanCount} 次检测到 NaN 算子异常");
            return true;
        }

        return false;
    }

    /// <summary>
    /// 记录执行期间抛出的未捕获异常并熔断降级
    /// </summary>
    public void RecordExecutionFault(Exception ex, string? context = null)
    {
        lock (_stateLock)
        {
            _lastException = ex;
        }
        _logger.LogWarning(ex, "[RerankStateMachine] 执行期间发生异常，自适应切入 Degraded 状态: {Context}", context);
        TransitionTo(RerankExecutionState.Degraded, ex.Message);
    }

    /// <summary>
    /// 重置状态机回 Idle 闲置准备态
    /// </summary>
    public void Reset()
    {
        lock (_stateLock)
        {
            _currentState = RerankExecutionState.Idle;
            _consecutiveNanCount = 0;
            _lastException = null;
        }
    }

    /// <summary>
    /// 生成符合召回先验顺序的平滑保底评分列表
    /// </summary>
    public static List<RerankDocument> GenerateFallbackDocuments(
        IReadOnlyList<string> documents,
        int startIndex = 0)
    {
        var total = documents.Count;
        var list = new List<RerankDocument>(total);

        for (int i = 0; i < total; i++)
        {
            var rawIndex = startIndex + i;
            var step = total > 1 ? 0.35 / total : 0.0;
            var score = Math.Clamp(0.85 - (rawIndex * step), 0.10, 0.99);

            list.Add(new RerankDocument
            {
                Index = rawIndex,
                Document = documents[i],
                RelevanceScore = score
            });
        }

        return list;
    }
}
